using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeuTaskBar
{
    enum MenuKind { Item, Header, Separator }

    sealed class MenuDef
    {
        public MenuKind Kind;
        public string Text = "";
        public IconImage Image;       // icono del elemento (no es propiedad del menú si OwnsImage es false)
        public bool OwnsImage;
        public string Glyph;          // alternativa: glifo de Segoe Fluent Icons
        public Action Action;

        public static MenuDef Item(string text, Action action, string glyph) { return new MenuDef { Kind = MenuKind.Item, Text = text, Action = action, Glyph = glyph }; }
        public static MenuDef Item(string text, Action action, IconImage image) { return new MenuDef { Kind = MenuKind.Item, Text = text, Action = action, Image = image }; }
        public static MenuDef Header(string text) { return new MenuDef { Kind = MenuKind.Header, Text = text }; }
        public static MenuDef Separator() { return new MenuDef { Kind = MenuKind.Separator }; }
    }

    static class Theme
    {
        public static bool SystemLight()
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
    }

    // Menú contextual de la barra con las medidas y el aspecto del menú de la barra de tareas de Windows 11.
    sealed class TaskbarMenu
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

        const string ClassName = "NeuTaskBar.Menu";
        const string TextFont = "Segoe UI";
        const string IconFont = "Segoe Fluent Icons";
        const uint T_ANIM = 1;
        static TaskbarMenu self;
        static readonly WndProcDelegate Proc = StaticProc;

        public int Dpi = 96;
        public RECT Monitor;
        public Edge Edge = Edge.Bottom;

        IntPtr hwnd;
        List<MenuDef> items = new List<MenuDef>();
        readonly List<RECT> rows = new List<RECT>();
        readonly Gfx.AlphaSurface surface = new Gfx.AlphaSurface();
        int w, h, baseX, baseY, hover = -1;
        bool visible, tracking, animating, light;
        int appliedTheme = -1;
        Ease slide;
        long lastTick;

        public bool Visible { get { return visible; } }

        // Escala fraccionaria (tamaños tipográficos).
        float F(float v) { return v * Dpi / 96f; }
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
            hwnd = Native.CreateWindowExW(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW, ClassName, "NeuTaskBar",
                Native.WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        public static IconImage LoadIcon(string file, int index, int px)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file)) return null;
                var icons = new IntPtr[1];
                var ids = new int[1];
                uint n = PrivateExtractIconsW(file, index, px, px, icons, ids, 1, 0);
                if (n == 0 || n == 0xFFFFFFFF || icons[0] == IntPtr.Zero) return null;
                var img = Gfx.FromHicon(icons[0]);
                Native.DestroyIcon(icons[0]);
                return img;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ mostrar / cerrar

        // pointX/pointY: punto de pantalla sobre el que se centra el menú (a lo largo de la barra);
        // anchor: lado de las islas que mira al centro de la pantalla.
        public void Show(List<MenuDef> defs, int pointX, int pointY, int anchor)
        {
            Close();
            items = defs;
            light = Theme.SystemLight();
            int t = light ? 1 : 0;
            if (t != appliedTheme) { appliedTheme = t; Island.ApplyAcrylic(hwnd, light ? 0xCCF3F3F3u : 0xCC2C2C2Cu, !light, false); }

            int padX = S(16), iconS = S(16), gap = S(12), rowH = S(32), sepH = S(9), padY = S(4);
            float maxText = 0;
            foreach (var d in items)
            {
                if (d.Kind == MenuKind.Separator || string.IsNullOrEmpty(d.Text)) continue;
                float tw = Gfx.MeasureText(d.Text, TextFont, d.Kind == MenuKind.Header ? F(12) : F(13.3f), false);
                if (tw > maxText) maxText = tw;
            }
            w = (int)Math.Min(S(294), Math.Max(S(200), maxText + padX * 2 + iconS + gap));
            rows.Clear();
            int y = padY;
            foreach (var d in items)
            {
                int hh = d.Kind == MenuKind.Separator ? sepH : rowH;
                rows.Add(new RECT(0, y, w, y + hh));
                y += hh;
            }
            h = y + padY;

            int minX = Monitor.Left + S(12), maxX = Math.Max(minX, Monitor.Right - w - S(12));
            int minY = Monitor.Top + S(12), maxY = Math.Max(minY, Monitor.Bottom - h - S(12));
            switch (Edge)
            {
                case Edge.Top:
                    baseX = Math.Max(minX, Math.Min(pointX - w / 2, maxX));
                    baseY = anchor + S(12);
                    break;
                case Edge.Left:
                    baseX = Math.Min(anchor + S(12), maxX);
                    baseY = Math.Max(minY, Math.Min(pointY - h / 2, maxY));
                    break;
                case Edge.Right:
                    baseX = Math.Max(minX, anchor - S(12) - w);
                    baseY = Math.Max(minY, Math.Min(pointY - h / 2, maxY));
                    break;
                default:
                    baseX = Math.Max(minX, Math.Min(pointX - w / 2, maxX));
                    baseY = anchor - S(12) - h;
                    break;
            }
            hover = -1;
            slide = new Ease(S(12));
            slide.T = 0;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, baseX + SlideDx * S(12), baseY + SlideDy * S(12), w, h, Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            visible = true;
            Native.SetForegroundWindow(hwnd);
            StartAnim();
            Native.InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        // Dirección en la que se desliza el menú al aparecer: desde el borde de pantalla hacia su posición.
        int SlideDx { get { return Edge == Edge.Left ? -1 : Edge == Edge.Right ? 1 : 0; } }
        int SlideDy { get { return Edge == Edge.Top ? -1 : Edge == Edge.Bottom ? 1 : 0; } }

        public void Close()
        {
            if (hwnd == IntPtr.Zero || !visible) { FreeImages(); return; }
            Native.KillTimer(hwnd, new UIntPtr(T_ANIM));
            animating = false;
            visible = false;
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            FreeImages();
        }

        void FreeImages()
        {
            foreach (var d in items) if (d.OwnsImage && d.Image != null) { Gfx.Dispose(d.Image); d.Image = null; }
            items = new List<MenuDef>();
        }

        public void Destroy()
        {
            Close();
            surface.Free();
            if (hwnd != IntPtr.Zero) { Native.DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
        }

        void StartAnim()
        {
            if (animating) return;
            animating = true;
            lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            Native.SetTimer(hwnd, new UIntPtr(T_ANIM), 15, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ ventana

        static IntPtr StaticProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var m = self;
            if (m == null || m.hwnd == IntPtr.Zero) return Native.DefWindowProcW(h, msg, wParam, lParam);
            try { return m.WndProc(msg, wParam, lParam); }
            catch (Exception ex) { Diag.Log(ex); return Native.DefWindowProcW(h, msg, wParam, lParam); }
        }

        int HitRow(int x, int y)
        {
            for (int i = 0; i < rows.Count; i++)
                if (items[i].Kind == MenuKind.Item && rows[i].Contains(x, y)) return i;
            return -1;
        }

        void Invoke(int index)
        {
            if (index < 0 || index >= items.Count) return;
            var action = items[index].Action;
            Close();
            if (action != null) action();
        }

        void Move(int dir)
        {
            if (items.Count == 0) return;
            int i = hover;
            for (int n = 0; n < items.Count; n++)
            {
                i = i < 0 ? (dir > 0 ? 0 : items.Count - 1) : (i + dir + items.Count) % items.Count;
                if (items[i].Kind == MenuKind.Item) { hover = i; Native.InvalidateRect(hwnd, IntPtr.Zero, false); return; }
            }
        }

        IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_ERASEBKGND: return new IntPtr(1);
                case Native.WM_PAINT: Paint(); return IntPtr.Zero;
                case 0x0006: // WM_ACTIVATE: al perder el foco se cierra, como los menús de Windows
                    if ((wParam.ToInt64() & 0xFFFF) == 0 && visible) Close();
                    return IntPtr.Zero;
                case Native.WM_TIMER:
                    {
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        float dt = (float)((now - lastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
                        lastTick = now;
                        if (dt > 0.1f) dt = 0.1f;
                        bool more = slide.Step(dt, 22f, 0.5f);
                        int off = (int)Math.Round(slide.V);
                        Native.SetWindowPos(hwnd, IntPtr.Zero, baseX + SlideDx * off, baseY + SlideDy * off, 0, 0,
                            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                        if (!more) { Native.KillTimer(hwnd, new UIntPtr(T_ANIM)); animating = false; }
                        return IntPtr.Zero;
                    }
                case Native.WM_MOUSEMOVE:
                    {
                        if (!tracking)
                        {
                            var t = new TRACKMOUSEEVENT();
                            t.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
                            t.dwFlags = Native.TME_LEAVE;
                            t.hwndTrack = hwnd;
                            Native.TrackMouseEvent(ref t);
                            tracking = true;
                        }
                        int hr = HitRow(Native.LoWord(lParam), Native.HiWord(lParam));
                        if (hr != hover) { hover = hr; Native.InvalidateRect(hwnd, IntPtr.Zero, false); }
                        return IntPtr.Zero;
                    }
                case Native.WM_MOUSELEAVE:
                    tracking = false;
                    if (hover != -1) { hover = -1; Native.InvalidateRect(hwnd, IntPtr.Zero, false); }
                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP:
                    Invoke(HitRow(Native.LoWord(lParam), Native.HiWord(lParam)));
                    return IntPtr.Zero;
                case 0x0100: // WM_KEYDOWN
                    switch ((int)wParam.ToInt64())
                    {
                        case 0x1B: Close(); break;                 // Esc
                        case 0x28: Move(1); break;                 // Abajo
                        case 0x26: Move(-1); break;                // Arriba
                        case 0x0D: if (hover >= 0) Invoke(hover); break;
                    }
                    return IntPtr.Zero;
            }
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        // ------------------------------------------------------------------ dibujo

        void Paint()
        {
            PAINTSTRUCT ps;
            IntPtr dc = Native.BeginPaint(hwnd, out ps);
            RECT rc;
            Native.GetClientRect(hwnd, out rc);
            if (rc.Width > 0 && rc.Height > 0 && visible)
            {
                surface.Resize(dc, rc.Width, rc.Height);
                IntPtr g = surface.BeginGraphics();
                Gfx.Clear(g, 0);
                DrawContent(g);
                Gfx.End(g);
                surface.Present(dc);
            }
            Native.EndPaint(hwnd, ref ps);
        }

        void DrawContent(IntPtr g)
        {
            int ink = light ? 0 : 255;
            uint text = Gfx.Argb(1f, light ? 26 : 255, light ? 26 : 255, light ? 26 : 255);
            uint muted = Gfx.Argb(light ? 0.62f : 0.7f, ink, ink, ink);
            int padX = S(16), iconS = S(16), gap = S(12);

            for (int i = 0; i < items.Count; i++)
            {
                var d = items[i];
                var r = rows[i];
                if (d.Kind == MenuKind.Separator)
                {
                    Gfx.FillRect(g, S(8), r.Top + r.Height / 2f, w - S(16), 1f, Gfx.Argb(light ? 0.10f : 0.14f, ink, ink, ink));
                    continue;
                }
                if (d.Kind == MenuKind.Header)
                {
                    Gfx.Text(g, d.Text, TextFont, F(12), false, muted, padX, r.Top, w - padX * 2, r.Height, 0);
                    continue;
                }
                if (i == hover)
                    Gfx.FillRound(g, S(4), r.Top, w - S(8), r.Height, S(4), Gfx.Argb(light ? 0.06f : 0.08f, ink, ink, ink));

                float iy = r.Top + (r.Height - iconS) / 2f;
                if (d.Image != null) Gfx.DrawImage(g, d.Image, padX, iy, iconS, iconS, 1f);
                else if (!string.IsNullOrEmpty(d.Glyph)) Gfx.Text(g, d.Glyph, IconFont, F(16), false, text, padX, iy, iconS, iconS, 1);

                float tx = padX + iconS + gap;
                Gfx.Text(g, d.Text, TextFont, F(13.3f), false, text, tx, r.Top, w - tx - padX, r.Height, 0);
            }
        }
    }
}
