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
                // Conserva el estado original si quedó de una ejecución anterior que no se cerró limpiamente.
                if (!File.Exists(StatePath)) File.WriteAllText(StatePath, cur.ToString());
            }
            catch { }
            if ((cur & Native.ABS_AUTOHIDE) == 0) SetState(tray, cur | Native.ABS_AUTOHIDE);
            Native.ShowWindow(tray, Native.SW_HIDE);
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
            if (tray != IntPtr.Zero) Native.ShowWindow(tray, Native.SW_HIDE);
        }

        public static bool IsTrayVisible()
        {
            IntPtr tray = FindTray();
            return tray != IntPtr.Zero && Native.IsWindowVisible(tray);
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

        public static bool BarRegistered { get { return barRegistered; } }

        // ---- AppBar: reserva la franja inferior para que las ventanas maximizadas no queden tapadas ----
        static bool barRegistered;

        public static bool RegisterBar(IntPtr hwnd, uint callbackMsg)
        {
            if (barRegistered) return true;
            var d = NewData(hwnd);
            d.uCallbackMessage = callbackMsg;
            barRegistered = Native.SHAppBarMessage(Native.ABM_NEW, ref d) != UIntPtr.Zero;
            return barRegistered;
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

        public static void SetBarPos(IntPtr hwnd, RECT monitor, int reserved, Edge edge)
        {
            if (!barRegistered) return;
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

        public static void UnregisterBar(IntPtr hwnd)
        {
            if (!barRegistered) return;
            var d = NewData(hwnd);
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
            barRegistered = false;
        }

        public static void ForgetBar() { barRegistered = false; }
    }
}
