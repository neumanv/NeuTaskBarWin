using System;
using System.Diagnostics;
using System.Threading;

namespace NeuTaskBar
{
    static class Program
    {
        static string Usage
        {
            get
            {
                return L.T("NeuTaskBar - barra de tareas de dos islas") + "\r\n\r\n" +
                    "  NeuTaskBar.exe              " + L.T("abre la aplicación") + "\r\n" +
                    "  NeuTaskBar.exe --engine     " + L.T("inicia directamente la barra de tareas (sin ventana)") + "\r\n" +
                    "  NeuTaskBar.exe --quit       " + L.T("cierra la barra de tareas en ejecución") + "\r\n" +
                    "  NeuTaskBar.exe --restore    " + L.T("restaura la barra de tareas original (por si quedó oculta)") + "\r\n" +
                    "  NeuTaskBar.exe --autostart on|off   " + L.T("activa/desactiva el inicio con Windows") + "\r\n";
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--engine") Log("arranque del motor (pid " + Process.GetCurrentProcess().Id + ")");
            switch (mode)
            {
                case "":
                case "--settings":
                    return SettingsApp.Run();
                case "--engine":
                    return RunEngine();
                case "--register":
                    Config.RegisterUninstall();
                    StartMenu.Create();
                    return 0;
                case "--guard":
                    uint pid;
                    if (args.Length > 1 && uint.TryParse(args[1], out pid)) Guard(pid);
                    return 0;
                case "--restore":
                    ShellTaskbar.Restore();
                    IntPtr tray = ShellTaskbar.FindTray();
                    if (tray != IntPtr.Zero) Native.ShowWindow(tray, Native.SW_SHOWNA);
                    return 0;
                case "--quit":
                    {
                        IntPtr h = Native.FindWindowW(App.HostClass, null);
                        if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
                        return 0;
                    }
                case "--autostart":
                    Config.SetAutoStart(args.Length > 1 && args[1].Equals("on", StringComparison.OrdinalIgnoreCase));
                    return 0;
                default:
                    Config.Load();
                    Native.MessageBoxW(IntPtr.Zero, Usage, "NeuTaskBar", 0x40);
                    return 0;
            }
        }

        // Registro mínimo para diagnosticar si el inicio con Windows llega a lanzar el motor.
        internal static void Log(string text)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Config.DataDir);
                string path = System.IO.Path.Combine(Config.DataDir, "startup.log");
                if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 20000) System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { }
        }

        static int RunEngine()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\NeuTaskBar.Single", out created))
            {
                if (!created)
                {
                    Log("ya había una instancia; se reanuda");
                    // Ya hay una instancia: si está en pausa, la reanuda.
                    IntPtr h = Native.FindWindowW(App.HostClass, null);
                    if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_RESUME, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log("excepción no controlada: " + e.ExceptionObject); EmergencyRestore(); };
                AppDomain.CurrentDomain.ProcessExit += (s, e) => EmergencyRestore();

                StartGuard();
                Gfx.Init();
                try { return App.Run(); }
                finally { Log("motor finalizado"); EmergencyRestore(); Gfx.Shutdown(); }
            }
        }

        static void EmergencyRestore()
        {
            try { if (ShellTaskbar.IsHiddenByUs()) ShellTaskbar.Restore(); } catch { }
        }

        // Proceso vigía: si el motor muere de forma abrupta, devuelve la barra de Windows a su estado original.
        static void StartGuard()
        {
            try
            {
                var psi = new ProcessStartInfo(Config.ExePath, "--guard " + Process.GetCurrentProcess().Id);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        static void Guard(uint parentPid)
        {
            IntPtr h = Native.OpenProcess(Native.SYNCHRONIZE, false, parentPid);
            if (h == IntPtr.Zero) return;
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            Native.WaitForSingleObject(h, 0xFFFFFFFF);
            Native.CloseHandle(h);
            Thread.Sleep(400);
            // Si ya arrancó otra instancia, no toca nada.
            if (Native.FindWindowW(App.HostClass, null) != IntPtr.Zero) return;
            if (ShellTaskbar.IsHiddenByUs()) ShellTaskbar.Restore();
        }
    }
}
