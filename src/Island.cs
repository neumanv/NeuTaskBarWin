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
        RECT rest;
        // La isla se ancla por su final (derecha/abajo) en lugar de por su inicio: crece hacia el inicio.
        public bool AnchorEnd;
        public Action Moved;            // ha cambiado el sitio que ocupa (RestRect)
        protected bool keepZ;           // no se pone por encima de las demás al recolocarse (la placa de las islas unidas)
        // Modo mover (se activa desde la aplicación): la isla se arrastra con el ratón en lugar de atender clics.
        public bool Movable;
        public Action<Island> OnMoveStart;
        public Action<Island, int, int> OnMoving, OnMoveEnd;   // centro de la isla en pantalla
        bool moving, floating;
        int moveDx, moveDy;
        RECT restWin;                   // la ventana en su sitio, con sus salientes
        const uint AnimTimerId = 100;
        bool animating;
        long lastTick;
        // Superficie con alfa de la isla y otra auxiliar para el texto (ver Text).
        readonly Gfx.AlphaSurface surface = new Gfx.AlphaSurface(), textLayer = new Gfx.AlphaSurface();
        int[] textRow = new int[0], destRow = new int[0];
        // Color de la isla: tinte del acrílico (AARRGGBB), o transparente del todo si su alfa es 0.
        uint tint;
        bool tintApplied, clear, darkInk;
        int inkMode;                    // tinta con la que se aplicó el color (Config.InkMode)
        public bool Outline;            // contorno fino alrededor de la isla
        bool tracking;
        bool shown;

        // Fondo de la isla: una ventana justo detrás de ella y con su misma forma que muestra el escritorio (miniatura
        // en vivo de DWM). Así el acrílico nunca deja entrever la ventana que quede debajo: una app maximizada se ve
        // por el hueco entre las islas, pero tras ellas es como si no hubiera ventana.
        const string BackClass = "NeuTaskBar.Back";
        static readonly WndProcDelegate BackProc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);
        static bool backRegistered;
        IntPtr back, backThumb, backSource;

        protected Island(bool anchorEnd) { AnchorEnd = anchorEnd; }

        public int S(int v) { return (int)Math.Round(v * Dpi / 96.0); }

        // Grosor de la isla (px), margen entre su borde y las celdas, y factor con el que encogen los adornos
        // (logo de Inicio, glifos, indicadores) cuando la barra es más baja que la normal de Windows.
        protected int BarH { get { return S(Config.BarHeight); } }
        protected int Inset { get { return Math.Max(S(2), Math.Min(S(4), BarH / 12)); } }
        protected float Shrink { get { return Math.Min(1f, Config.BarHeight / (float)Config.StdBarHeight); } }

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
            CreateBack();
        }

        void CreateBack()
        {
            if (!backRegistered)
            {
                var wc = new WNDCLASS();
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(BackProc);
                wc.hInstance = Native.GetModuleHandleW(null);
                wc.hbrBackground = Native.GetStockObject(4 /* BLACK_BRUSH */);
                wc.lpszClassName = BackClass;
                Native.RegisterClassW(ref wc);
                backRegistered = true;
            }
            int ex = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            back = Native.CreateWindowExW(ex, BackClass, "", Native.WS_POPUP, 0, 0, 10, 10,
                IntPtr.Zero, IntPtr.Zero, wc_hInstance(), IntPtr.Zero);
            if (back == IntPtr.Zero || !IsWin11) return;
            int round = 2; // DWMWCP_ROUND, como la isla
            Native.DwmSetWindowAttribute(back, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            int border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            Native.DwmSetWindowAttribute(back, Native.DWMWA_BORDER_COLOR, ref border, 4);
        }

        // Coloca el fondo exactamente bajo la isla (mismo rectángulo, justo detrás en el orden Z) con el trozo de
        // escritorio que le corresponde.
        void SyncBack(int x, int y, int w, int h, bool vis)
        {
            if (back == IntPtr.Zero) return;
            IntPtr desk = Native.FindWindowW("Progman", null);
            if (desk != backSource)
            {
                // El escritorio estrena ventana cuando se reinicia el Explorador.
                if (backThumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(backThumb); backThumb = IntPtr.Zero; }
                backSource = desk;
                if (desk != IntPtr.Zero && Native.DwmRegisterThumbnail(back, desk, out backThumb) != 0) { backThumb = IntPtr.Zero; backSource = IntPtr.Zero; }
            }
            if (backThumb == IntPtr.Zero || clear) vis = false;
            Native.SetWindowPos(back, Hwnd, x, y, w, h,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | (vis ? Native.SWP_SHOWWINDOW : Native.SWP_HIDEWINDOW));
            if (!vis) return;
            RECT d;
            Native.GetWindowRect(desk, out d);
            // Solo la parte que cae dentro del escritorio (los salientes de la isla quedan fuera de la pantalla).
            int l = Math.Max(x, d.Left), t = Math.Max(y, d.Top), r = Math.Min(x + w, d.Right), b = Math.Min(y + h, d.Bottom);
            var p = new DWM_THUMBNAIL_PROPERTIES();
            p.dwFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10; // destino, origen, opacidad, visible, solo área cliente
            p.rcDestination = new RECT(l - x, t - y, r - x, b - y);
            p.rcSource = new RECT(l - d.Left, t - d.Top, r - d.Left, b - d.Top);
            p.opacity = 255;
            p.fVisible = r > l && b > t ? 1 : 0;
            p.fSourceClientAreaOnly = 1;
            Native.DwmUpdateThumbnailProperties(backThumb, ref p);
        }

        void ShowBack(bool show)
        {
            if (back == IntPtr.Zero) return;
            Native.SetWindowPos(back, Hwnd, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE
                | Native.SWP_NOOWNERZORDER | (show && backThumb != IntPtr.Zero && !clear ? Native.SWP_SHOWWINDOW : Native.SWP_HIDEWINDOW));
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
            if (Movable || moving)
            {
                switch (msg)
                {
                    case Native.WM_SETCURSOR:
                        Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, new IntPtr(32646 /* IDC_SIZEALL */)));
                        return new IntPtr(1);
                    case Native.WM_LBUTTONDOWN: BeginMove(); return IntPtr.Zero;
                    case Native.WM_MOUSEMOVE: if (moving) MoveStep(); return IntPtr.Zero;
                    case Native.WM_LBUTTONUP:
                    case 0x0215: // WM_CAPTURECHANGED
                        if (moving) EndMove();
                        return IntPtr.Zero;
                    case Native.WM_MOUSELEAVE:
                    case Native.WM_MBUTTONUP:
                    case Native.WM_RBUTTONUP:
                    case Native.WM_MOUSEWHEEL:
                    case 0x204: // WM_RBUTTONDOWN
                    case 0x207: // WM_MBUTTONDOWN
                        return IntPtr.Zero;
                }
            }
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

        // Color de la isla (AARRGGBB): acrílico con ese tinte o, con alfa 0, transparente del todo (sin desenfoque y sin
        // el fondo de escritorio: se ve lo que haya detrás). El contenido pasa a tinta oscura si el color es claro.
        public void SetTint(uint t)
        {
            if (tintApplied && t == tint && inkMode == Config.InkMode) return;
            tint = t;
            tintApplied = true;
            inkMode = Config.InkMode;
            clear = (t >> 24) == 0;
            if (inkMode != 0) darkInk = inkMode == 2;   // tinta elegida a mano
            else if (!clear)
            {
                float a = (t >> 24) / 255f;
                float lum = (0.299f * ((t >> 16) & 255) + 0.587f * ((t >> 8) & 255) + 0.114f * (t & 255)) / 255f;
                darkInk = a * lum + (1f - a) * 0.5f > 0.6f;   // lo que se transparenta se supone de tono medio
            }
            ApplyAccent(Hwnd, clear ? 2 /* ACCENT_ENABLE_TRANSPARENTGRADIENT */ : 4, t, !darkInk, true);
            if (clear && IsWin11)
            {
                // Con las esquinas redondeadas DWM le pone sombra a la ventana, y delataría el contorno de la isla.
                int square = 1; // DWMWCP_DONOTROUND
                Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, 4);
            }
            if (VisW > 0) Reposition();   // pone o quita el fondo de escritorio
            WatchBackdrop();
            Invalidate();
        }

        public void RefreshTint(uint t)
        {
            tintApplied = false;
            SetTint(t);
        }

        // Isla transparente: la tinta se elige según lo que se ve detrás (el escritorio, o el relleno de una ventana
        // maximizada). Se mira la franja de la isla que da al centro de la pantalla, donde no se dibuja nada.
        public void WatchBackdrop()
        {
            if (!clear || inkMode != 0 || !shown || autoHidden || hideE.V > 0.01f || Hwnd == IntPtr.Zero) return;
            RECT r = rest;
            int skip = S(12), band = Math.Max(1, Math.Min(2, Inset));
            if (Vert)
            {
                r.Top += skip; r.Bottom -= skip;
                if (edge == Edge.Left) r.Left = r.Right - band; else r.Right = r.Left + band;
            }
            else
            {
                r.Left += skip; r.Right -= skip;
                if (edge == Edge.Top) r.Top = r.Bottom - band; else r.Bottom = r.Top + band;
            }
            int[] px = StripFill.Capture(r);
            if (px == null || px.Length == 0) return;
            long sum = 0;
            foreach (int c in px) sum += 299 * ((c >> 16) & 255) + 587 * ((c >> 8) & 255) + 114 * (c & 255);
            float lum = sum / (255000f * px.Length);
            // Con margen entre un cambio y el contrario, para que no baile sobre fondos de tono medio.
            bool dark = darkInk ? lum > 0.42f : lum > 0.6f;
            if (dark == darkInk) return;
            darkInk = dark;
            Invalidate();
        }

        // Toma la tinta de otra isla (las islas unidas son transparentes y van sobre una placa con el color).
        public void AdoptInk(Island other)
        {
            if (darkInk == other.darkInk) return;
            darkInk = other.darkInk;
            Invalidate();
        }

        public bool Clear { get { return clear; } }
        public uint TintValue { get { return tint; } }

        // Tinta del contenido: clara sobre islas oscuras y oscura sobre islas claras. v = gris sobre fondo oscuro.
        protected int Ink(int v)
        {
            if (darkInk) v = Math.Max(0, Math.Min(255, 271 - v));
            return Native.Rgb(v, v, v);
        }

        // Velo que resalta una celda (ratón encima, app activa...).
        protected uint Veil(float alpha)
        {
            return darkInk ? Gfx.Argb(alpha * 0.7f, 0, 0, 0) : Gfx.Argb(alpha, 255, 255, 255);
        }

        // Color de acento (logo de Inicio, indicador de la ventana activa): el elegido en la aplicación o, si no se ha
        // elegido ninguno, un azul que se lee bien sobre la tinta de la isla.
        protected int AccentR { get { uint a = Config.Accent; return a != 0 ? (int)((a >> 16) & 255) : darkInk ? 0 : 76; } }
        protected int AccentG { get { uint a = Config.Accent; return a != 0 ? (int)((a >> 8) & 255) : darkInk ? 95 : 194; } }
        protected int AccentB { get { uint a = Config.Accent; return a != 0 ? (int)(a & 255) : darkInk ? 184 : 255; } }
        protected uint Accent(float alpha) { return Gfx.Argb(alpha, AccentR, AccentG, AccentB); }

        // Acrílico con el tinte indicado (AARRGGBB). dark = esquema oscuro de DWM; noBorder = sin borde del sistema.
        public static void ApplyAcrylic(IntPtr hwnd, uint t, bool dark, bool noBorder)
        {
            ApplyAccent(hwnd, 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, t, dark, noBorder);
        }

        // El mismo acrílico para una ventana que no es una isla (el relleno de la franja): sin esquinas redondeadas ni
        // borde. on = false se lo quita.
        public static void SetPlainAcrylic(IntPtr hwnd, bool on, uint t)
        {
            ApplyAccent(hwnd, on ? 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */ : 0 /* ACCENT_DISABLED */, t, true, true);
            if (!IsWin11) return;
            int square = 1; // DWMWCP_DONOTROUND
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, 4);
        }

        static void ApplyAccent(IntPtr hwnd, int state, uint t, bool dark, bool noBorder)
        {
            var accent = new ACCENT_POLICY();
            accent.AccentState = state;
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

        // Rectángulo que ocupa la isla en su sitio (pantalla), esté escondida o no.
        public RECT RestRect { get { return rest; } }

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

        // ---- modo mover: la ventana sigue al cursor hasta soltarla; quien decide dónde queda es la aplicación ----

        void BeginMove()
        {
            POINT p;
            RECT w;
            Native.GetCursorPos(out p);
            Native.GetWindowRect(Hwnd, out w);
            moveDx = p.X - w.Left;
            moveDy = p.Y - w.Top;
            moving = true;
            Native.SetCapture(Hwnd);
            HideTip();
            if (OnMoveStart != null) OnMoveStart(this);
        }

        protected virtual void HideTip() { }

        // Acompaña a otra isla que se está arrastrando (las islas unidas se mueven juntas): la ventana se desplaza
        // de su sitio lo mismo que aquella, hasta Settle.
        public void FloatBy(int dx, int dy)
        {
            if (Hwnd == IntPtr.Zero || !shown || moving) return;
            floating = true;
            int x = restWin.Left + dx, y = restWin.Top + dy;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | (keepZ ? Native.SWP_NOZORDER : 0u));
            SyncBack(x, y, restWin.Width, restWin.Height, true);
        }

        public void Settle()
        {
            if (!floating) return;
            floating = false;
            Reposition();
        }

        void MoveStep()
        {
            POINT p;
            RECT w;
            Native.GetCursorPos(out p);
            Native.GetWindowRect(Hwnd, out w);
            int x = p.X - moveDx, y = p.Y - moveDy;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            SyncBack(x, y, w.Width, w.Height, shown);
            if (OnMoving != null) OnMoving(this, x + w.Width / 2, y + w.Height / 2);
        }

        void EndMove()
        {
            moving = false;
            Native.ReleaseCapture();
            RECT w;
            Native.GetWindowRect(Hwnd, out w);
            if (OnMoveEnd != null) OnMoveEnd(this, (w.Left + w.Right) / 2, (w.Top + w.Bottom) / 2);
            Reposition();   // a su sitio (el nuevo, o el de antes si no cambió)
        }

        // Deja la ventana justo detrás de otra en el orden Z.
        public void PlaceBehind(IntPtr other)
        {
            if (Hwnd == IntPtr.Zero || !shown) return;
            Native.SetWindowPos(Hwnd, other, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            ShowBack(!FullyHidden);
        }

        protected void Reposition()
        {
            if (moving || floating) return;
            bool vert = Vert;
            int pw = vert ? VisH : VisW, ph = vert ? VisW : VisH;
            int len = vert ? ph : pw;
            int start = AnchorEnd ? along - len : along;
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
            bool moved = x != rest.Left || y != rest.Top || x + pw != rest.Right || y + ph != rest.Bottom;
            rest = new RECT(x, y, x + pw, y + ph);

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

            restWin = new RECT(x - ovL, y - ovT, x + pw + ovR, y + ph + ovB);
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
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | (vis ? Native.SWP_SHOWWINDOW : 0u) | (keepZ ? Native.SWP_NOZORDER : 0u));
            if (!vis && Native.IsWindowVisible(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_HIDE);
            SyncBack(x - ovL, y - ovT, pw + ovL + ovR, ph + ovT + ovB, vis);
            Invalidate();
            if (moved && Moved != null) Moved();
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
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | (keepZ ? Native.SWP_NOZORDER : 0u));
                ShowBack(!FullyHidden);
            }
            else
            {
                Native.ShowWindow(Hwnd, Native.SW_HIDE);
                shown = false;
                ShowBack(false);
            }
        }

        public void RaiseTopmost()
        {
            if (Hwnd == IntPtr.Zero || !shown || FullyHidden || keepZ) return;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            ShowBack(true);
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
                // Superficie con alfa (transparente de partida): el contenido tapa de verdad lo que tiene detrás, sea
                // claro u oscuro, en vez de sumarse a ello.
                surface.Resize(dc, w, h);
                Gdi.FillRectColor(surface.Dc, new RECT(0, 0, w, h), 0);
                if (!clear)
                {
                    IntPtr g = surface.BeginGraphics();
                    Gfx.FillRect(g, 0, 0, w, h, tint);
                    Gfx.End(g);
                }
                OnPaintContent(surface.Dc);
                if (Outline)
                {
                    // Sigue las esquinas que redondea DWM en Windows 11. Los lados pegados al borde de la pantalla
                    // quedan fuera de ella con sus salientes, así que ahí no se ve.
                    IntPtr g = surface.BeginGraphics();
                    Gfx.StrokeRound(g, 0, 0, w, h, IsWin11 ? S(8) : 0, Math.Max(1, S(1)), darkInk ? Gfx.Argb(0.30f, 0, 0, 0) : Gfx.Argb(0.34f, 255, 255, 255));
                    Gfx.End(g);
                }
                if (Movable)
                {
                    // Modo mover: un velo sobre toda la isla avisa de que ahora se arrastra.
                    IntPtr g = surface.BeginGraphics();
                    Gfx.FillRect(g, 0, 0, w, h, Veil(0.14f));
                    Gfx.End(g);
                }
                surface.Present(dc);
            }
            Native.EndPaint(Hwnd, ref ps);
        }

        // Dibujo suavizado (GDI+) sobre la isla, en sus coordenadas visibles.
        protected IntPtr BeginGfx()
        {
            IntPtr g = surface.BeginGraphics();
            Gfx.Translate(g, ovL, ovT);
            return g;
        }

        [DllImport("gdi32.dll")] static extern bool GdiFlush();

        // Texto con GDI (el más nítido a estos tamaños), que no sabe de alfa: se dibuja en blanco sobre negro en una
        // superficie aparte y esa cobertura se compone sobre la isla con el color pedido. Llamar después de End(g).
        protected void Text(string s, RECT r, IntPtr font, int rgb, uint flags)
        {
            Text(s, r, font, rgb, flags, 1f);
        }

        // alpha = opacidad del texto (para fundir uno con otro).
        protected void Text(string s, RECT r, IntPtr font, int rgb, uint flags, float alpha)
        {
            int a256 = (int)Math.Round(Math.Max(0f, Math.Min(1f, alpha)) * 256f);
            if (string.IsNullOrEmpty(s) || surface.Dc == IntPtr.Zero || a256 == 0) return;
            int W = surface.W, H = surface.H;
            r = new RECT(r.Left + ovL, r.Top + ovT, r.Right + ovL, r.Bottom + ovT);
            int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top), x1 = Math.Min(W, r.Right), y1 = Math.Min(H, r.Bottom);
            int n = x1 - x0;
            if (n <= 0 || y1 <= y0) return;
            textLayer.Resize(surface.Dc, W, H);
            Gdi.FillRectColor(textLayer.Dc, new RECT(x0, y0, x1, y1), 0);
            Gdi.Text(textLayer.Dc, s, r, font, 0xFFFFFF, flags);
            GdiFlush();
            if (textRow.Length < n) { textRow = new int[n]; destRow = new int[n]; }
            int cr = rgb & 255, cg = (rgb >> 8) & 255, cb = (rgb >> 16) & 255;
            for (int y = y0; y < y1; y++)
            {
                long offset = ((long)y * W + x0) * 4;
                Marshal.Copy(new IntPtr(textLayer.Bits.ToInt64() + offset), textRow, 0, n);
                bool any = false;
                for (int i = 0; i < n && !any; i++) any = (textRow[i] & 0xFF00) != 0;
                if (!any) continue;
                IntPtr dest = new IntPtr(surface.Bits.ToInt64() + offset);
                Marshal.Copy(dest, destRow, 0, n);
                for (int i = 0; i < n; i++)
                {
                    int c = (((textRow[i] >> 8) & 255) * a256) >> 8;
                    if (c == 0) continue;
                    int d = destRow[i], k = 255 - c;
                    destRow[i] = ((c + ((d >> 24) & 255) * k / 255) << 24) | (((cr * c + ((d >> 16) & 255) * k) / 255) << 16)
                        | (((cg * c + ((d >> 8) & 255) * k) / 255) << 8) | ((cb * c + (d & 255) * k) / 255);
                }
                Marshal.Copy(destRow, 0, dest, n);
            }
        }

        public void ReleaseSurface()
        {
            surface.Free();
            textLayer.Free();
        }

        public void Destroy()
        {
            ReleaseSurface();
            if (backThumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(backThumb); backThumb = IntPtr.Zero; }
            if (back != IntPtr.Zero) { Native.DestroyWindow(back); back = IntPtr.Zero; }
            if (Hwnd != IntPtr.Zero) { Native.DestroyWindow(Hwnd); Hwnd = IntPtr.Zero; }
        }
    }

    // Placa de las islas unidas: una sola pieza con el color, las esquinas redondeadas y la sombra, que va detrás de
    // las dos islas (transparentes en ese modo). Dos ventanas pegadas no valdrían: Windows le pone a cada una su
    // sombra y sus esquinas, y se vería la costura.
    sealed class PlateIsland : Island
    {
        public PlateIsland() : base(false) { keepZ = true; }
        public void Init() { Create(false); }
        protected override void OnPaintContent(IntPtr dc) { }
        public override void Layout() { Reposition(); }
    }
}
