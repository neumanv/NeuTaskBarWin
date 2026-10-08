using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace NeuTaskBar
{
    static class Diag
    {
        // Registro minimo de errores inesperados en la carpeta de datos (se recorta si crece demasiado).
        public static void Log(Exception ex)
        {
            try
            {
                string dir = Config.DataDir;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 200000) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    static class Config
    {
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeuTaskBar");

        public static string ExePath { get { return System.Reflection.Assembly.GetEntryAssembly().Location; } }

        // Valores a 96 dpi; se escalan con el DPI del monitor.
        public static int Height = 48;
        public static int Margin = 0;
        public static int IconSize = 24;
        // Tinte del acrílico, formato AARRGGBB. Más alfa = más opaco.
        public static uint Tint = 0x8C1A1A1A;
        // Botones junto al de Inicio según los ajustes de Windows (la barra original está oculta, así que se dibujan aquí).
        public static bool ShowSearch, ShowTaskView, ShowWidgets;

        // Lee los botones que el usuario activa en Configuración > Personalización > Barra de tareas.
        // Devuelve true si algo cambió.
        public static bool ReadWindowsTaskbarButtons()
        {
            bool search = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0) != 0;
            const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            bool taskView = ReadDword(adv, "ShowTaskViewButton", 1) != 0;
            bool widgets = ReadDword(adv, "TaskbarDa", 1) != 0;
            bool changed = search != ShowSearch || taskView != ShowTaskView || widgets != ShowWidgets;
            ShowSearch = search; ShowTaskView = taskView; ShowWidgets = widgets;
            return changed;
        }

        static int ReadDword(string key, string name, int def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : def;
                }
            }
            catch { return def; }
        }

        public static void Load()
        {
            try
            {
                string path = Path.Combine(DataDir, "config.ini");
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    switch (key)
                    {
                        case "height": if (int.TryParse(val, out n) && n >= 32 && n <= 96) Height = n; break;
                        case "margin": if (int.TryParse(val, out n) && n >= 0 && n <= 64) Margin = n; break;
                        case "iconsize": if (int.TryParse(val, out n) && n >= 16 && n <= 48) IconSize = n; break;
                        case "tint":
                            uint t;
                            if (uint.TryParse(val.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out t)) Tint = t;
                            break;
                    }
                }
            }
            catch { }
        }

        public static string InstallDir { get { return Path.GetDirectoryName(ExePath); } }

        // Acceso directo en el menú Inicio: es lo que permite encontrar la app al buscar en Windows.
        public static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NeuTaskBar.lnk"); }
        }

        public static void RemoveStartMenuShortcut()
        {
            try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); } catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "NeuTaskBar";

        public static bool GetAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunName, "\"" + ExePath + "\" --engine");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NeuTaskBar";

        // Registra la app en Configuracion > Aplicaciones para poder desinstalarla (si Uninstall.exe esta junto al exe).
        public static void RegisterUninstall()
        {
            try
            {
                string un = Path.Combine(InstallDir, "Uninstall.exe");
                if (!File.Exists(un)) return;
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "NeuTaskBar");
                    k.SetValue("DisplayVersion", "1.0");
                    k.SetValue("Publisher", "NeuTaskBar");
                    k.SetValue("DisplayIcon", ExePath);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + un + "\"");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void UnregisterUninstall()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        }
    }
}
