using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeuTaskBar
{
    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    // Vista previa de las ventanas de una app al pasar el ratón por su icono (miniaturas en vivo de DWM),
    // con el aspecto del flyout de la barra de tareas de Windows 11 (esquema claro/oscuro del sistema).
    sealed class ThumbFlyout
    {
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

        const string ClassName = "NeuTaskBar.Flyout";
        const string TextFont = "Segoe UI Variable Text";
        const string IconFont = "Segoe Fluent Icons";
        const uint T_SHOW = 1, T_HIDE = 2, T_ANIM = 3;
        const int ShowDelayMs = 350, HideDelayMs = 280, MaxCards = 16;
        static ThumbFlyout self;
        static readonly WndProcDelegate Proc = StaticProc;

        sealed class Card
        {
            public IntPtr Hwnd, Thumb;
            public string Title = "";
            public bool Iconic;
            public RECT Box, ThumbArea, Close;
        }

        public Action<IntPtr> OnActivate, OnClose;
        public int Dpi = 96;
        public RECT Monitor;
        public int AnchorY;     // y de pantalla de la parte superior de las islas

        IntPtr hwnd;
        AppItem current, pending;
        RECT pendingRect, lastRect;
        readonly List<Card> cards = new List<Card>();
        readonly Gfx.AlphaSurface surface = new Gfx.AlphaSurface();
        int w, h, baseX, baseY;
        Ease slide;
        bool visible, tracking, animating;
        int hoverCard = -1;
        bool hoverClose;
        long lastTick;
        int appliedTheme = -1;   // 0 oscuro, 1 claro

        public bool Visible { get { return visible; } }

        int S(int v) { return (int)Math.Round(v * Dpi / 96.0); }

        public void Init()
        {
            self = this;
            var wc = new WNDCLASS();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc);
            wc.hInstance = Native.GetModuleHandleW(null);
            wc.hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512));
            wc.lpszClassName = ClassName;
            Native.RegisterClassW(ref wc);
            hwnd = Native.CreateWindowExW(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, ClassName,
                "NeuTaskBar", Native.WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ tema del sistema

        static bool SystemUsesLightTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        bool light;

        void ApplyTheme()
        {
            light = SystemUsesLightTheme();
            int t = light ? 1 : 0;
            if (t == appliedTheme) return;
            appliedTheme = t;
            // Acrílico como el de los paneles de Windows 11: claro #F3F3F3 u oscuro #2C2C2C, translúcido.
            Island.ApplyAcrylic(hwnd, light ? 0xCCF3F3F3u : 0xCC2C2C2Cu, !light, false);
        }

        // ------------------------------------------------------------------ control desde la isla

        public void Hover(AppItem item, RECT cellRect)
        {
            if (item == null || !item.Running) { HoverEnd(); return; }
            Native.KillTimer(hwnd, new UIntPtr(T_HIDE));
            if (visible)
            {
                if (current == null || current.Id != item.Id) Present(item, cellRect);
                else lastRect = cellRect;
            }
            else
            {
                pending = item;
                pendingRect = cellRect;
                Native.SetTimer(hwnd, new UIntPtr(T_SHOW), ShowDelayMs, IntPtr.Zero);
            }
        }

        public void HoverEnd()
        {
            Native.KillTimer(hwnd, new UIntPtr(T_SHOW));
            pending = null;
            if (visible) Native.SetTimer(hwnd, new UIntPtr(T_HIDE), HideDelayMs, IntPtr.Zero);
        }

        public void HideNow()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.KillTimer(hwnd, new UIntPtr(T_SHOW));
            Native.KillTimer(hwnd, new UIntPtr(T_HIDE));
            Native.KillTimer(hwnd, new UIntPtr(T_ANIM));
            animating = false;
            pending = null;
            current = null;
            hoverCard = -1;
            ClearThumbs();
            cards.Clear();
            if (visible) Native.ShowWindow(hwnd, Native.SW_HIDE);
            visible = false;
        }

        // Mantiene la vista previa al día cuando cambian las ventanas de la app.
        public void UpdateItems(List<AppItem> items)
        {
            if (!visible || current == null) return;
            AppItem it = null;
            foreach (var i in items) if (i.Id == current.Id) { it = i; break; }
            if (it == null || !it.Running) { HideNow(); return; }
            bool same = it.Windows.Count == cards.Count || (it.Windows.Count > MaxCards && cards.Count == MaxCards);
            if (same)
            {
                for (int i = 0; i < cards.Count; i++) if (!it.Windows.Contains(cards[i].Hwnd)) { same = false; break; }
            }
            current = it;
            if (!same) Present(it, lastRect);
            else
            {
                bool changed = false;
                foreach (var c in cards)
                {
                    string t = Native.GetWindowText(c.Hwnd);
                    if (t != c.Title) { c.Title = t; changed = true; }
                }
                if (changed) Invalidate();
            }
        }

        public void Destroy()
        {
            HideNow();
            surface.Free();
            if (hwnd != IntPtr.Zero) { Native.DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
        }

        // ------------------------------------------------------------------ construcción

        void Present(AppItem item, RECT cellRect)
        {
            ClearThumbs();
            cards.Clear();
            current = item;
            lastRect = cellRect;
            foreach (var hw in item.Windows)
            {
                if (cards.Count >= MaxCards) break;
                cards.Add(new Card { Hwnd = hw, Title = Native.GetWindowText(hw), Iconic = Native.IsIconic(hw) });
            }
            if (cards.Count == 0) { HideNow(); return; }
            ApplyTheme();

            // Medidas del flyout de Windows 11 (a 96 dpi).
            int pad = S(8), gap = S(4), cardW = S(204), inner = S(6), headerH = S(28), thumbW = cardW - inner * 2, thumbH = S(108);
            int cardH = inner + headerH + thumbH + inner;
            int maxCols = Math.Max(1, (Monitor.Width - S(40) - pad * 2 + gap) / (cardW + gap));
            int cols = Math.Min(cards.Count, maxCols);
            int rows = (cards.Count + cols - 1) / cols;
            w = pad * 2 + cols * cardW + (cols - 1) * gap;
            h = pad * 2 + rows * cardH + (rows - 1) * gap;

            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                int cx = pad + (i % cols) * (cardW + gap), cy = pad + (i / cols) * (cardH + gap);
                c.Box = new RECT(cx, cy, cx + cardW, cy + cardH);
                c.ThumbArea = new RECT(cx + inner, cy + inner + headerH, cx + inner + thumbW, cy + inner + headerH + thumbH);
                int cs = S(24);
                c.Close = new RECT(cx + cardW - inner - cs, cy + inner + (headerH - cs) / 2, cx + cardW - inner, cy + inner + (headerH - cs) / 2 + cs);
            }

            int centerX = (cellRect.Left + cellRect.Right) / 2;
            baseX = Math.Max(Monitor.Left + S(10), Math.Min(centerX - w / 2, Monitor.Right - w - S(10)));
            baseY = AnchorY - S(8) - h;

            bool was = visible;
            int y = was ? baseY + (int)slide.V : baseY + S(12);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, baseX, y, w, h,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            visible = true;
            RegisterThumbs();
            if (!was)
            {
                slide = new Ease(S(12));
                slide.T = 0;
                StartAnim();
            }
            Invalidate();
        }

        void RegisterThumbs()
        {
            foreach (var c in cards)
            {
                if (c.Iconic) continue;
                IntPtr t;
                if (DwmRegisterThumbnail(hwnd, c.Hwnd, out t) != 0 || t == IntPtr.Zero) { c.Iconic = true; continue; }
                c.Thumb = t;
                SIZE src;
                if (DwmQueryThumbnailSourceSize(t, out src) != 0 || src.cx <= 0 || src.cy <= 0) src = new SIZE { cx = 16, cy = 9 };
                int aw = c.ThumbArea.Width, ah = c.ThumbArea.Height;
                double k = Math.Min((double)aw / src.cx, (double)ah / src.cy);
                int dw = (int)(src.cx * k), dh = (int)(src.cy * k);
                int dx = c.ThumbArea.Left + (aw - dw) / 2, dy = c.ThumbArea.Top + (ah - dh) / 2;
                var props = new DWM_THUMBNAIL_PROPERTIES();
                props.dwFlags = 0x1 | 0x4 | 0x8 | 0x10; // destino, opacidad, visible, solo área cliente
                props.rcDestination = new RECT(dx, dy, dx + dw, dy + dh);
                props.opacity = 255;
                props.fVisible = 1;
                props.fSourceClientAreaOnly = 0;
                DwmUpdateThumbnailProperties(t, ref props);
            }
        }

        void ClearThumbs()
        {
            foreach (var c in cards)
                if (c.Thumb != IntPtr.Zero) { DwmUnregisterThumbnail(c.Thumb); c.Thumb = IntPtr.Zero; }
        }

        void StartAnim()
        {
            if (animating) return;
            animating = true;
            lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            Native.SetTimer(hwnd, new UIntPtr(T_ANIM), 15, IntPtr.Zero);
        }

        void Invalidate() { if (hwnd != IntPtr.Zero) Native.InvalidateRect(hwnd, IntPtr.Zero, false); }

        // ------------------------------------------------------------------ ventana

        static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var f = self;
            if (f == null || f.hwnd == IntPtr.Zero) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
            try { return f.WndProc(msg, wParam, lParam); }
            catch (Exception ex) { Diag.Log(ex); return Native.DefWindowProcW(hwnd, msg, wParam, lParam); }
        }

        IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_ERASEBKGND: return new IntPtr(1);
                case Native.WM_MOUSEACTIVATE: return new IntPtr(3);
                case Native.WM_PAINT: Paint(); return IntPtr.Zero;
                case Native.WM_TIMER: OnTimer((uint)wParam.ToInt64()); return IntPtr.Zero;
                case Native.WM_MOUSEMOVE:
                    {
                        Native.KillTimer(hwnd, new UIntPtr(T_HIDE));
                        if (!tracking)
                        {
                            var t = new TRACKMOUSEEVENT();
                            t.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
                            t.dwFlags = Native.TME_LEAVE;
                            t.hwndTrack = hwnd;
                            Native.TrackMouseEvent(ref t);
                            tracking = true;
                        }
                        int x = Native.LoWord(lParam), y = Native.HiWord(lParam);
                        int hc = HitCard(x, y);
                        bool cl = hc >= 0 && cards[hc].Close.Contains(x, y);
                        if (hc != hoverCard || cl != hoverClose) { hoverCard = hc; hoverClose = cl; Invalidate(); }
                        return IntPtr.Zero;
                    }
                case Native.WM_MOUSELEAVE:
                    tracking = false;
                    hoverCard = -1;
                    hoverClose = false;
                    Invalidate();
                    HoverEnd();
                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP:
                    {
                        int x = Native.LoWord(lParam), y = Native.HiWord(lParam);
                        int hc = HitCard(x, y);
                        if (hc < 0) return IntPtr.Zero;
                        IntPtr target = cards[hc].Hwnd;
                        if (cards[hc].Close.Contains(x, y)) { if (OnClose != null) OnClose(target); }
                        else { HideNow(); if (OnActivate != null) OnActivate(target); }
                        return IntPtr.Zero;
                    }
                case Native.WM_MBUTTONUP:
                    {
                        int hc = HitCard(Native.LoWord(lParam), Native.HiWord(lParam));
                        if (hc >= 0 && OnClose != null) OnClose(cards[hc].Hwnd);
                        return IntPtr.Zero;
                    }
            }
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        int HitCard(int x, int y)
        {
            for (int i = 0; i < cards.Count; i++) if (cards[i].Box.Contains(x, y)) return i;
            return -1;
        }

        void OnTimer(uint id)
        {
            switch (id)
            {
                case T_SHOW:
                    Native.KillTimer(hwnd, new UIntPtr(T_SHOW));
                    if (pending != null) { var it = pending; pending = null; Present(it, pendingRect); }
                    break;
                case T_HIDE:
                    Native.KillTimer(hwnd, new UIntPtr(T_HIDE));
                    HideNow();
                    break;
                case T_ANIM:
                    {
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        float dt = (float)((now - lastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
                        lastTick = now;
                        if (dt > 0.1f) dt = 0.1f;
                        bool more = slide.Step(dt, 22f, 0.5f);
                        Native.SetWindowPos(hwnd, IntPtr.Zero, baseX, baseY + (int)Math.Round(slide.V), 0, 0,
                            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                        if (!more) { Native.KillTimer(hwnd, new UIntPtr(T_ANIM)); animating = false; }
                        break;
                    }
            }
        }

        // ------------------------------------------------------------------ dibujo (GDI+ con alfa sobre el acrílico)

        void Paint()
        {
            PAINTSTRUCT ps;
            IntPtr dc = Native.BeginPaint(hwnd, out ps);
            RECT rc;
            Native.GetClientRect(hwnd, out rc);
            if (rc.Width > 0 && rc.Height > 0 && current != null)
            {
                surface.Resize(dc, rc.Width, rc.Height);
                IntPtr g = surface.BeginGraphics();
                Gfx.Clear(g, 0x00000000);
                DrawContent(g);
                Gfx.End(g);
                surface.Present(dc);
            }
            Native.EndPaint(hwnd, ref ps);
        }

        void DrawContent(IntPtr g)
        {
            int ink = light ? 0 : 255;                     // color del texto y de los velos
            uint text = Gfx.Argb(1f, light ? 26 : 255, light ? 26 : 255, light ? 26 : 255);
            float cardRadius = S(6);
            int inner = S(6), iconS = S(16);

            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                bool hov = i == hoverCard;
                if (hov) Gfx.FillRound(g, c.Box.Left, c.Box.Top, c.Box.Width, c.Box.Height, cardRadius, Gfx.Argb(light ? 0.06f : 0.09f, ink, ink, ink));

                // Miniatura: marco fino y fondo para las ventanas sin vista previa.
                Gfx.FillRound(g, c.ThumbArea.Left - 1, c.ThumbArea.Top - 1, c.ThumbArea.Width + 2, c.ThumbArea.Height + 2, S(4),
                    Gfx.Argb(light ? 0.12f : 0.16f, ink, ink, ink));
                Gfx.FillRound(g, c.ThumbArea.Left, c.ThumbArea.Top, c.ThumbArea.Width, c.ThumbArea.Height, S(3),
                    light ? Gfx.Argb(0.9f, 252, 252, 252) : Gfx.Argb(0.85f, 32, 32, 32));

                if (current != null && current.Image != null)
                {
                    Gfx.DrawImage(g, current.Image, c.Box.Left + inner, c.Box.Top + inner + (S(28) - iconS) / 2f, iconS, iconS, 1f);
                    if (c.Iconic || c.Thumb == IntPtr.Zero)
                    {
                        float big = S(48);
                        Gfx.DrawImage(g, current.Image, c.ThumbArea.Left + (c.ThumbArea.Width - big) / 2f,
                            c.ThumbArea.Top + (c.ThumbArea.Height - big) / 2f, big, big, 1f);
                    }
                }

                float tx = c.Box.Left + inner + iconS + S(8);
                float tr = c.Close.Left - S(4);
                Gfx.Text(g, c.Title, TextFont, S(12), false, text, tx, c.Box.Top + inner, tr - tx, S(28), 0);

                if (hov)
                {
                    if (hoverClose)
                    {
                        Gfx.FillRound(g, c.Close.Left, c.Close.Top, c.Close.Width, c.Close.Height, S(5), Gfx.Argb(1f, 196, 43, 28));
                        Gfx.Text(g, "", IconFont, S(10), false, Gfx.Argb(1f, 255, 255, 255), c.Close.Left, c.Close.Top, c.Close.Width, c.Close.Height, 1);
                    }
                    else
                    {
                        Gfx.Text(g, "", IconFont, S(10), false, text, c.Close.Left, c.Close.Top, c.Close.Width, c.Close.Height, 1);
                    }
                }
            }
        }
    }
}
