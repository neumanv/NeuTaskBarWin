using System;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    // Relleno de la franja reservada para las islas. Las ventanas maximizadas terminan donde empieza la franja (así
    // las islas no tapan nada de la app); para que el hueco entre las dos islas no enseñe el escritorio, esta ventana
    // prolonga hasta el borde de la pantalla la última línea de la ventana maximizada que queda a la vista, de modo
    // que parece que la app continúa por el hueco con sus mismos colores.
    //  - Línea de colores lisos (lo normal en una app): miniatura en vivo de DWM de esa línea, estirada.
    //  - Línea con un fondo dibujado, texto o iconos: estirada dejaría rayas, así que se lee de la pantalla cada poco
    //    y el hueco se pinta por tramos, cada uno con el color liso de su panel (Flatten).
    //  - Línea sin paneles reconocibles (una imagen, un mapa): el hueco entero, con su color principal.
    // Si se ha elegido en Apariencia, en vez de prolongar la ventana el hueco lleva el color de las islas: el mismo
    // acrílico que ellas, de lado a lado.
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

        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);

        IntPtr hwnd, thumb, source;
        RECT shown, watchLine;
        Edge side = Edge.Bottom;   // borde de pantalla en el que está la franja
        bool busy;         // la línea no es de colores lisos: se pinta aquí en lugar de mostrar la miniatura
        bool solid;         // siempre un color liso (franja de arriba)
        int[] soft;         // lo que se pinta entonces: un color por píxel de la línea, o uno solo para todo el hueco
        bool softAlong;     // esos colores van a lo ancho del hueco (franja arriba o abajo) o a lo alto
        int unit = 6;       // 6 px a la escala del monitor: medida de los bloques y tramos de Flatten
        bool plain;         // el hueco lleva el color de las islas en vez de prolongar la ventana
        bool plainOn;       // el acrílico está puesto en la ventana
        uint plainTint;
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
            // Con el color de las islas y estas transparentes del todo no hay nada que pintar: se ve el escritorio.
            plain = Config.FillMode == 1;
            uint tint = Config.IslandTint(Config.TintLeft);
            if (thumb == IntPtr.Zero || strip.Width <= 0 || strip.Height <= 0 || (plain && (tint >> 24) == 0))
            {
                if (Native.IsWindowVisible(hwnd)) Native.ShowWindow(hwnd, Native.SW_HIDE);
                Visible = busy = false;
                return;
            }
            SetMaterial(plain, tint);
            unit = Math.Max(4, inset);

            // Línea de la ventana que se prolonga (2 px de grosor, en coordenadas de pantalla): la última de su contenido
            // junto a la franja. Maximizadas, algunas apps (las de tipo Chrome) dejan entre su contenido y la franja unos
            // píxeles de marco de otro color: el relleno se extiende para taparlos. Aquí se fija la línea de partida, un
            // poco más adentro (inset); la definitiva la elige ReadLine en cada repaso, mirando cómo acaba la ventana.
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
            side = edge;
            solid = edge == Edge.Top;
            Visible = true;
            Native.SetWindowPos(hwnd, IntPtr.Zero, strip.Left, strip.Top, strip.Width, strip.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            Watch();
        }

        // Pone o quita en la ventana el acrílico de las islas (solo cuando cambia).
        void SetMaterial(bool on, uint tint)
        {
            if (on == plainOn && (!on || tint == plainTint)) return;
            plainOn = on;
            plainTint = tint;
            Island.SetPlainAcrylic(hwnd, on, tint);
            Native.InvalidateRect(hwnd, IntPtr.Zero, true);   // fuera lo que hubiera pintado: el acrílico se ve sobre negro
        }

        // Decide cómo se rellena y lo actualiza. Se repasa cada poco: el contenido de la ventana cambia sin que se mueva.
        public void Watch()
        {
            if (!Visible) return;
            int[] row = plain ? null : ReadLine();   // elige además de qué línea de la ventana se parte (watchLine)
            int w = watchLine.Width, h = watchLine.Height;
            bool along = w >= h;                 // la línea va a lo ancho (barra arriba o abajo)
            int len = along ? w : h;
            // Una línea lisa (o un degradado) solo tiene saltos en las divisiones entre paneles; con margen entre
            // entrar y salir para que no parpadee.
            int rough = row == null ? 0 : Roughness(row);
            busy = !plain && (solid || (busy ? rough > len / 128 : rough > len / 64));

            // El origen de la miniatura se mide desde la esquina de la ventana, que maximizada sobresale de la pantalla.
            RECT win;
            Native.GetWindowRect(source, out win);
            var p = new DWM_THUMBNAIL_PROPERTIES();
            p.dwFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10; // destino, origen, opacidad, visible, solo área cliente
            p.rcDestination = new RECT(0, 0, shown.Width, shown.Height);
            p.rcSource = new RECT(watchLine.Left - win.Left, watchLine.Top - win.Top, watchLine.Right - win.Left, watchLine.Bottom - win.Top);
            p.opacity = 255;
            p.fVisible = busy || plain ? 0 : 1;
            p.fSourceClientAreaOnly = 0;
            string state = thumb + "," + busy + "," + plain + "," + shown.Width + "," + shown.Height + "," + p.rcSource.Left + "," + p.rcSource.Top + "," + p.rcSource.Right + "," + p.rcSource.Bottom;
            if (state != thumbState)
            {
                thumbState = state;
                Native.DwmUpdateThumbnailProperties(thumb, ref p);
            }
            if (!busy) { soft = null; return; }
            if (row == null) return;

            // Por tramos de color liso; arriba (la barra de título) o sin paneles reconocibles, un solo color.
            soft = solid ? null : Flatten(row, unit * 3, unit * 5);
            if (soft == null) soft = new[] { MainColor(row, 0, len) };
            softAlong = along;
            IntPtr dc = Native.GetDC(hwnd);
            PaintSoft(dc);
            Native.ReleaseDC(hwnd, dc);
        }

        // Lee de la pantalla la banda de la ventana que toca el hueco y elige la línea que se prolonga: la última, la
        // que de verdad queda junto al hueco, para que el relleno empalme con ella sin cambio de tono. Solo si esa
        // última línea es un filete más fino que el resto (el borde oscuro que pinta Brave) se toma la siguiente, y si
        // no hay ninguna estable (una imagen), la de siempre, unos píxeles más adentro. Devuelve sus colores.
        int[] ReadLine()
        {
            if (solid)
            {
                // Arriba vale la primera línea de la ventana, sin más (ver Update).
                int[] px = Capture(watchLine);
                if (px == null) return null;
                var first = new int[watchLine.Width];
                Array.Copy(px, first, first.Length);
                return first;
            }
            int thick = Math.Max(2, (unit + 1) / 3), depth = unit + thick;
            bool along = side != Edge.Left && side != Edge.Right;
            RECT band = shown;
            if (side == Edge.Left) { band.Left = shown.Right; band.Right = band.Left + depth; }
            else if (side == Edge.Right) { band.Right = shown.Left; band.Left = band.Right - depth; }
            else { band.Bottom = shown.Top; band.Top = band.Bottom - depth; }
            int[] all = Capture(band);
            if (all == null) return null;
            // lines[k] = línea de la ventana a k píxeles del hueco.
            int len = along ? band.Width : band.Height;
            var lines = new int[depth][];
            for (int k = 0; k < depth; k++)
            {
                lines[k] = new int[len];
                for (int i = 0; i < len; i++)
                    lines[k][i] = along ? all[(depth - 1 - k) * len + i] : all[i * depth + (side == Edge.Left ? k : depth - 1 - k)];
            }
            int d = PickDepth(lines, thick, unit);
            watchLine = shown;
            if (side == Edge.Left) { watchLine.Left = shown.Right + d; watchLine.Right = watchLine.Left + 2; }
            else if (side == Edge.Right) { watchLine.Right = shown.Left - d; watchLine.Left = watchLine.Right - 2; }
            else { watchLine.Bottom = shown.Top - d; watchLine.Top = watchLine.Bottom - 2; }
            return lines[d];
        }

        // A cuántos píxeles del hueco está la línea que conviene prolongar: la más cercana que se repite igual en las
        // "thick" líneas seguidas (un filete de borde es más fino y no llega). Sin ninguna así antes de "fallback", esa.
        public static int PickDepth(int[][] lines, int thick, int fallback)
        {
            fallback = Math.Max(0, Math.Min(fallback, lines.Length - 1));
            for (int d = 0; d < fallback && d + thick <= lines.Length; d++)
            {
                bool stable = true;
                for (int k = d + 1; k < d + thick && stable; k++) stable = SameLine(lines[d], lines[k]);
                if (stable) return d;
            }
            return fallback;
        }

        // Dos líneas son la misma si casi todos sus píxeles coinciden (un fondo dibujado cambia unos pocos de una a otra).
        static bool SameLine(int[] a, int[] b)
        {
            int differ = 0;
            for (int i = 0; i < a.Length; i++) if (Diff(a[i], b[i]) > Near) differ++;
            return differ <= a.Length / 16;
        }

        void PaintSoft(IntPtr dc)
        {
            if (soft == null) return;
            int n = soft.Length;
            var bmi = new BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.biWidth = softAlong ? n : 1;
            bmi.biHeight = softAlong ? -1 : -n;
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            SetStretchBltMode(dc, 3 /* COLORONCOLOR */);
            StretchDIBits(dc, 0, 0, shown.Width, shown.Height, 0, 0, softAlong ? n : 1, softAlong ? 1 : n, soft, ref bmi, 0, 0x00CC0020 /* SRCCOPY */);
        }

        // Saltos de color entre píxeles vecinos de la línea, por pequeños que sean: los deja el dibujo de un fondo, el
        // texto o una imagen; un color liso o un degradado no dejan ninguno.
        public static int Roughness(int[] row)
        {
            int n = 0;
            for (int i = 1; i < row.Length; i++) if (Diff(row[i - 1], row[i]) > 5) n++;
            return n;
        }

        const int Near = 12;          // dos colores tan parecidos se toman por el mismo panel
        const int MaxPanels = 10;     // con más tramos que estos la línea no es de paneles

        // Parte la línea en tramos de color liso (los paneles de la app) y devuelve el color de cada píxel: el
        // principal de su tramo. Así el dibujo de un fondo, el texto o los iconos que cruce la línea no dejan rayas al
        // prolongarla, y cada panel conserva su color. block = tamaño de los bloques con los que se tantea la línea;
        // minLen = tramo más corto que se admite (los menores se unen al vecino). Devuelve null si la línea no tiene
        // paneles reconocibles (una imagen, un mapa).
        public static int[] Flatten(int[] row, int block, int minLen)
        {
            int n = row.Length;
            if (n == 0) return null;
            block = Math.Max(4, block);
            // Color principal de cada bloque; los bloques seguidos de color parecido forman un tramo.
            var starts = new System.Collections.Generic.List<int>();
            var colors = new System.Collections.Generic.List<int>();
            for (int b = 0; b < n; b += block)
            {
                int c = MainColor(row, b, Math.Min(block, n - b));
                if (colors.Count > 0 && Diff(c, colors[colors.Count - 1]) <= Near) continue;
                starts.Add(b);
                colors.Add(c);
            }
            // El corte entre dos tramos cae en algún punto de los bloques que lo rodean: donde mejor separa los
            // píxeles que se parecen más a un lado de los que se parecen más al otro.
            for (int i = 1; i < starts.Count; i++)
            {
                int lo = Math.Max(starts[i - 1], starts[i] - block), hi = Math.Min(i + 1 < starts.Count ? starts[i + 1] : n, starts[i] + block);
                int a = colors[i - 1], bcol = colors[i], right = 0;
                for (int j = lo; j < hi; j++) if (Diff(row[j], bcol) < Diff(row[j], a)) right++;
                int best = lo, bestScore = right, score = right;
                for (int j = lo; j < hi; j++)
                {
                    // Con el corte tras j: acierta si j es del lado izquierdo, falla si era del derecho.
                    score += Diff(row[j], bcol) < Diff(row[j], a) ? -1 : 1;
                    if (score > bestScore) { bestScore = score; best = j + 1; }
                }
                starts[i] = best;
            }
            // Los tramos demasiado cortos (un icono, una barra de desplazamiento, una palabra) se unen al vecino de
            // color más parecido, empezando por el menor.
            int dropped = 0;
            while (starts.Count > 1)
            {
                int tiny = -1, tinyLen = minLen;
                for (int i = 0; i < starts.Count; i++)
                {
                    int len = (i + 1 < starts.Count ? starts[i + 1] : n) - starts[i];
                    if (len < tinyLen) { tinyLen = len; tiny = i; }
                }
                if (tiny < 0) break;
                dropped += tinyLen;
                int last = starts.Count - 1;
                bool intoLeft = tiny == last || (tiny > 0 && Diff(colors[tiny], colors[tiny - 1]) <= Diff(colors[tiny], colors[tiny + 1]));
                if (!intoLeft) starts[tiny + 1] = starts[tiny];
                starts.RemoveAt(tiny);
                colors.RemoveAt(tiny);
            }
            // Al quitar uno pueden quedar juntos dos tramos del mismo color.
            for (int i = starts.Count - 1; i > 0; i--)
                if (Diff(colors[i], colors[i - 1]) <= Near) { starts.RemoveAt(i); colors.RemoveAt(i); }
            if (starts.Count > MaxPanels || dropped > n / 4) return null;

            var flat = new int[n];
            for (int i = 0; i < starts.Count; i++)
            {
                int from = starts[i], to = i + 1 < starts.Count ? starts[i + 1] : n;
                int c = MainColor(row, from, to - from);
                for (int j = from; j < to; j++) flat[j] = c;
            }
            return flat;
        }

        static readonly int[] bins = new int[4096];

        static int Bin(int c) { return ((c >> 12) & 0xF00) | ((c >> 8) & 0xF0) | ((c >> 4) & 0xF); }

        // Color principal de un trozo de la línea: el tono más repetido (agrupando los casi iguales) y, dentro de él,
        // su media.
        static int MainColor(int[] row, int start, int count)
        {
            int end = start + count, best = Bin(row[start]);
            for (int i = start; i < end; i++)
            {
                int k = Bin(row[i]);
                if (++bins[k] > bins[best]) best = k;
            }
            long r = 0, g = 0, b = 0;
            int n = 0;
            for (int i = start; i < end; i++)
            {
                int c = row[i];
                bins[Bin(c)] = 0;   // el contador queda limpio para la próxima vez
                if (Bin(c) != best) continue;
                r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                n++;
            }
            n = Math.Max(1, n);
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
