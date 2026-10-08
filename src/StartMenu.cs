using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    static class StartMenu
    {
        // Crea (o actualiza) el acceso directo del menú Inicio apuntando a este exe. Windows lo indexa y aparece al buscar.
        public static void Create()
        {
            try
            {
                string lnk = Config.StartMenuShortcut;
                string exe = Config.ExePath;
                if (File.Exists(lnk))
                {
                    string target, name;
                    Shortcut.Resolve(lnk, out target, out name);
                    if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                var link = (IShellLinkW)new ShellLinkCom();
                link.SetPath(exe);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                link.SetDescription("Barra de tareas de dos islas");
                link.SetIconLocation(exe, 0);
                ((IPersistFile)link).Save(lnk, true);
                Marshal.ReleaseComObject(link);
            }
            catch (Exception ex) { Diag.Log(ex); }
        }
    }
}
