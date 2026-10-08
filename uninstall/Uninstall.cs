using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NeuTaskBar
{
    // Desinstalador: detiene la barra, devuelve la barra de Windows a su estado original y elimina
    // el inicio automático, el registro en Aplicaciones, los datos (opcional) y los ejecutables.
    static class Uninstaller
    {
        const string HostClass = "NeuTaskBar.Host";
        const uint WM_APP_QUIT = Native.WM_APP + 4;
        const uint MB_YESNO = 4, MB_ICONQUESTION = 0x20, MB_ICONINFORMATION = 0x40, MB_ICONWARNING = 0x30;
        const int IDYES = 6;

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = false;
            foreach (var a in args) if (a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) silent = true;

            string dir = Config.InstallDir;
            string mainExe = Path.Combine(dir, "NeuTaskBar.exe");

            bool removeData = true;
            if (!silent)
            {
                int r = Native.MessageBoxW(IntPtr.Zero,
                    "¿Quieres desinstalar NeuTaskBar?\r\n\r\nSe detendrá la barra de tareas personalizada, volverá la barra original de Windows y se eliminarán los programas de:\r\n" + dir,
                    "Desinstalar NeuTaskBar", MB_YESNO | MB_ICONQUESTION);
                if (r != IDYES) return 0;
                int d = Native.MessageBoxW(IntPtr.Zero,
                    "¿Eliminar también tus ajustes y las aplicaciones fijadas?\r\n\r\n(Si eliges No se conservan por si vuelves a instalarla.)",
                    "Desinstalar NeuTaskBar", MB_YESNO | MB_ICONQUESTION);
                removeData = d == IDYES;
            }

            // 1. Cierra la ventana de la aplicación y detiene la barra.
            IntPtr settings = Native.FindMainWindow();
            if (settings != IntPtr.Zero) Native.PostMessageW(settings, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            IntPtr host = Native.FindWindowW(HostClass, null);
            if (host != IntPtr.Zero) Native.PostMessageW(host, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
            WaitForProcessesToExit(5000);

            // 2. Devuelve la barra de Windows a su estado original (por si el cierre no fue limpio).
            ShellTaskbar.Restore();
            IntPtr tray = ShellTaskbar.FindTray();
            if (tray != IntPtr.Zero) Native.ShowWindow(tray, Native.SW_SHOWNA);

            // 3. Quita el inicio con Windows y el registro en Aplicaciones.
            Config.SetAutoStart(false);
            Config.UnregisterUninstall();
            Config.RemoveStartMenuShortcut();

            // 4. Elimina los datos del usuario (opcional).
            if (removeData)
            {
                TryDeleteDir(Config.DataDir);
            }

            // 5. Elimina NeuTaskBar.exe y, al terminar, este mismo desinstalador.
            bool deleted = TryDelete(mainExe);
            ScheduleSelfDelete();

            if (!silent)
            {
                if (deleted)
                    Native.MessageBoxW(IntPtr.Zero, "NeuTaskBar se ha desinstalado correctamente.", "Desinstalar NeuTaskBar", MB_ICONINFORMATION);
                else
                    Native.MessageBoxW(IntPtr.Zero,
                        "NeuTaskBar se ha detenido, pero no se pudo borrar NeuTaskBar.exe (está en uso). Bórralo manualmente:\r\n" + mainExe,
                        "Desinstalar NeuTaskBar", MB_ICONWARNING);
            }
            return 0;
        }

        // Espera a que terminen los procesos NeuTaskBar (motor y vigía); si no, los cierra.
        static void WaitForProcessesToExit(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Process.GetProcessesByName("NeuTaskBar").Length == 0) return;
                Thread.Sleep(150);
            }
            foreach (var p in Process.GetProcessesByName("NeuTaskBar"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }

        static bool TryDelete(string path)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.Delete(path);
                    return true;
                }
                catch { Thread.Sleep(150); }
            }
            return !File.Exists(path);
        }

        static void TryDeleteDir(string path)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    Directory.Delete(path, true);
                    return;
                }
                catch { Thread.Sleep(200); }
            }
        }

        static void ScheduleSelfDelete()
        {
            try
            {
                string self = Config.ExePath;
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + self + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }
    }
}
