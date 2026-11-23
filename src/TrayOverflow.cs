using System;
using System.Windows.Automation;

namespace NeuTaskBar
{
    // Abre el panel de iconos ocultos de Windows 11 por UI Automation, pulsando su flecha (la misma que la bandeja
    // real). Enviar Win+B y Enter abría el primer icono de la bandeja, que no siempre es la flecha; los mensajes de
    // ratón no valen porque la bandeja es una isla XAML.
    static class TrayOverflow
    {
        public const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";

        // La flecha es el primer botón SystemTray.NormalButton de la bandeja (a la izquierda de los iconos).
        public static bool Open(IntPtr tray)
        {
            if (tray == IntPtr.Zero || !Island.IsWin11) return false;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(tray);
                if (root == null) return false;
                AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                AutomationElement best = null;
                double bestX = double.MaxValue;
                foreach (AutomationElement e in all)
                {
                    AutomationElement.AutomationElementInformation c;
                    try { c = e.Current; }
                    catch { continue; }
                    if (c.AutomationId != "SystemTrayIcon" || c.ClassName != "SystemTray.NormalButton") continue;
                    double x = c.BoundingRectangle.X;
                    if (x < bestX) { bestX = x; best = e; }
                }
                if (best == null) return false;
                object pattern;
                if (!best.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
                ((InvokePattern)pattern).Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Diag.Log(ex);
                return false;
            }
        }

        public static IntPtr Find()
        {
            return Native.FindWindowByClass(OverflowClass);
        }

        public static bool IsOpen()
        {
            IntPtr o = Find();
            return o != IntPtr.Zero && Native.IsWindowVisible(o);
        }

        // El ESC solo llega si el panel tiene el foco: se le da primero y, si aun así sigue visible, se oculta.
        public static void Close()
        {
            IntPtr o = Find();
            if (o == IntPtr.Zero) return;
            Native.SetForegroundWindow(o);
            Native.PressKeys(0x1B /* ESC */);
            System.Threading.Thread.Sleep(80);
            if (Native.IsWindowVisible(o)) Native.ShowWindow(o, Native.SW_HIDE);
        }
    }
}
