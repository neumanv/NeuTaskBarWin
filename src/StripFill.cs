using System;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    // Relleno de la franja reservada para las islas. Las ventanas maximizadas terminan donde empieza la franja (así
    // las islas no tapan nada de la app); para que el hueco entre las dos islas no enseñe el escritorio, esta ventana
    // prolonga hasta el borde de la pantalla la última línea de la ventana maximizada que queda a la vista, de modo
    // que parece que la app continúa por el hueco con sus mismos colores.
    //  - Línea de colores lisos (lo normal en una app): miniatura en vivo de DWM de esa línea, estirada.
    //  - Línea con contenido (texto, una imagen, un mapa): estirada dejaría rayas, así que el hueco se pinta entero con
    //    su color principal, liso, leyéndola de la pantalla cada poco.
    sealed class StripFill
    {
        const string ClassName = "NeuTaskBar.Fill";
        static readonly WndProcDelegate Proc = StaticProc;
        static readonly System.Collections.Generic.List<StripFill> instances = new System.Collections.Generic.List<StripFill>();
        static bool classRegistered;

        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] int[] bits, ref BITMAPINFOHEADER bmi, uint usage);
        [DllImport("gdi32.dll")]
        static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, int[] bits,
            ref BITMAPINFOHEADER bmi, uint usage, uint rop);

        IntPtr hwnd, thumb, source;
        RECT shown, watchLine;
        bool busy;          // la línea no es de colores lisos: se pinta aquí en lugar de mostrar la miniatura
        bool solid;         // siempre un color liso (franja de arriba)
        int[] soft;         // lo que se pinta entonces: el color principal de la línea (un píxel)
        string thumbState = "";   // lo último que se le pidió a la miniatura (para no repetirlo en cada repaso)

        public bool Visible { get; private set; }

        public void Init()
        {
            instances.Add(this);
            var wc = new WNDCLASS();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc);
            wc.hInstance = Native.GetModuleHandleW(null);
            wc.hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512));
            wc.hbrBackground = Native.GetStockObject(4 /* BLACK_BRUSH */);
            wc.lpszClassName = ClassName;
            if (!classRegistered) { Native.RegisterClassW(ref wc); classRegistered = true; }
            // Siempre visible, como una barra de tareas, pero por debajo de las islas: se crea antes que ellas y después
            // nunca se le cambia el orden Z.
            hwnd = Native.CreateWindowExW(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, ClassName, "",
                Native.WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        static IntPtr StaticProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == Native.WM_MOUSEACTIVATE) return new IntPtr(3); // MA_NOACTIVATE
            if (m == Native.WM_ERASEBKGND)
                foreach (var f in instances)
                    if (f.busy && h == f.hwnd)
                    {
                        f.PaintSoft(w);
                        return new IntPtr(1);
                    }
            return Native.DefWindowProcW(h, m, w, l);
        }

        // Ventana maximizada del monitor que queda más arriba en el orden Z (la que se ve junto a la franja).
        public static IntPtr TopMaximized(IntPtr monitor)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || !Native.IsZoomed(h) || Native.IsIconic(h)) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                if (Native.MonitorFromWindow(h, 2 /* NEAREST */) != monitor) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // strip = franja reservada (pantalla); edge = borde en el que está; src = ventana que se prolonga (Zero = ocultar);
        // inset = píxeles hacia dentro del contenido de los que se toma la línea.
        public void Update(RECT strip, Edge edge, IntPtr src, int inset)
        {
            if (hwnd == IntPtr.Zero) return;
            if (src != source)
            {
                if (thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; }
                source = src;
                if (src != IntPtr.Zero && Native.DwmRegisterThumbnail(hwnd, src, out thumb) != 0) thumb = IntPtr.Zero;
            }
            if (thumb == IntPtr.Zero || strip.Width <= 0 || strip.Height <= 0)
            {
                if (Native.IsWindowVisible(hwnd)) Native.ShowWindow(hwnd, Native.SW_HIDE);
                Visible = busy = false;
                return;
            }

            // Línea de la ventana que se prolonga (2 px de grosor, en coordenadas de pantalla): la última de su contenido
            // junto a la franja. Maximizadas, algunas apps (las de tipo Chrome) dejan entre su contenido y la franja unos
            // píxeles de marco de otro color: el relleno se extiende para taparlos. La línea se toma un poco más adentro
            // (inset), porque el propio contenido suele acabar en un filete de borde (Brave lo pinta oscuro).
            RECT client;
            Native.GetClientRect(src, out client);
            var c0 = new POINT(0, 0);
            Native.ClientToScreen(src, ref c0);
            client = new RECT(c0.X, c0.Y, c0.X + client.Width, c0.Y + client.Height);
            // Tampoco debe quedar a la vista el escritorio si la ventana no llega a la franja (p. ej. una app elevada
            // que no se deja recolocar cuando Windows reserva más sitio que las islas): se tapa hasta su borde.
            int maxFrame = Math.Max(12, Math.Min(strip.Width, strip.Height));
            RECT line = strip;
            switch (edge)
            {
                case Edge.Top:
                    // Arriba la ventana empieza por su barra de título, y unos píxeles más adentro ya cruzan pestañas
                    // y botones: estirados dejarían bloques de otro tono. Se toma su primera línea y el relleno es
                    // siempre de un solo color, el del marco.
                    line.Top = strip.Bottom + 1; line.Bottom = line.Top + 2;
                    break;
                case Edge.Left:
                    if (client.Left > strip.Right && client.Left - strip.Right <= maxFrame) strip.Right = client.Left;
                    line.Left = strip.Right + inset; line.Right = line.Left + 2;
                    break;
                case Edge.Right:
                    if (client.Right < strip.Left && strip.Left - client.Right <= maxFrame) strip.Left = client.Right;
                    line.Right = strip.Left - inset; line.Left = line.Right - 2;
                    break;
                default:
                    if (client.Bottom < strip.Top && strip.Top - client.Bottom <= maxFrame) strip.Top = client.Bottom;
                    line.Bottom = strip.Top - inset; line.Top = line.Bottom - 2;
                    break;
            }
            shown = strip;
            watchLine = line;
            solid = edge == Edge.Top;
            Visible = true;
            Native.SetWindowPos(hwnd, IntPtr.Zero, strip.Left, strip.Top, strip.Width, strip.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            Watch();
        }

        // Decide cómo se rellena y lo actualiza. Se repasa cada poco: el contenido de la ventana cambia sin que se mueva.
        public void Watch()
        {
            if (!Visible) return;
            int w = watchLine.Width, h = watchLine.Height;
            bool along = w >= h;                 // la línea va a lo ancho (barra arriba o abajo)
            int len = along ? w : h;
            int stride = along ? 1 : w;
            int[] px = Capture(watchLine);
            int changes = 0;
            if (px != null)
                for (int i = 1; i < len; i++)
                    if (Diff(px[(i - 1) * stride], px[i * stride]) > 30) changes++;
            // Una app normal cruza unas pocas divisiones; con margen entre entrar y salir para que no parpadee.
            busy = solid || (busy ? changes > len / 96 : changes > len / 48);

            // El origen de la miniatura se mide desde la esquina de la ventana, que maximizada sobresale de la pantalla.
            RECT win;
            Native.GetWindowRect(source, out win);
            var p = new DWM_THUMBNAIL_PROPERTIES();
            p.dwFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10; // destino, origen, opacidad, visible, solo área cliente
            p.rcDestination = new RECT(0, 0, shown.Width, shown.Height);
            p.rcSource = new RECT(watchLine.Left - win.Left, watchLine.Top - win.Top, watchLine.Right - win.Left, watchLine.Bottom - win.Top);
            p.opacity = 255;
            p.fVisible = busy ? 0 : 1;
            p.fSourceClientAreaOnly = 0;
            string state = thumb + "," + busy + "," + shown.Width + "," + shown.Height + "," + p.rcSource.Left + "," + p.rcSource.Top + "," + p.rcSource.Right + "," + p.rcSource.Bottom;
            if (state != thumbState)
            {
                thumbState = state;
                Native.DwmUpdateThumbnailProperties(thumb, ref p);
            }
            if (!busy) { soft = null; return; }
            if (px == null) return;

            var row = new int[len];
            for (int i = 0; i < len; i++) row[i] = px[i * stride];
            soft = new[] { MainColor(row) };
            IntPtr dc = Native.GetDC(hwnd);
            PaintSoft(dc);
            Native.ReleaseDC(hwnd, dc);
        }

        void PaintSoft(IntPtr dc)
        {
            if (soft == null) return;
            var bmi = new BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.biWidth = 1;
            bmi.biHeight = -1;
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            StretchDIBits(dc, 0, 0, shown.Width, shown.Height, 0, 0, 1, 1, soft, ref bmi, 0, 0x00CC0020 /* SRCCOPY */);
        }

        // Color principal de la línea: el tono más repetido (agrupando los casi iguales) y, dentro de él, su media.
        static int MainColor(int[] row)
        {
            var count = new int[4096];
            foreach (int c in row) count[((c >> 12) & 0xF00) | ((c >> 8) & 0xF0) | ((c >> 4) & 0xF)]++;
            int best = 0;
            for (int i = 1; i < count.Length; i++) if (count[i] > count[best]) best = i;
            long r = 0, g = 0, b = 0;
            foreach (int c in row)
            {
                if ((((c >> 12) & 0xF00) | ((c >> 8) & 0xF0) | ((c >> 4) & 0xF)) != best) continue;
                r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
            }
            int n = Math.Max(1, count[best]);
            return (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
        }

        static int Diff(int a, int b)
        {
            return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255));
        }

        // Píxeles de un rectángulo de la pantalla (0x00RRGGBB, por filas de arriba abajo), o null si no se pudo leer.
        public static int[] Capture(RECT r)
        {
            int w = r.Width, h = r.Height;
            if (w <= 0 || h <= 0) return null;
            var px = new int[w * h];
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr dc = Native.CreateCompatibleDC(screen);
            IntPtr bmp = Native.CreateCompatibleBitmap(screen, w, h);
            IntPtr old = Native.SelectObject(dc, bmp);
            Native.BitBlt(dc, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
            Native.SelectObject(dc, old);
            var bmi = new BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.biWidth = w;
            bmi.biHeight = -h;
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            int got = GetDIBits(screen, bmp, 0, (uint)h, px, ref bmi, 0);
            Native.DeleteObject(bmp);
            Native.DeleteDC(dc);
            Native.ReleaseDC(IntPtr.Zero, screen);
            return got == 0 ? null : px;
        }

        public void Hide() { Update(default(RECT), Edge.Bottom, IntPtr.Zero, 0); }

        public void Destroy()
        {
            if (thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; }
            if (hwnd != IntPtr.Zero) { Native.DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
            instances.Remove(this);
        }
    }
}
