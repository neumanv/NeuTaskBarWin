using System;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    // Ventana base de una "isla": popup sin bordes, siempre visible, sin activación, con acrílico.
    abstract class Island
    {
        const string ClassName = "NeuTaskBar.Island";
        static readonly WndProcDelegate StaticProc = StaticWndProc;
        static bool classRegistered;

        public IntPtr Hwnd;
        public Action DpiChanged;
        public Action OnAnyMouseDown;   // cualquier botón pulsado sobre la isla (cierra menús abiertos)
        public int Dpi = 96;
        public int VisW, VisH;          // tamaño visible (px)
        protected int ovL, ovT, ovR, ovB; // saliente fuera de pantalla (para ocultar las esquinas redondeadas pegadas al borde)
        Edge edge = Edge.Bottom;
        int cross, along;
        // Alineación centrada (como "Alineación de la barra de tareas: Centro"): la isla se centra en CenterPos
        // sin salirse de [CenterMin, CenterMax] a lo largo de la barra.
        public bool Centered;
        public int CenterPos, CenterMin, CenterMax;
        // Ocultación automática: la isla se desliza fuera de la pantalla hacia su borde.
        Ease hideE;
        int hideDist;
        bool autoHidden;
        readonly bool anchorRight;
        const uint AnimTimerId = 100;
        bool animating;
        long lastTick;
        IntPtr memDc, memBmp, oldBmp;
        int memW, memH;
        bool tracking;
        bool shown;

        protected Island(bool anchorRight) { this.anchorRight = anchorRight; }

        public int S(int v) { return (int)Math.Round(v * Dpi / 96.0); }

        public static bool IsWin11 { get { return Environment.OSVersion.Version.Build >= 22000; } }

        public void Create(bool acceptFiles)
        {
            if (!classRegistered)
            {
                var wc = new WNDCLASS();
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc);
                wc.hInstance = Native.GetModuleHandleW(null);
                wc.hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512));
                wc.lpszClassName = ClassName;
                Native.RegisterClassW(ref wc);
                classRegistered = true;
            }
            var handle = GCHandle.Alloc(this);
            int ex = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | (acceptFiles ? Native.WS_EX_ACCEPTFILES : 0);
            Hwnd = Native.CreateWindowExW(ex, ClassName, "NeuTaskBar", Native.WS_POPUP, 0, 0, 10, 10,
                IntPtr.Zero, IntPtr.Zero, wc_hInstance(), GCHandle.ToIntPtr(handle));
            ApplyEffects(Hwnd);
        }

        static IntPtr wc_hInstance() { return Native.GetModuleHandleW(null); }

        static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_NCCREATE)
            {
                IntPtr param = Marshal.ReadIntPtr(lParam);
                Native.SetWindowLongPtr(hwnd, Native.GWLP_USERDATA, param);
                var isl = (Island)GCHandle.FromIntPtr(param).Target;
                isl.Hwnd = hwnd;
                return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
            }
            IntPtr ud = Native.GetWindowLongPtr(hwnd, Native.GWLP_USERDATA);
            if (ud == IntPtr.Zero) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
            var self = (Island)GCHandle.FromIntPtr(ud).Target;
            if (msg == Native.WM_NCDESTROY)
            {
                Native.SetWindowLongPtr(hwnd, Native.GWLP_USERDATA, IntPtr.Zero);
                GCHandle.FromIntPtr(ud).Free();
                return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
            }
            try { return self.WndProc(msg, wParam, lParam); }
            catch (Exception ex)
            {
                Diag.Log(ex);
                // Si falla un mensaje de raton, se descarta: el procesado por defecto lo reenviaria al escritorio.
                if (msg >= 0x200 && msg <= 0x20E) return IntPtr.Zero;
                return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        protected virtual IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_ERASEBKGND: return new IntPtr(1);
                case Native.WM_MOUSEACTIVATE: return new IntPtr(3); // MA_NOACTIVATE
                case Native.WM_PAINT: Paint(); return IntPtr.Zero;
                case Native.WM_TIMER:
                    if ((uint)wParam.ToInt64() == AnimTimerId)
                    {
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        float dt = (float)((now - lastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
                        lastTick = now;
                        if (dt > 0.1f) dt = 0.1f;
                        bool more = OnAnimTick(dt);
                        if (hideE.Step(dt, 15f, 0.004f)) { Reposition(); more = true; }
                        Invalidate();
                        if (!more) { Native.KillTimer(Hwnd, new UIntPtr(AnimTimerId)); animating = false; }
                        return IntPtr.Zero;
                    }
                    return Native.DefWindowProcW(Hwnd, msg, wParam, lParam);
                case Native.WM_MOUSEMOVE:
                    if (!tracking)
                    {
                        var t = new TRACKMOUSEEVENT();
                        t.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
                        t.dwFlags = Native.TME_LEAVE;
                        t.hwndTrack = Hwnd;
                        Native.TrackMouseEvent(ref t);
                        tracking = true;
                    }
                    OnMouseMove(Native.LoWord(lParam) - ovL, Native.HiWord(lParam) - ovT);
                    return IntPtr.Zero;
                case Native.WM_MOUSELEAVE: tracking = false; OnMouseLeave(); return IntPtr.Zero;
                case 0x204: // WM_RBUTTONDOWN
                case 0x207: // WM_MBUTTONDOWN
                    if (OnAnyMouseDown != null) OnAnyMouseDown();
                    return IntPtr.Zero;
                case Native.WM_LBUTTONDOWN: if (OnAnyMouseDown != null) OnAnyMouseDown(); OnMouseButton(0, true, Native.LoWord(lParam) - ovL, Native.HiWord(lParam) - ovT); return IntPtr.Zero;
                case Native.WM_LBUTTONUP: OnMouseButton(0, false, Native.LoWord(lParam) - ovL, Native.HiWord(lParam) - ovT); return IntPtr.Zero;
                case Native.WM_MBUTTONUP: OnMouseButton(1, false, Native.LoWord(lParam) - ovL, Native.HiWord(lParam) - ovT); return IntPtr.Zero;
                case Native.WM_RBUTTONUP: OnMouseButton(2, false, Native.LoWord(lParam) - ovL, Native.HiWord(lParam) - ovT); return IntPtr.Zero;
                case Native.WM_MOUSEWHEEL:
                    OnWheel(Native.HiWord(wParam));
                    return IntPtr.Zero;
                case Native.WM_DPICHANGED:
                    if (DpiChanged != null) DpiChanged();
                    return IntPtr.Zero;
            }
            return Native.DefWindowProcW(Hwnd, msg, wParam, lParam);
        }

        // Arranca el temporizador de animación (solo corre mientras haya algo animándose).
        protected void StartAnim()
        {
            if (animating || Hwnd == IntPtr.Zero) return;
            animating = true;
            lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            Native.SetTimer(Hwnd, new UIntPtr(AnimTimerId), 15, IntPtr.Zero);
        }

        // Devuelve true mientras queden valores por asentarse.
        protected virtual bool OnAnimTick(float dt) { return false; }

        protected virtual void OnMouseMove(int x, int y) { }
        protected virtual void OnMouseLeave() { }
        protected virtual void OnMouseButton(int button, bool down, int x, int y) { }
        protected virtual void OnWheel(int delta) { }
        protected abstract void OnPaintContent(IntPtr dc);
        public abstract void Layout();

        public static void ApplyEffects(IntPtr hwnd)
        {
            ApplyAcrylic(hwnd, Config.Tint, true, true);
        }

        // Acrílico con el tinte indicado (AARRGGBB). dark = esquema oscuro de DWM; noBorder = sin borde del sistema.
        public static void ApplyAcrylic(IntPtr hwnd, uint t, bool dark, bool noBorder)
        {
            var accent = new ACCENT_POLICY();
            accent.AccentState = 4; // ACCENT_ENABLE_ACRYLICBLURBEHIND
            accent.AccentFlags = 2;
            // AARRGGBB -> AABBGGRR
            accent.GradientColor = unchecked((int)((t & 0xFF000000) | ((t & 0xFF) << 16) | (t & 0xFF00) | ((t >> 16) & 0xFF)));
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENT_POLICY)));
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new WINCOMPATTRDATA();
                data.Attribute = 19; // WCA_ACCENT_POLICY
                data.Data = p;
                data.DataSize = Marshal.SizeOf(typeof(ACCENT_POLICY));
                Native.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }

            int darkFlag = dark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, 4);
            if (IsWin11)
            {
                int round = 2; // DWMWCP_ROUND
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                int border = noBorder ? unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */ : unchecked((int)0xFFFFFFFF) /* por defecto */;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, 4);
            }
        }

        public Edge Edge { get { return edge; } }
        protected bool Vert { get { return edge == Edge.Left || edge == Edge.Right; } }

        // Coordenadas lógicas (u a lo largo de la isla, v a lo ancho) a físicas (x, y), y al revés.
        protected void MapRect(float u, float v, float du, float dv, out float x, out float y, out float w, out float h)
        {
            if (Vert) { x = v; y = u; w = dv; h = du; }
            else { x = u; y = v; w = du; h = dv; }
        }

        protected RECT MapRectI(float u, float v, float du, float dv)
        {
            float x, y, w, h;
            MapRect(u, v, du, dv, out x, out y, out w, out h);
            return new RECT((int)x, (int)y, (int)(x + w), (int)(y + h));
        }

        protected void Unmap(int x, int y, out int u, out int v)
        {
            if (Vert) { u = y; v = x; } else { u = x; v = y; }
        }

        // Fija el borde de pantalla, la coordenada del lado de la barra pegado a ese borde (cross) y el punto de
        // arranque a lo largo de la barra (along): izquierda/arriba si la isla se ancla al inicio, derecha/abajo si al final.
        public void SetPlacement(Edge e, int crossPos, int alongPos)
        {
            edge = e;
            cross = crossPos;
            along = alongPos;
            if (VisW > 0) Reposition();
        }

        static bool HasMonitorAt(int x, int y)
        {
            return Native.MonitorFromPoint(new POINT(x, y), 0) != IntPtr.Zero;
        }

        bool FullyHidden { get { return autoHidden && hideE.V >= 0.999f; } }

        public bool AutoHidden { get { return autoHidden; } }

        // Oculta o muestra la isla deslizándola hacia su borde de pantalla (dist = recorrido en píxeles).
        public void SetAutoHidden(bool hidden, int dist)
        {
            if (hidden == autoHidden && dist == hideDist) return;
            autoHidden = hidden;
            hideDist = dist;
            hideE.T = hidden ? 1f : 0f;
            // Si hay otro monitor al otro lado del borde, no se desliza (se vería pasar por él): aparece y desaparece.
            RECT w;
            Native.GetWindowRect(Hwnd, out w);
            int mx = (w.Left + w.Right) / 2, my = (w.Top + w.Bottom) / 2, far = S(70);
            bool neighbour = edge == Edge.Bottom ? HasMonitorAt(mx, cross + far) : edge == Edge.Top ? HasMonitorAt(mx, cross - far)
                : edge == Edge.Left ? HasMonitorAt(cross - far, my) : HasMonitorAt(cross + far, my);
            if (neighbour) hideE.V = hideE.T;
            Reposition();
            StartAnim();
        }

        protected void Reposition()
        {
            bool vert = Vert;
            int pw = vert ? VisH : VisW, ph = vert ? VisW : VisH;
            int len = vert ? ph : pw;
            int start = anchorRight ? along - len : along;
            if (Centered)
            {
                start = Math.Min(CenterPos - len / 2, CenterMax - len);
                start = Math.Max(start, CenterMin);
            }
            int x, y;
            if (vert)
            {
                x = edge == Edge.Left ? cross : cross - pw;
                y = start;
            }
            else
            {
                x = start;
                y = edge == Edge.Top ? cross : cross - ph;
            }

            ovL = ovT = ovR = ovB = 0;
            if (IsWin11)
            {
                // Las esquinas redondeadas de DWM quedan fuera de pantalla en los bordes pegados, sin invadir otros monitores.
                int r = S(12);
                if (!HasMonitorAt(x - 1, y + ph / 2)) ovL = r;
                if (!HasMonitorAt(x + pw + 1, y + ph / 2)) ovR = r;
                if (!HasMonitorAt(x + pw / 2, y - 1)) ovT = r;
                if (!HasMonitorAt(x + pw / 2, y + ph + 1)) ovB = r;
            }

            int off = (int)Math.Round(hideE.V * hideDist);
            if (off != 0)
            {
                if (edge == Edge.Bottom) y += off;
                else if (edge == Edge.Top) y -= off;
                else if (edge == Edge.Left) x -= off;
                else x += off;
            }
            bool vis = shown && !FullyHidden;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x - ovL, y - ovT, pw + ovL + ovR, ph + ovT + ovB,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | (vis ? Native.SWP_SHOWWINDOW : 0u));
            if (!vis && Native.IsWindowVisible(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_HIDE);
            Invalidate();
        }

        public void Invalidate()
        {
            if (Hwnd != IntPtr.Zero) Native.InvalidateRect(Hwnd, IntPtr.Zero, false);
        }

        public void Show(bool show)
        {
            if (Hwnd == IntPtr.Zero) return;
            if (show)
            {
                shown = true;
                if (!FullyHidden)
                    Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            else
            {
                Native.ShowWindow(Hwnd, Native.SW_HIDE);
                shown = false;
            }
        }

        public void RaiseTopmost()
        {
            if (Hwnd == IntPtr.Zero || !shown || FullyHidden) return;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void Paint()
        {
            PAINTSTRUCT ps;
            IntPtr dc = Native.BeginPaint(Hwnd, out ps);
            RECT rc;
            Native.GetClientRect(Hwnd, out rc);
            int w = rc.Width, h = rc.Height;
            if (w > 0 && h > 0)
            {
                if (memDc == IntPtr.Zero)
                {
                    memDc = Native.CreateCompatibleDC(dc);
                }
                if (memBmp == IntPtr.Zero || memW != w || memH != h)
                {
                    if (memBmp != IntPtr.Zero)
                    {
                        Native.SelectObject(memDc, oldBmp);
                        Native.DeleteObject(memBmp);
                    }
                    memBmp = Native.CreateCompatibleBitmap(dc, w, h);
                    oldBmp = Native.SelectObject(memDc, memBmp);
                    memW = w; memH = h;
                }
                RECT all = new RECT(0, 0, w, h);
                Gdi.FillRectColor(memDc, all, 0);
                Native.SetViewportOrgEx(memDc, ovL, ovT, IntPtr.Zero);
                OnPaintContent(memDc);
                Native.SetViewportOrgEx(memDc, 0, 0, IntPtr.Zero);
                Native.BitBlt(dc, 0, 0, w, h, memDc, 0, 0, 0x00CC0020);
            }
            Native.EndPaint(Hwnd, ref ps);
        }

        public void ReleaseSurface()
        {
            if (memDc != IntPtr.Zero)
            {
                if (memBmp != IntPtr.Zero)
                {
                    Native.SelectObject(memDc, oldBmp);
                    Native.DeleteObject(memBmp);
                    memBmp = IntPtr.Zero;
                }
                Native.DeleteDC(memDc);
                memDc = IntPtr.Zero;
            }
        }

        public void Destroy()
        {
            ReleaseSurface();
            if (Hwnd != IntPtr.Zero) { Native.DestroyWindow(Hwnd); Hwnd = IntPtr.Zero; }
        }
    }
}
