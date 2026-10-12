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

        // Tamaño de la barra de tareas elegido en Windows. Height e IconSize valen para el tamaño normal (48 a 96 dpi);
        // con otro tamaño las islas y sus iconos se escalan en la misma proporción que la barra original.
        public const int StdBarHeight = 48;
        public static int WinBarHeight = StdBarHeight;   // grosor de la barra de Windows a 96 dpi
        public static int TaskbarSize;                   // ajuste "Tamaño de la barra de tareas": 0 normal, 1 pequeña
        static int smallBarHeight = 32;                  // grosor de la barra pequeña (se corrige con el que se mida)

        public static int BarHeight { get { return Math.Max(24, (Height * WinBarHeight + StdBarHeight / 2) / StdBarHeight); } }
        public static int BarIconSize { get { return Math.Max(16, (IconSize * WinBarHeight + StdBarHeight / 2) / StdBarHeight); } }

        // Fija el grosor de la barra de Windows: el medido en la barra original o, si no se pudo medir (0),
        // el que corresponde al tamaño elegido en Configuración. Devuelve true si cambió.
        public static bool SetWinBarHeight(int measured)
        {
            if (measured < 24 || measured > 96) measured = TaskbarSize == 1 ? smallBarHeight : StdBarHeight;
            else if (TaskbarSize == 1 && measured < StdBarHeight) smallBarHeight = measured;
            if (measured == WinBarHeight) return false;
            WinBarHeight = measured;
            return true;
        }

        // Tinte del acrílico, formato AARRGGBB. Más alfa = más opaco; con alfa 0 la isla es transparente del todo.
        public const uint DefaultTint = 0x8C1A1A1A;
        public static uint Tint = DefaultTint;
        // Apariencia: color propio de cada isla (si no se ha elegido, vale el general).
        public static uint TintLeft = DefaultTint, TintRight = DefaultTint;
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
        public static bool AutoHide = false;     // ocultar automáticamente la barra
        static string winSig = "";

        // Apariencia: islas que se esconden hasta acercar el ratón, cada una por su cuenta (se guardan en config.ini).
        public static bool HideLeft, HideRight;

        // Posición: las dos islas unidas en una sola barra, y la esquina de pantalla elegida para cada una
        // (-1 = donde manda la barra de Windows; 0 arriba-izquierda, 1 arriba-derecha, 2 abajo-izquierda, 3 abajo-derecha).
        public static bool Join;
        public static int CornerLeft = -1, CornerRight = -1;
        static readonly string[] CornerNames = { "tl", "tr", "bl", "br" };

        public static string CornerName(int corner) { return corner >= 0 && corner < 4 ? CornerNames[corner] : ""; }

        static int ParseCorner(string val)
        {
            return val.Length < 2 ? -1 : Array.IndexOf(CornerNames, val.Substring(0, 2).ToLowerInvariant());
        }

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
            // La alineación se mira en la barra original; el registro solo vale mientras el explorador no tiene barra.
            Center = ShellTaskbar.LiveCentered() ?? (ReadDword(AdvKey, "TaskbarAl", 1) != 0);
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
            // No entra en la firma: el tamaño se sigue midiendo la barra original (App.FollowTaskbarSize).
            TaskbarSize = ReadDword(AdvKey, "TaskbarSize", 0);

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
            HideLeft = HideRight = Join = false;
            CornerLeft = CornerRight = -1;
            Tint = DefaultTint;
            bool ownLeft = false, ownRight = false;
            string lang = "";
            try
            {
                string path = Path.Combine(DataDir, "config.ini");
                foreach (string raw in File.Exists(path) ? File.ReadAllLines(path) : new string[0])
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
                        case "tint": ParseColor(val, ref Tint); break;
                        case "tintleft": ownLeft = ParseColor(val, ref TintLeft); break;
                        case "tintright": ownRight = ParseColor(val, ref TintRight); break;
                        case "hideleft": HideLeft = val.StartsWith("1"); break;
                        case "hideright": HideRight = val.StartsWith("1"); break;
                        case "join": Join = val.StartsWith("1"); break;
                        case "lang": lang = val.Length > 2 ? val.Substring(0, 2) : val; break;
                        case "cornerleft": CornerLeft = ParseCorner(val); break;
                        case "cornerright": CornerRight = ParseCorner(val); break;
                    }
                }
            }
            catch { }
            L.Set(lang);   // sin elegir, el idioma de Windows
            if (!ownLeft) TintLeft = Tint;
            if (!ownRight) TintRight = Tint;
        }

        // Color AARRGGBB en hexadecimal. Con solo RRGGBB se le da la opacidad de los colores de Apariencia.
        static bool ParseColor(string val, ref uint color)
        {
            val = val.TrimStart('#');
            int n = 0;
            while (n < val.Length && Uri.IsHexDigit(val[n])) n++;
            uint t;
            if (!uint.TryParse(val.Substring(0, n), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out t)) return false;
            color = n <= 6 ? t | 0xCC000000 : t;
            return true;
        }

        // Guarda una clave en config.ini conservando el resto del archivo (las demás claves y los comentarios).
        public static void Save(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string path = Path.Combine(DataDir, "config.ini");
                var lines = new System.Collections.Generic.List<string>();
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0 || line[0] == '#' || line[0] == ';') continue;
                    if (line.Substring(0, eq).Trim().ToLowerInvariant() != key) continue;
                    if (found) lines.RemoveAt(i--);
                    else { lines[i] = key + "=" + value; found = true; }
                }
                if (!found) lines.Add(key + "=" + value);
                File.WriteAllLines(path, lines.ToArray());
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

        const string TaskName = "NeuTaskBar";

        // Ejecuta schtasks.exe sin ventana; devuelve su código de salida (-1 si no pudo lanzarse).
        static int Schtasks(string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) return -1;
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        static bool TaskExists() { return Schtasks("/Query /TN " + TaskName) == 0; }

        // Windows no siempre lanza las entradas de la clave Run, así que el inicio se hace con una tarea al iniciar sesión.
        static bool CreateTask()
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><StartWhenAvailable>true</StartWhenAvailable></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(ExePath) + "</Command>" +
                "<Arguments>--engine</Arguments></Exec></Actions></Task>";
            string file = Path.Combine(Path.GetTempPath(), "NeuTaskBar-task.xml");
            try
            {
                File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
                return Schtasks("/Create /TN " + TaskName + " /XML \"" + file + "\" /F") == 0;
            }
            catch { return false; }
            finally { try { File.Delete(file); } catch { } }
        }

        public static bool GetAutoStart()
        {
            if (TaskExists()) return true;
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
                    // Con la tarea programada basta; la entrada Run antigua se retira para no duplicar el inicio.
                    if (on && !CreateTask()) k.SetValue(RunName, "\"" + ExePath + "\" --engine");
                    else k.DeleteValue(RunName, false);
                    if (!on) Schtasks("/Delete /TN " + TaskName + " /F");
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
