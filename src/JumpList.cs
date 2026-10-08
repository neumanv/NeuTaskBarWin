using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NeuTaskBar
{
    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistStream
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load(IStream stream);
        void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out long size);
    }

    sealed class JumpEntry
    {
        public string Title = "", Path = "", Args = "", WorkDir = "", IconPath = "";
        public int IconIndex;
    }

    sealed class JumpCategory
    {
        public string Name;     // null en la categoría "Tareas"
        public bool Tasks;
        public List<JumpEntry> Entries = new List<JumpEntry>();
    }

    // Lectura de las listas de salto personalizadas (CustomDestinations) que Windows muestra en el menú de cada botón.
    static class JumpLists
    {
        [DllImport("shlwapi.dll")] static extern IntPtr SHCreateMemStream(byte[] init, uint cb);

        sealed class Parsed
        {
            public DateTime Stamp;
            public List<JumpCategory> Categories = new List<JumpCategory>();
            public HashSet<string> Exes = new HashSet<string>();
        }

        static readonly byte[] LnkSig = { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 };
        static readonly byte[] Footer = { 0xAB, 0xFB, 0xBF, 0xBA };
        static readonly Dictionary<string, Parsed> cache = new Dictionary<string, Parsed>(StringComparer.OrdinalIgnoreCase);

        static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations"); }
        }

        // Categorías de la lista de salto de la app cuyo exe se indica (vacío si no tiene).
        public static List<JumpCategory> For(string exePath)
        {
            var result = new List<JumpCategory>();
            if (string.IsNullOrEmpty(exePath)) return result;
            string exe = exePath.ToLowerInvariant();
            try
            {
                if (!Directory.Exists(Folder)) return result;
                Parsed best = null;
                foreach (string file in Directory.GetFiles(Folder, "*.customDestinations-ms"))
                {
                    var fi = new FileInfo(file);
                    if (fi.Length < 64) continue;
                    Parsed p;
                    if (!cache.TryGetValue(file, out p) || p.Stamp != fi.LastWriteTimeUtc)
                    {
                        p = ParseFile(file);
                        p.Stamp = fi.LastWriteTimeUtc;
                        cache[file] = p;
                    }
                    if (!p.Exes.Contains(exe) || p.Categories.Count == 0) continue;
                    if (best == null || p.Stamp > best.Stamp) best = p;
                }
                if (best != null) result = best.Categories;
            }
            catch { }
            return result;
        }

        static int IndexOf(byte[] data, byte[] pat, int from, int to)
        {
            for (int i = from; i + pat.Length <= to; i++)
            {
                if (data[i] != pat[0]) continue;
                bool ok = true;
                for (int j = 1; j < pat.Length; j++) if (data[i + j] != pat[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        static Parsed ParseFile(string file)
        {
            var parsed = new Parsed();
            try
            {
                byte[] d = File.ReadAllBytes(file);
                if (d.Length < 16) return parsed;
                int total = BitConverter.ToInt32(d, 4);
                int pos = 12;
                for (int c = 0; c < total && pos + 8 <= d.Length; c++)
                {
                    int type = BitConverter.ToInt32(d, pos); pos += 4;
                    var cat = new JumpCategory();
                    if (type == 0)
                    {
                        int len = BitConverter.ToUInt16(d, pos); pos += 2;
                        cat.Name = Encoding.Unicode.GetString(d, pos, len * 2);
                        pos += len * 2;
                    }
                    else if (type == 2) cat.Tasks = true;
                    int count = BitConverter.ToInt32(d, pos); pos += 4;
                    int end = IndexOf(d, Footer, pos, d.Length);
                    if (end < 0) end = d.Length;

                    // Los accesos directos van uno tras otro; se separan por su firma.
                    var starts = new List<int>();
                    int s = IndexOf(d, LnkSig, pos, end);
                    while (s >= 0) { starts.Add(s); s = IndexOf(d, LnkSig, s + 4, end); }
                    for (int i = 0; i < starts.Count; i++)
                    {
                        int to = i + 1 < starts.Count ? starts[i + 1] : end;
                        byte[] blob = new byte[to - starts[i]];
                        Array.Copy(d, starts[i], blob, 0, blob.Length);
                        var entry = ReadLink(blob);
                        if (entry != null)
                        {
                            cat.Entries.Add(entry);
                            if (entry.Path.Length > 0) parsed.Exes.Add(entry.Path.ToLowerInvariant());
                        }
                    }
                    parsed.Categories.Add(cat);
                    pos = end + 4;
                }
            }
            catch { }
            return parsed;
        }

        static JumpEntry ReadLink(byte[] blob)
        {
            object link = null;
            try
            {
                link = new ShellLinkCom();
                IntPtr mem = SHCreateMemStream(blob, (uint)blob.Length);
                if (mem == IntPtr.Zero) return null;
                var stream = (IStream)Marshal.GetObjectForIUnknown(mem);
                Marshal.Release(mem);
                ((IPersistStream)link).Load(stream);
                var sl = (IShellLinkW)link;
                var e = new JumpEntry();
                var sb = new StringBuilder(1040);
                sl.GetPath(sb, sb.Capacity, IntPtr.Zero, 0); e.Path = sb.ToString();
                sb.Length = 0; sl.GetArguments(sb, sb.Capacity); e.Args = sb.ToString();
                sb.Length = 0; sl.GetWorkingDirectory(sb, sb.Capacity); e.WorkDir = sb.ToString();
                sb.Length = 0;
                int idx;
                sl.GetIconLocation(sb, sb.Capacity, out idx);
                e.IconPath = Environment.ExpandEnvironmentVariables(sb.ToString());
                e.IconIndex = idx;
                string title = AppUserModel.ReadString(link, new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
                if (string.IsNullOrEmpty(title)) { sb.Length = 0; sl.GetDescription(sb, sb.Capacity); title = sb.ToString(); }
                if (string.IsNullOrEmpty(title)) title = Path.GetFileNameWithoutExtension(e.Path);
                e.Title = title;
                return e;
            }
            catch { return null; }
            finally { if (link != null) Marshal.ReleaseComObject(link); }
        }
    }
}
