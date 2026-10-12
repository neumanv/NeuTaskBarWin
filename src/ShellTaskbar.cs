using System;
using System.IO;

namespace NeuTaskBar
{
    // Oculta/restaura la barra de tareas original y reserva el área de trabajo mediante un AppBar.
    static class ShellTaskbar
    {
        static string StatePath { get { return Path.Combine(Config.DataDir, "state.txt"); } }

        public static IntPtr FindTray() { return Native.FindWindowW("Shell_TrayWnd", null); }

        static APPBARDATA NewData(IntPtr hwnd)
        {
            var d = new APPBARDATA();
            d.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = hwnd;
            return d;
        }

        static int GetState(IntPtr tray)
        {
            var d = NewData(tray);
            return (int)(ulong)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d);
        }

        static void SetState(IntPtr tray, int state)
        {
            var d = NewData(tray);
            d.lParam = new IntPtr(state);
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d);
            ClearStaleAutoHide(tray);
        }

        // En ocultación automática Windows apunta su barra como "barra oculta" en los cuatro bordes de la pantalla, y al
        // quitarla solo la borra del borde en el que está. Mientras siga apuntada en los demás, las apps de tipo Chrome
        // (VS Code, Brave...) dejan 2 px libres en esos bordes al maximizarse y se ve una línea. Se borran aquí.
        static void ClearStaleAutoHide(IntPtr tray)
        {
            if ((GetState(tray) & Native.ABS_AUTOHIDE) != 0) return;
            for (uint edge = 0; edge < 4; edge++)
            {
                var q = NewData(IntPtr.Zero);
                q.uEdge = edge;
                if (Native.SHAppBarMessage(Native.ABM_GETAUTOHIDEBAR, ref q) != new UIntPtr((ulong)tray.ToInt64())) continue;
                var d = NewData(tray);
                d.uEdge = edge;   // lParam = 0: deja de ser la barra oculta de ese borde
                Native.SHAppBarMessage(Native.ABM_SETAUTOHIDEBAR, ref d);
            }
        }

        // La barra original no se oculta con SW_HIDE: se vuelve transparente y deja pasar los clics. Así sigue visible
        // para Windows, que la mantiene pintada; oculta de verdad, si el usuario la movía de borde o le cambiaba el
        // tamaño mientras tanto, al volver a mostrarla se quedaba en blanco hasta reiniciar el Explorador.
        const int GhostStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;

        static bool IsGhost(IntPtr tray)
        {
            return (Native.GetWindowLongPtr(tray, Native.GWL_EXSTYLE).ToInt64() & GhostStyle) == GhostStyle;
        }

        static void SetGhost(IntPtr tray, bool on)
        {
            long ex = Native.GetWindowLongPtr(tray, Native.GWL_EXSTYLE).ToInt64();
            if (on)
            {
                Native.SetWindowLongPtr(tray, Native.GWL_EXSTYLE, new IntPtr(ex | GhostStyle));
                Native.SetLayeredWindowAttributes(tray, 0, 0, 2 /* LWA_ALPHA */);
                if (!Native.IsWindowVisible(tray)) Native.ShowWindow(tray, Native.SW_SHOWNA);
            }
            else if ((ex & GhostStyle) != 0)
            {
                Native.SetWindowLongPtr(tray, Native.GWL_EXSTYLE, new IntPtr(ex & ~(long)GhostStyle));
                Native.RedrawWindow(tray, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100 | 0x400 /* invalidar todo, con hijas */);
            }
        }

        // Devuelve false si el explorador aún no tiene barra (p. ej. al iniciar sesión).
        public static bool Hide()
        {
            IntPtr tray = FindTray();
            if (tray == IntPtr.Zero) return false;
            int cur = GetState(tray);
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                if (!File.Exists(StatePath)) File.WriteAllText(StatePath, cur.ToString());
                else
                {
                    // Quedó de una ejecución anterior que no se cerró limpiamente: se repone el estado que guardó
                    // (las versiones antiguas dejaban la barra en ocultación automática mientras estaban activas).
                    int saved;
                    if (int.TryParse(File.ReadAllText(StatePath).Trim(), out saved) && saved != cur) SetState(tray, saved);
                }
            }
            catch { }
            // Su modo de ocultación no se toca. Antes se forzaba la ocultación automática para que no reservara franja,
            // pero con una barra así las apps de tipo Chrome (VS Code, Brave...) dejan 2 px libres en los bordes de la
            // pantalla al maximizarse, y se veía una línea a los lados. La franja es la misma que reservan las islas.
            ClearStaleAutoHide(tray);
            SetGhost(tray, true);
            return true;
        }

        // Estado real de la barra original: ¿está en ocultación automática? (null si no hay barra)
        public static bool? LiveAutoHide()
        {
            IntPtr tray = FindTray();
            if (tray == IntPtr.Zero) return null;
            return (GetState(tray) & Native.ABS_AUTOHIDE) != 0;
        }

        // Solo esconde la ventana de la barra original, sin tocar su modo de ocultación.
        public static void HideWindow()
        {
            IntPtr tray = FindTray();
            if (tray != IntPtr.Zero) SetGhost(tray, true);
        }

        // ¿Se ve la barra original? (Windows la repone a veces por su cuenta, o estrena ventana.)
        public static bool IsTrayVisible()
        {
            IntPtr tray = FindTray();
            return tray != IntPtr.Zero && !(IsGhost(tray) && Native.IsWindowVisible(tray));
        }

        public static void Restore()
        {
            IntPtr tray = FindTray();
            int original = 0;
            bool hadState = false;
            try
            {
                if (File.Exists(StatePath))
                {
                    int.TryParse(File.ReadAllText(StatePath).Trim(), out original);
                    hadState = true;
                }
            }
            catch { }

            if (tray != IntPtr.Zero)
            {
                SetGhost(tray, false);
                if (hadState) SetState(tray, original);
                if ((original & Native.ABS_AUTOHIDE) == 0) Native.ShowWindow(tray, Native.SW_SHOWNA);
            }
            try { if (hadState) File.Delete(StatePath); } catch { }
        }

        public static bool IsHiddenByUs() { return File.Exists(StatePath); }

        // Estado que tenía la barra original antes de ocultarla: ¿estaba en ocultación automática?
        public static bool OriginalAutoHide()
        {
            try
            {
                int st;
                if (File.Exists(StatePath) && int.TryParse(File.ReadAllText(StatePath).Trim(), out st)) return (st & Native.ABS_AUTOHIDE) != 0;
            }
            catch { }
            return false;
        }

        // El usuario cambió la ocultación automática en Configuración: es el estado que se restaurará al salir.
        public static void SetOriginalAutoHide(bool on)
        {
            try
            {
                int st = 0;
                if (!File.Exists(StatePath)) return;
                int.TryParse(File.ReadAllText(StatePath).Trim(), out st);
                st = on ? st | Native.ABS_AUTOHIDE : st & ~Native.ABS_AUTOHIDE;
                File.WriteAllText(StatePath, st.ToString());
            }
            catch { }
        }

        public static bool BarRegistered(IntPtr hwnd) { return bars.Contains(hwnd); }

        // ---- AppBar: reserva la franja de las islas para que las ventanas maximizadas no queden tapadas ----
        // Una por ventana: con islas en dos bordes (arriba y abajo) hacen falta dos.
        static readonly System.Collections.Generic.HashSet<IntPtr> bars = new System.Collections.Generic.HashSet<IntPtr>();

        public static bool RegisterBar(IntPtr hwnd, uint callbackMsg)
        {
            if (bars.Contains(hwnd)) return true;
            var d = NewData(hwnd);
            d.uCallbackMessage = callbackMsg;
            if (Native.SHAppBarMessage(Native.ABM_NEW, ref d) == UIntPtr.Zero) return false;
            bars.Add(hwnd);
            return true;
        }

        // Borde en el que está de verdad la barra original (null si el explorador aún no tiene barra).
        public static Edge? QueryEdge()
        {
            IntPtr tray = FindTray();
            if (tray == IntPtr.Zero) return null;
            var d = NewData(tray);
            if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref d) == UIntPtr.Zero || d.uEdge > 3) return null;
            return (Edge)d.uEdge;
        }

        // Borde deducido de dónde está la ventana de la barra original (por si el dato anterior no se actualiza).
        public static Edge? RectEdge()
        {
            IntPtr tray = FindTray();
            if (tray == IntPtr.Zero) return null;
            RECT r;
            Native.GetWindowRect(tray, out r);
            var mi = new MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
            if (r.Width <= 0 || r.Height <= 0 || !Native.GetMonitorInfoW(Native.MonitorFromWindow(tray, 2 /* NEAREST */), ref mi)) return null;
            RECT m = mi.rcMonitor;
            if (r.Width >= r.Height)
            {
                if (r.Width < m.Width * 6 / 10) return null;
                return (r.Top + r.Bottom) / 2 < (m.Top + m.Bottom) / 2 ? Edge.Top : Edge.Bottom;
            }
            if (r.Height < m.Height * 6 / 10) return null;
            return (r.Left + r.Right) / 2 < (m.Left + m.Right) / 2 ? Edge.Left : Edge.Right;
        }

        // Alineación real de la barra original: ¿está su botón de Inicio lejos del principio de la barra? (null si no
        // hay barra). Windows mueve ese botón al instante al cambiar "Alineación de la barra de tareas", mientras que
        // en el registro (TaskbarAl) el cambio no llega a aparecer.
        public static bool? LiveCentered()
        {
            IntPtr tray = FindTray();
            if (tray == IntPtr.Zero) return null;
            IntPtr start = Native.FindWindowExW(tray, IntPtr.Zero, "Start", null);
            RECT t, b;
            if (start == IntPtr.Zero || !Native.GetWindowRect(tray, out t) || !Native.GetWindowRect(start, out b)) return null;
            if (b.Width <= 0 || b.Height <= 0) return null;
            return t.Width >= t.Height ? b.Left - t.Left > b.Width : b.Top - t.Top > b.Height;
        }

        // Grosor real de la barra original a 96 dpi (0 si el explorador aún no tiene barra). Oculta conserva su tamaño,
        // y Windows se lo cambia al instante cuando el usuario elige otro tamaño de barra en Configuración.
        public static int Thickness()
        {
            IntPtr tray = FindTray();
            if (tray == IntPtr.Zero) return 0;
            RECT r;
            Native.GetWindowRect(tray, out r);
            if (r.Width <= 0 || r.Height <= 0)
            {
                var d = NewData(tray);
                if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref d) == UIntPtr.Zero) return 0;
                r = d.rc;
            }
            int px = Math.Min(r.Width, r.Height);
            if (px <= 0) return 0;
            int dpi = Native.GetMonitorDpi(Native.MonitorFromWindow(tray, 2 /* NEAREST */));
            return (int)Math.Round(px * 96.0 / dpi);
        }

        public static void SetBarPos(IntPtr hwnd, RECT monitor, int reserved, Edge edge)
        {
            if (!bars.Contains(hwnd)) return;
            var d = NewData(hwnd);
            d.uEdge = (uint)edge;
            d.rc = monitor;
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
            // La franja va siempre pegada al borde del monitor: si la barra de Windows aún ocupa ese borde (p. ej. mientras
            // se mueve de sitio), QUERYPOS nos desplazaría hacia dentro y quedaría un hueco doble.
            d.rc = monitor;
            switch (edge)
            {
                case Edge.Top: d.rc.Bottom = d.rc.Top + reserved; break;
                case Edge.Left: d.rc.Right = d.rc.Left + reserved; break;
                case Edge.Right: d.rc.Left = d.rc.Right - reserved; break;
                default: d.rc.Top = d.rc.Bottom - reserved; break;
            }
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);
        }

        // Lleva el área de trabajo hasta la franja de las islas si Windows reserva más en ese borde (los demás bordes
        // se dejan como están); con reserved = 0, hasta el borde de la pantalla (no queda ninguna isla en el borde de la
        // barra de Windows). Al quitar nuestra barra (UnregisterBar) Windows vuelve a calcularla entera.
        public static void FitWorkArea(RECT monitor, int reserved, Edge edge)
        {
            RECT wa = new RECT();
            if (!Native.SystemParametersInfoW(Native.SPI_GETWORKAREA, 0, ref wa, 0)) return;
            RECT want = wa;
            switch (edge)
            {
                case Edge.Top: want.Top = Math.Min(wa.Top, monitor.Top + reserved); break;
                case Edge.Left: want.Left = Math.Min(wa.Left, monitor.Left + reserved); break;
                case Edge.Right: want.Right = Math.Max(wa.Right, monitor.Right - reserved); break;
                default: want.Bottom = Math.Max(wa.Bottom, monitor.Bottom - reserved); break;
            }
            if (want.Left == wa.Left && want.Top == wa.Top && want.Right == wa.Right && want.Bottom == wa.Bottom) return;
            unfitted = wa;
            fitted = want;
            SetWorkArea(wa, want);
        }

        // Área de trabajo que tenía puesta Windows y la que se dejó en su lugar (vacías si no se ha ajustado).
        static RECT unfitted, fitted;

        static bool Same(RECT a, RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

        // Cambia el área de trabajo sin avisar (con SPIF_SENDCHANGE Windows la recalcula al momento y la deja como
        // estaba: bucle). Sin el aviso las ventanas que ya estaban maximizadas no se enteran y dejarían una rendija
        // con el escritorio, así que se recolocan aquí: las que ocupaban el área anterior pasan a ocupar la nueva.
        static void SetWorkArea(RECT from, RECT to)
        {
            if (!Native.SystemParametersInfoW(Native.SPI_SETWORKAREA, 0, ref to, 0)) return;
            IntPtr primary = Native.MonitorFromPoint(new POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */);
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || !Native.IsZoomed(h) || Native.IsIconic(h)) return true;
                if (Native.MonitorFromWindow(h, 2 /* NEAREST */) != primary) return true;
                RECT seen, r;
                if (Native.DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out seen, 16) != 0 || !Same(seen, from)) return true;
                Native.GetWindowRect(h, out r);
                Native.SetWindowPos(h, IntPtr.Zero, r.Left + to.Left - from.Left, r.Top + to.Top - from.Top,
                    r.Width + to.Width - from.Width, r.Height + to.Height - from.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                return true;
            }, IntPtr.Zero);
        }

        // Devuelve el área de trabajo a como la tenía Windows (antes de quitar nuestra barra o de pausar).
        public static void UnfitWorkArea()
        {
            if (fitted.Width <= 0) return;
            RECT wa = new RECT();
            if (Native.SystemParametersInfoW(Native.SPI_GETWORKAREA, 0, ref wa, 0) && Same(wa, fitted)) SetWorkArea(fitted, unfitted);
            fitted = unfitted = new RECT();
        }

        public static void UnregisterBar(IntPtr hwnd)
        {
            UnfitWorkArea();
            if (!bars.Remove(hwnd)) return;
            var d = NewData(hwnd);
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
        }

        public static void ForgetBar() { bars.Clear(); }
    }
}
