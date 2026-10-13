using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NeuTaskBar
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkCom { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile(out IntPtr file);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int Commit();
    }

    static class AppUserModel
    {
        public const string AppsFolder = "shell:AppsFolder\\";

        // AppUserModelID de una ventana: Windows agrupa los botones de la barra por este id.
        public static string Get(IntPtr hwnd)
        {
            try
            {
                Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                object o;
                if (Native.SHGetPropertyStoreForWindow(hwnd, ref iid, out o) != 0 || o == null) return "";
                string r = ReadId((IPropertyStore)o);
                Marshal.ReleaseComObject(o);
                return r;
            }
            catch { return ""; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

        // AppUserModelID del proceso (apps empaquetadas): así identifica Windows a Fotos, Calculadora, etc.
        public static string ForProcess(uint pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                uint len = 0;
                GetApplicationUserModelId(h, ref len, null);
                if (len == 0 || len > 512) return "";
                var sb = new StringBuilder((int)len);
                return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : "";
            }
            catch { return ""; }
            finally { Native.CloseHandle(h); }
        }

        static readonly Dictionary<string, string> displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Nombre que muestra Windows para la app (p. ej. "Fotos").
        public static string DisplayName(string aumid)
        {
            string name;
            if (displayNames.TryGetValue(aumid, out name)) return name;
            name = "";
            try
            {
                IntPtr pidl;
                uint attrs;
                if (Native.SHParseDisplayName(AppsFolder + aumid, IntPtr.Zero, out pidl, 0, out attrs) == 0 && pidl != IntPtr.Zero)
                {
                    var fi = new SHFILEINFO();
                    Native.SHGetFileInfoPidl(pidl, 0, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), Native.SHGFI_PIDL | 0x200 /* SHGFI_DISPLAYNAME */);
                    Native.ILFree(pidl);
                    name = fi.szDisplayName ?? "";
                }
            }
            catch { }
            displayNames[aumid] = name;
            return name;
        }

        // AppUserModelID guardado en un acceso directo (.lnk).
        public static string FromLink(object shellLink)
        {
            try { return ReadId((IPropertyStore)shellLink); }
            catch { return ""; }
        }

        static string ReadId(IPropertyStore store)
        {
            return ReadString(store, new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        }

        // Lee una propiedad de tipo cadena de un objeto con IPropertyStore (acceso directo, ventana...).
        public static string ReadString(object propertyStoreOwner, Guid fmtid, uint pid)
        {
            try { return ReadString((IPropertyStore)propertyStoreOwner, fmtid, pid); }
            catch { return ""; }
        }

        static string ReadString(IPropertyStore store, Guid fmtid, uint pid)
        {
            IntPtr pv = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                var key = new PROPERTYKEY { fmtid = fmtid, pid = pid };
                string result = "";
                if (store.GetValue(ref key, pv) == 0 && Marshal.ReadInt16(pv) == 31 /* VT_LPWSTR */)
                    result = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) ?? "";
                Native.PropVariantClear(pv);
                return result;
            }
            finally { Marshal.FreeHGlobal(pv); }
        }
    }

    sealed class AppItem
    {
        public string Key;              // identidad del grupo: "aumid:<id>" o ruta del exe en minúsculas
        public string Name;             // nombre mostrado
        public string LaunchPath;       // acceso directo o exe con el que se lanza
        public string ExePath;
        public bool Pinned;
        public List<IntPtr> Windows = new List<IntPtr>(); // orden z (más reciente primero)
        public string Id;               // identidad estable para animaciones (no cambia al abrir/cerrar la app)
        public IconImage Image;         // imagen GDI+ (propiedad de la caché)
        public bool Active;
        public bool Flashing;
        public string Title;            // título de la ventana más reciente
        public string Label;            // texto junto al icono (solo con "no combinar botones"); null = sin etiqueta
        public string Aumid = "";       // AppUserModelID de la ventana (si lo declara)
        public bool Packaged;           // app empaquetada de la Tienda: icono y arranque salen del paquete, no del exe
        // Grupo de apps ancladas que comparten un botón: sus apps y la lista de PinStore a la que corresponde (su
        // lista de ventanas reúne entonces las de todas ellas). En una app normal, null.
        public List<AppItem> Members;
        public List<string> Group;

        public bool Running { get { return Windows.Count > 0; } }
    }

    sealed class PinEntry
    {
        public string Path;     // .lnk o .exe
        public string Exe;      // exe resuelto en minúsculas ("" si no se pudo resolver)
        public string Aumid = "";   // AppUserModelID en minúsculas ("" si no tiene)
        public string Identity = "";    // programa al que lleva de verdad (Shortcut.AppIdentity)
        public string Name;
    }

    static class Shortcut
    {
        public static void Resolve(string path, out string target, out string name)
        {
            string aumid;
            Resolve(path, out target, out name, out aumid);
        }

        public static void Resolve(string path, out string target, out string name, out string aumid)
        {
            string args;
            Resolve(path, out target, out name, out aumid, out args);
        }

        // Identidad de una app, para emparejar lo anclado con lo abierto cuando la ruta del exe no coincide. Los
        // instaladores tipo Squirrel (Discord, Slack, Teams...) dejan el programa en una carpeta "app-<versión>" que
        // cambia con cada actualización, y sus accesos directos apuntan a un lanzador que lo abre
        // ("Update.exe --processStart Programa.exe"). Devuelve la ruta del programa, en minúsculas y sin la carpeta
        // de versión: la misma para el acceso directo y para el proceso en marcha.
        public static string AppIdentity(string exe, string args)
        {
            if (string.IsNullOrEmpty(exe)) return "";
            string path = exe.ToLowerInvariant();
            try
            {
                const string flag = "--processstart";
                string a = (args ?? "").ToLowerInvariant();
                int at = a.IndexOf(flag);
                if (at >= 0)
                {
                    // El lanzador abre el programa que se le nombra, que vive junto a él.
                    string rest = a.Substring(at + flag.Length).TrimStart(' ', '=');
                    int end = rest.StartsWith("\"") ? rest.IndexOf('"', 1) : rest.IndexOf(' ');
                    string program = (end < 0 ? rest : rest.Substring(0, end)).Trim('"', ' ');
                    if (program.Length > 0) path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFileName(program));
                }
                var parts = new List<string>(path.Split('\\'));
                parts.RemoveAll(s => s.Length > 4 && s.StartsWith("app-") && char.IsDigit(s[4]));
                return string.Join("\\", parts.ToArray());
            }
            catch { return path; }
        }

        public static void Resolve(string path, out string target, out string name, out string aumid, out string args)
        {
            target = "";
            aumid = "";
            args = "";
            name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                target = path;
                return;
            }
            try
            {
                var link = (IShellLinkW)new ShellLinkCom();
                ((IPersistFile)link).Load(path, 0);
                var sb = new StringBuilder(520);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                target = sb.ToString();
                var ab = new StringBuilder(2048);
                link.GetArguments(ab, ab.Capacity);
                args = ab.ToString();
                aumid = AppUserModel.FromLink(link);
                Marshal.ReleaseComObject(link);
            }
            catch { }
        }
    }

    // Lista de apps fijadas, persistida en %AppData%\NeuTaskBar\pins.txt (una ruta por línea).
    sealed class PinStore
    {
        public readonly List<PinEntry> Pins = new List<PinEntry>();
        static string FilePath { get { return System.IO.Path.Combine(Config.DataDir, "pins.txt"); } }

        public void Load()
        {
            Pins.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string p = line.Trim();
                        string name = null;
                        int bar = p.IndexOf('|');
                        if (bar > 0) { name = p.Substring(bar + 1); p = p.Substring(0, bar); }
                        if (p.Length == 0) continue;
                        if (p.StartsWith(AppUserModel.AppsFolder, StringComparison.OrdinalIgnoreCase) || File.Exists(p)) AddInternal(p, name);
                    }
                }
                else
                {
                    SeedFromWindows();
                    Save();
                }
            }
            catch { }
            LoadGroups();
        }

        void SeedFromWindows()
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(dir)) return;
            var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.lnk"));
            files.Sort((a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
            foreach (var f in files) AddInternal(f.FullName, null);
        }

        void AddInternal(string path, string displayName)
        {
            if (path.StartsWith(AppUserModel.AppsFolder, StringComparison.OrdinalIgnoreCase))
            {
                string id = path.Substring(AppUserModel.AppsFolder.Length);
                Pins.Add(new PinEntry { Path = path, Exe = "", Aumid = id.ToLowerInvariant(), Name = displayName ?? id });
                return;
            }
            string target, name, aumid, args;
            Shortcut.Resolve(path, out target, out name, out aumid, out args);
            Pins.Add(new PinEntry
            {
                Path = path, Exe = (target ?? "").ToLowerInvariant(), Aumid = (aumid ?? "").ToLowerInvariant(), Name = displayName ?? name,
                Identity = Shortcut.AppIdentity(target, args)
            });
        }

        // ¿Está ya anclada esa app? Vale el mismo exe, el mismo AppUserModelID o la misma app por otro camino (identity).
        public bool Contains(string exe, string aumid, string identity)
        {
            foreach (var p in Pins)
            {
                if (p.Exe.Length > 0 && p.Exe == exe) return true;
                if (p.Aumid.Length > 0 && p.Aumid == aumid) return true;
                if (p.Identity.Length > 0 && p.Identity == identity) return true;
            }
            return false;
        }

        public void Add(string path, string displayName)
        {
            AddInternal(path, displayName);
            Save();
        }

        public void ReorderByPaths(List<string> paths)
        {
            var result = new List<PinEntry>();
            foreach (string p in paths)
            {
                var e = Pins.Find(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase));
                if (e != null && !result.Contains(e)) result.Add(e);
            }
            foreach (var e in Pins) if (!result.Contains(e)) result.Add(e);
            Pins.Clear();
            Pins.AddRange(result);
            Save();
        }

        public void RemoveByPath(string path)
        {
            Pins.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            Save();
            LoadGroups();   // la app sale también del grupo en el que estuviera
            SaveGroups();
        }

        // ---- Grupos: varias apps ancladas (hasta MaxGroupSize) que en la barra ocupan un solo botón. Cada grupo es
        // la lista de rutas (PinEntry.Path) de sus apps, en el orden en que se añadieron. Se guardan en groups.txt, un
        // grupo por línea con las rutas separadas por "|" (un carácter que no cabe en una ruta).
        public const int MaxGroupSize = 4;
        public readonly List<List<string>> Groups = new List<List<string>>();
        static string GroupsPath { get { return System.IO.Path.Combine(Config.DataDir, "groups.txt"); } }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // Lee los grupos y los deja coherentes con lo anclado: sin apps que ya no lo están ni repetidas, con
        // MaxGroupSize como mucho y sin grupos vacíos.
        public void LoadGroups()
        {
            Groups.Clear();
            try
            {
                if (!File.Exists(GroupsPath)) return;
                var taken = new List<string>();
                foreach (string line in File.ReadAllLines(GroupsPath))
                {
                    var group = new List<string>();
                    foreach (string part in line.Split('|'))
                    {
                        string path = part.Trim();
                        var pin = Pins.Find(p => Same(p.Path, path));
                        if (pin == null || group.Count >= MaxGroupSize || taken.Exists(t => Same(t, path))) continue;
                        group.Add(pin.Path);
                        taken.Add(pin.Path);
                    }
                    if (group.Count > 0) Groups.Add(group);
                }
            }
            catch { }
        }

        void SaveGroups()
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                var lines = new List<string>();
                foreach (var g in Groups) lines.Add(string.Join("|", g.ToArray()));
                if (lines.Count == 0) { if (File.Exists(GroupsPath)) File.Delete(GroupsPath); }
                else File.WriteAllLines(GroupsPath, lines.ToArray());
            }
            catch { }
        }

        // Grupo al que pertenece una app anclada (null si a ninguno).
        public List<string> GroupOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var g in Groups) if (g.Exists(m => Same(m, path))) return g;
            return null;
        }

        // Nombre con el que se enseña un grupo: los de sus apps.
        public string GroupName(List<string> group)
        {
            var names = new List<string>();
            foreach (string path in group)
            {
                var pin = Pins.Find(p => Same(p.Path, path));
                if (pin != null) names.Add(pin.Name);
            }
            return string.Join(", ", names.ToArray());
        }

        // Empieza un grupo con una app anclada (si estaba en otro, sale de él).
        public void CreateGroup(string path)
        {
            if (Pins.Find(p => Same(p.Path, path)) == null) return;
            Detach(path);
            Groups.Add(new List<string> { path });
            SaveGroups();
        }

        // Añade una app anclada al grupo en el que está otra (member); false si no cabe o ya está en él.
        public bool AddToGroup(string member, string path)
        {
            var group = GroupOf(member);
            if (group == null || group.Count >= MaxGroupSize || group.Exists(m => Same(m, path))) return false;
            if (Pins.Find(p => Same(p.Path, path)) == null) return false;
            Detach(path);   // sale del grupo en el que estuviera
            group.Add(path);
            SaveGroups();
            return true;
        }

        // La app vuelve a tener su propio botón.
        public void RemoveFromGroup(string path)
        {
            Detach(path);
            SaveGroups();
        }

        // Deshace el grupo en el que está una app: todas vuelven a tener su botón.
        public void DissolveGroup(string member)
        {
            var group = GroupOf(member);
            if (group == null) return;
            Groups.Remove(group);
            SaveGroups();
        }

        void Detach(string path)
        {
            foreach (var g in Groups) g.RemoveAll(m => Same(m, path));
            Groups.RemoveAll(g => g.Count == 0);
        }

        // Para la ventana de la aplicación, que no lleva la lista de lo anclado: cuántos grupos hay, y deshacerlos todos.
        public static int CountGroups()
        {
            try
            {
                int n = 0;
                if (File.Exists(GroupsPath)) foreach (string line in File.ReadAllLines(GroupsPath)) if (line.Trim().Length > 0) n++;
                return n;
            }
            catch { return 0; }
        }

        public static void ClearGroups()
        {
            try { if (File.Exists(GroupsPath)) File.Delete(GroupsPath); } catch { }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Config.DataDir);
                var lines = new List<string>();
                foreach (var p in Pins)
                    lines.Add(p.Path.StartsWith(AppUserModel.AppsFolder, StringComparison.OrdinalIgnoreCase) ? p.Path + "|" + p.Name : p.Path);
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch { }
        }
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    // Iconos como imágenes GDI+ al tamaño exacto pedido al shell (los mismos que usa la barra de tareas de Windows).
    sealed class IconImages
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetObject(IntPtr h, int cb, ref BITMAP bm);
        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage);

        readonly Dictionary<string, IconImage> map = new Dictionary<string, IconImage>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<long, IconImage> hicons = new Dictionary<long, IconImage>();
        readonly List<IntPtr> buffers = new List<IntPtr>();
        readonly List<IconImage> all = new List<IconImage>();
        public int Px = 32;

        // Devuelve true si cambió el tamaño (y se vació la caché).
        public bool SetPx(int px)
        {
            if (px == Px) return false;
            Px = px;
            Clear();
            return true;
        }

        public void Clear()
        {
            foreach (var i in all) Gfx.Dispose(i);
            all.Clear();
            map.Clear();
            hicons.Clear();
            foreach (var b in buffers) Marshal.FreeHGlobal(b);
            buffers.Clear();
        }

        public IconImage Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            IconImage img;
            if (map.TryGetValue(path, out img)) return img;
            img = Load(path);
            map[path] = img;
            return img;
        }

        public IconImage FromHicon(IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return null;
            IconImage img;
            if (hicons.TryGetValue(hicon.ToInt64(), out img)) return img;
            img = Gfx.FromHicon(hicon);
            if (img != null) all.Add(img);
            if (hicons.Count > 64) hicons.Clear();
            hicons[hicon.ToInt64()] = img;
            return img;
        }

        IconImage Load(string path)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
                IShellItemImageFactory factory;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                SIZE size = new SIZE { cx = Px, cy = Px };
                int hr = factory.GetImage(size, 4 /* SIIGBF_ICONONLY */, out hbm);
                Marshal.ReleaseComObject(factory);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                return FromHBitmap(hbm);
            }
            catch { return null; }
            finally { if (hbm != IntPtr.Zero) Native.DeleteObject(hbm); }
        }

        IconImage FromHBitmap(IntPtr hbm)
        {
            var bm = new BITMAP();
            if (GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            IntPtr buf = Marshal.AllocHGlobal(w * h * 4);
            var bmi = new BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.biWidth = w;
            bmi.biHeight = -h; // de arriba hacia abajo
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            int lines = GetDIBits(dc, hbm, 0, (uint)h, buf, ref bmi, 0);
            Native.ReleaseDC(IntPtr.Zero, dc);
            if (lines == 0) { Marshal.FreeHGlobal(buf); return null; }

            // ¿Alfa premultiplicado? Si algún canal supera al alfa, es alfa directo.
            bool premultiplied = true, anyAlpha = false;
            for (int i = 0; i < w * h; i++)
            {
                uint px = unchecked((uint)Marshal.ReadInt32(buf, i * 4));
                uint a = px >> 24;
                if (a != 0) anyAlpha = true;
                if (((px >> 16) & 0xFF) > a || ((px >> 8) & 0xFF) > a || (px & 0xFF) > a) premultiplied = false;
            }
            if (!anyAlpha)
            {
                for (int i = 0; i < w * h; i++) Marshal.WriteInt32(buf, i * 4, Marshal.ReadInt32(buf, i * 4) | unchecked((int)0xFF000000));
            }
            var img = Gfx.FromScan0(w, h, buf, premultiplied || !anyAlpha);
            if (img == null) { Marshal.FreeHGlobal(buf); return null; }
            buffers.Add(buf);
            all.Add(img);
            return img;
        }
    }

    // Enumera las ventanas "de barra de tareas" y las agrupa por aplicación.
    sealed class WindowTracker
    {
        readonly PinStore pins;
        readonly IconImages icons;
        readonly IntPtr self1, self2;
        readonly List<string> runOrder = new List<string>();
        readonly Dictionary<uint, string> pidPath = new Dictionary<uint, string>();
        Dictionary<IntPtr, string> aumidCache = new Dictionary<IntPtr, string>();
        readonly Dictionary<uint, string> pidAumid = new Dictionary<uint, string>();
        readonly HashSet<IntPtr> flashing = new HashSet<IntPtr>();
        readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        public WindowTracker(PinStore pins, IconImages icons, IntPtr ignore1, IntPtr ignore2)
        {
            this.pins = pins;
            this.icons = icons;
            self1 = ignore1;
            self2 = ignore2;
        }

        public void Flash(IntPtr hwnd) { flashing.Add(hwnd); }

        // Orden manual de las apps abiertas no fijadas (arrastrar y soltar).
        public void SetRunOrder(List<string> keys)
        {
            var order = new List<string>(keys);
            foreach (string k in runOrder) if (!order.Contains(k)) order.Add(k);
            runOrder.Clear();
            runOrder.AddRange(order);
        }

        struct Found { public IntPtr Hwnd; public string Exe; public uint Pid; }

        public List<AppItem> Snapshot(out string signature)
        {
            var found = new List<Found>();
            var seenPids = new HashSet<uint>();
            Native.EnumWindows((h, l) =>
            {
                if (!IsTaskbarWindow(h)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == ownPid) return true;
                string exe = PathOfPid(pid);
                seenPids.Add(pid);
                found.Add(new Found { Hwnd = h, Exe = exe, Pid = pid });
                return true;
            }, IntPtr.Zero);

            // Descarta rutas de procesos que ya no existen (evita reutilización de PID).
            if (pidPath.Count > 0)
            {
                var stale = new List<uint>();
                foreach (var k in pidPath.Keys) if (!seenPids.Contains(k)) stale.Add(k);
                foreach (var k in stale) { pidPath.Remove(k); pidAumid.Remove(k); }
            }

            IntPtr fg = Native.GetForegroundWindow();
            IntPtr fgRoot = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOTOWNER);

            var groups = new Dictionary<string, AppItem>();
            var running = new List<AppItem>();
            var newAumids = new Dictionary<IntPtr, string>();
            foreach (var f in found)
            {
                string exe = f.Exe;
                bool host = exe.EndsWith("\\applicationframehost.exe", StringComparison.OrdinalIgnoreCase) || exe.Length == 0;
                string aumid;
                if (!aumidCache.TryGetValue(f.Hwnd, out aumid)) aumid = AppUserModel.Get(f.Hwnd);
                // Las ventanas del Explorador no declaran AUMID; Windows las agrupa bajo este id.
                if (aumid.Length == 0 && exe.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) aumid = "Microsoft.Windows.Explorer";
                // Apps empaquetadas (Fotos, etc.): su identidad es la del proceso.
                if (aumid.Length == 0 && !host)
                {
                    if (!pidAumid.TryGetValue(f.Pid, out aumid)) { aumid = AppUserModel.ForProcess(f.Pid); pidAumid[f.Pid] = aumid; }
                }
                newAumids[f.Hwnd] = aumid;
                string key = aumid.Length > 0 ? "aumid:" + aumid.ToLowerInvariant()
                    : host ? "hwnd:" + f.Hwnd.ToInt64() : exe.ToLowerInvariant();
                AppItem it;
                if (!groups.TryGetValue(key, out it))
                {
                    it = new AppItem { Key = key, ExePath = host ? "" : exe, Aumid = aumid };
                    it.Packaged = aumid.Length > 0 && (host || exe.IndexOf("\\windowsapps\\", StringComparison.OrdinalIgnoreCase) >= 0);
                    it.Title = Native.GetWindowText(f.Hwnd);
                    it.Name = host ? it.Title : Path.GetFileNameWithoutExtension(exe);
                    if (it.Packaged)
                    {
                        it.LaunchPath = AppUserModel.AppsFolder + aumid;
                        if (!host)
                        {
                            string display = AppUserModel.DisplayName(aumid);
                            if (display.Length > 0) it.Name = display;
                        }
                    }
                    groups[key] = it;
                    running.Add(it);
                }
                it.Windows.Add(f.Hwnd);
                if (f.Hwnd == fg || f.Hwnd == fgRoot) it.Active = true;
                if (flashing.Contains(f.Hwnd)) { if (it.Active) flashing.Remove(f.Hwnd); else if (Config.Flashing) it.Flashing = true; }
            }
            flashing.RemoveWhere(h => !Native.IsWindow(h));
            aumidCache = newAumids;

            // Orden estable de las apps en ejecución: por orden de primera aparición.
            foreach (var it in running) if (!runOrder.Contains(it.Key)) runOrder.Add(it.Key);
            runOrder.RemoveAll(k => !groups.ContainsKey(k));

            var result = new List<AppItem>();
            var used = new HashSet<string>();
            foreach (var p in pins.Pins)
            {
                // Windows asocia por AppUserModelID; si no coincide, por ruta del exe.
                AppItem it = null;
                if (p.Aumid.Length > 0 && groups.TryGetValue("aumid:" + p.Aumid, out it) && used.Contains(it.Key)) it = null;
                if (it == null && p.Exe.Length > 0)
                {
                    foreach (var g in running)
                    {
                        if (used.Contains(g.Key) || g.ExePath.Length == 0) continue;
                        // El mismo exe o, si el acceso directo lleva a un lanzador o a una versión anterior, la misma app:
                        // sin esto la app saldría dos veces, anclada y abierta.
                        if (g.ExePath.ToLowerInvariant() != p.Exe && Shortcut.AppIdentity(g.ExePath, null) != p.Identity) continue;
                        if (g.Aumid.Length > 0 && p.Aumid.Length > 0) continue;
                        it = g;
                        break;
                    }
                }
                if (it != null)
                {
                    it.Pinned = true;
                    it.LaunchPath = p.Path;
                    it.Name = p.Name;
                    it.Id = "pin:" + p.Path;
                    it.Image = icons.Get(p.Path);
                    used.Add(it.Key);
                    result.Add(it);
                }
                else
                {
                    result.Add(new AppItem
                    {
                        Key = "pin:" + p.Path, Id = "pin:" + p.Path, Name = p.Name, LaunchPath = p.Path, ExePath = p.Exe,
                        Pinned = true, Image = icons.Get(p.Path)
                    });
                }
            }
            foreach (string key in runOrder)
            {
                if (used.Contains(key)) continue;
                AppItem it = groups[key];
                if (string.IsNullOrEmpty(it.LaunchPath)) it.LaunchPath = it.ExePath;
                it.Id = it.Key;
                if (it.Packaged) it.Image = icons.Get(it.LaunchPath);
                if (it.Image == null && it.ExePath.Length > 0) it.Image = icons.Get(it.ExePath);
                if (it.Image == null) it.Image = icons.FromHicon(WindowIcon(it.Windows[0]));
                result.Add(it);
            }

            var sb = new StringBuilder();
            foreach (var it in result)
            {
                sb.Append(it.Key).Append('|').Append(it.Windows.Count).Append(it.Active ? 'A' : '-')
                  .Append(it.Flashing ? 'F' : '-').Append(it.Image == null ? 0 : it.Image.Image.ToInt64()).Append(';');
            }
            signature = sb.ToString();
            return result;
        }

        static IntPtr WindowIcon(IntPtr hwnd)
        {
            IntPtr res;
            foreach (int kind in new[] { 1, 2, 0 })
            {
                Native.SendMessageTimeoutW(hwnd, Native.WM_GETICON, new IntPtr(kind), IntPtr.Zero, 2 /* SMTO_ABORTIFHUNG */, 50, out res);
                if (res != IntPtr.Zero) return res;
            }
            IntPtr c = Native.GetClassLongPtr(hwnd, -14); // GCL_HICON
            if (c == IntPtr.Zero) c = Native.GetClassLongPtr(hwnd, -34); // GCL_HICONSM
            return c;
        }

        string PathOfPid(uint pid)
        {
            string p;
            if (pidPath.TryGetValue(pid, out p)) return p;
            p = "";
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (Native.QueryFullProcessImageNameW(h, 0, sb, ref size)) p = sb.ToString();
                Native.CloseHandle(h);
            }
            pidPath[pid] = p;
            return p;
        }

        bool IsTaskbarWindow(IntPtr h)
        {
            if (h == self1 || h == self2) return false;
            if (!Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && !appWindow) return false;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
            if (Native.GetWindowTextLengthW(h) == 0) return false;
            string cls = Native.GetClassName(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd"
                || cls == "NeuTaskBar.Island" || cls == "NeuTaskBar.Host") return false;
            return true;
        }
    }
}
