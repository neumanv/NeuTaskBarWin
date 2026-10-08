using System;
using System.Diagnostics;
using System.Threading;

namespace NeuTaskBar
{
    static class Program
    {
        const string Usage =
            "NeuTaskBar - barra de tareas de dos islas\r\n\r\n" +
            "  NeuTaskBar.exe              abre la aplicación\r\n" +
            "  NeuTaskBar.exe --engine     inicia directamente la barra de tareas (sin ventana)\r\n" +
            "  NeuTaskBar.exe --quit       cierra la barra de tareas en ejecución\r\n" +
            "  NeuTaskBar.exe --restore    restaura la barra de tareas original (por si quedó oculta)\r\n" +
            "  NeuTaskBar.exe --autostart on|off   activa/desactiva el inicio con Windows\r\n";

        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
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
                    Native.MessageBoxW(IntPtr.Zero, Usage, "NeuTaskBar", 0x40);
                    return 0;
            }
        }

        static int RunEngine()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\NeuTaskBar.Single", out created))
            {
                if (!created)
                {
                    // Ya hay una instancia: si está en pausa, la reanuda.
                    IntPtr h = Native.FindWindowW(App.HostClass, null);
                    if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_RESUME, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => EmergencyRestore();
                AppDomain.CurrentDomain.ProcessExit += (s, e) => EmergencyRestore();

                StartGuard();
                Gfx.Init();
                try { return App.Run(); }
                finally { EmergencyRestore(); Gfx.Shutdown(); }
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
