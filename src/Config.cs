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

    // Borde de la pantalla donde está la barra (mismos valores que ABE_* de Windows).
    enum Edge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    static class Config
    {
        static bool edgeKnown;
        static Edge taskbarEdge = Edge.Bottom;

        // Posición de la barra de tareas elegida en Windows: el borde en el que está de verdad la barra original.
        // Windows mueve su barra al instante, pero guarda el ajuste en el registro (TaskbarLocation, StuckRects3) con
        // retraso, así que el registro solo se usa mientras el explorador aún no tiene barra (p. ej. al iniciar sesión).
        public static Edge ReadTaskbarEdge()
        {
            Edge? live = ShellTaskbar.QueryEdge() ?? ShellTaskbar.RectEdge();
            if (live.HasValue)
            {
                taskbarEdge = live.Value;
                edgeKnown = true;
            }
            else if (!edgeKnown)
            {
                int loc = ReadDword(AdvKey, "TaskbarLocation", -1);   // 0 izq., 1 arriba, 2 dcha., 3 abajo
                taskbarEdge = loc >= 0 && loc <= 3 ? (Edge)loc : StuckEdge();
            }
            return taskbarEdge;
        }

        // Borde guardado en StuckRects3 (byte 12): último recurso.
        static Edge StuckEdge()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3"))
                {
                    var data = k == null ? null : k.GetValue("Settings") as byte[];
                    if (data != null && data.Length > 12 && data[12] <= 3) return (Edge)data[12];
                }
            }
            catch { }
            return Edge.Bottom;
        }

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

        // Resto de ajustes de Configuración > Personalización > Barra de tareas que la barra obedece.
        public static int SearchMode;            // 0 oculto, 1 solo icono, 2 cuadro de búsqueda, 3 icono y etiqueta
        public static bool Center = true;        // alineación de la barra: centro (true) o inicio
        public static int Combine;               // combinar botones y ocultar etiquetas: 0 siempre, 1 con la barra llena, 2 nunca
        public static int SmallButtons = 2;      // botones más pequeños: 0 siempre, 1 nunca, 2 con la barra llena (valor por defecto de Windows)
        public static bool ClockSeconds;         // segundos en el reloj
        public static bool ShowClock = true;     // fecha y hora en la bandeja
        public static bool Flashing = true;      // parpadeo de las apps que reclaman atención
        public static bool ShowDesktopCorner = true;   // esquina de la barra para mostrar el escritorio (activada por defecto en Windows)
        public static bool EndTask;              // "Finalizar tarea" en el menú del botón
        public static bool ShowChevron = true;   // menú de iconos ocultos
        public static bool TouchKeyboard;        // icono del teclado táctil
        public static bool AutoHide;             // ocultar automáticamente la barra
        static string winSig = "";

        const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        // Lee lo que el usuario elige en Configuración > Personalización > Barra de tareas.
        // Devuelve true si algo cambió.
        public static bool ReadWindowsTaskbarSettings()
        {
            SearchMode = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0);
            if (SearchMode < 0 || SearchMode > 3) SearchMode = 1;
            ShowSearch = SearchMode != 0;
            ShowTaskView = ReadDword(AdvKey, "ShowTaskViewButton", 1) != 0;
            ShowWidgets = ReadDword(AdvKey, "TaskbarDa", 1) != 0;
            Center = ReadDword(AdvKey, "TaskbarAl", 1) != 0;
            Combine = ReadDword(AdvKey, "TaskbarGlomLevel", 0);
            if (Combine < 0 || Combine > 2) Combine = 0;
            SmallButtons = ReadDword(AdvKey, "IconSizePreference", 2);
            if (SmallButtons < 0 || SmallButtons > 2) SmallButtons = 2;
            ClockSeconds = ReadDword(AdvKey, "ShowSecondsInSystemClock", 0) != 0;
            ShowClock = ReadDword(AdvKey, "ShowSystrayDateTimeValueName", 1) != 0;
            Flashing = ReadDword(AdvKey, "TaskbarFlashing", 1) != 0;
            ShowDesktopCorner = ReadDword(AdvKey, "TaskbarSd", 1) != 0;
            EndTask = ReadDword(AdvKey + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 0) != 0;
            ShowChevron = ReadDword(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify",
                "SystemTrayChevronVisibility", 1) != 0;
            TouchKeyboard = ReadDword(@"Software\Microsoft\TabletTip\1.7", "TipbandDesiredVisibility", 0) == 1;


            string sig = SearchMode + "," + ShowTaskView + "," + ShowWidgets + "," + Center + "," + Combine + "," + SmallButtons + ","
                + ClockSeconds + "," + ShowClock + "," + Flashing + "," + ShowDesktopCorner + "," + EndTask + "," + ShowChevron + ","
                + TouchKeyboard;
            bool changed = sig != winSig;
            winSig = sig;
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

        // Windows retrasa ~10 s las apps de inicio tras mostrar el escritorio; con 0 arrancan de inmediato.
        const string SerializeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
        const string DelayName = "StartupDelayInMSec";

        // Si el usuario (o el Administrador de tareas) desactivó la entrada, Windows la ignora aunque exista en Run.
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

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
            try
            {
                if (on)
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(SerializeKey))
                        k.SetValue(DelayName, 0, RegistryValueKind.DWord);
                    using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                        if (k != null) k.DeleteValue(RunName, false);
                }
                else
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(SerializeKey, true))
                        if (k != null) k.DeleteValue(DelayName, false);
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
