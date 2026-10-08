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

        public static void SetBarPos(IntPtr hwnd, RECT monitor, int reservedHeight)
        {
            if (!barRegistered) return;
            var d = NewData(hwnd);
            d.uEdge = Native.ABE_BOTTOM;
            d.rc = monitor;
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
            d.rc.Top = d.rc.Bottom - reservedHeight;
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
