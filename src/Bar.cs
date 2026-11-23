using System;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    // Sitio de una isla: borde de pantalla, extremo de ese borde (inicio o final) y, solo la de aplicaciones,
    // centrada: porque lo manda la alineación de Windows o porque se ha llevado al centro de arriba o de abajo.
    struct Spot
    {
        public Edge Edge;
        public bool End, Centered;
    }

    // Una barra completa sobre un monitor: sus dos islas, la placa, los rellenos de franja y las ventanas con las que
    // reserva el área de trabajo. El motor (App) es uno solo y reparte el trabajo entre todas las barras.
    sealed class Bar
    {
        const string HostClass = "NeuTaskBar.Bar";
        static readonly WndProcDelegate Proc = StaticProc;
        static bool registered, registered2;

        public IntPtr Host, Host2;
        public LeftIsland Left;
        public RightIsland Right;
        public PlateIsland Plate;
        public readonly StripFill[] Fills = new StripFill[2];

        public IntPtr Monitor;
        public RECT MonRect;
        public int Dpi = 96;
        public bool Primary;

        public Spot SpotL, SpotR;
        public bool Joined, Full, Following;
        public bool OnePiece { get { return Joined || Full; } }
        public readonly Edge[] Strips = new Edge[2];
        public int StripCount = 1;
        public bool BarReserved = true;

        public int LayH, LayMargin, HideDist, BarThick;
        public RECT LastBarRect;
        public int LastBarHeight = -1, LastStripSig = -1;

        public bool LeftRevealed = true, RightRevealed = true;
        public int LeftWantedTick, RightWantedTick;
        public bool IslandsHiddenForFullscreen;
        public bool SmallWhenFull;

        // Marcas de los sitios libres mientras se arrastra una isla (solo se usan en la barra principal).
        public readonly IntPtr[] Targets = new IntPtr[Config.SpotCount];
        public readonly RECT[] TargetRects = new RECT[Config.SpotCount];
        public readonly bool[] TargetFree = new bool[Config.SpotCount];
        public int TargetHot = -1;
        public POINT MoveOrigin;

        public bool Contains(int x, int y) { return MonRect.Contains(x, y); }

        public void CreateHosts()
        {
            if (Host != IntPtr.Zero) return;
            var wc = new WNDCLASS();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc);
            wc.hInstance = Native.GetModuleHandleW(null);
            wc.lpszClassName = HostClass;
            if (!registered) { Native.RegisterClassW(ref wc); registered = true; }
            Host = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, HostClass, "NeuTaskBar", Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            // Otra clase, para que quien busque la ventana del motor por su clase encuentre siempre la principal.
            wc.lpszClassName = HostClass + "2";
            if (!registered2) { Native.RegisterClassW(ref wc); registered2 = true; }
            Host2 = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, wc.lpszClassName, "NeuTaskBar", Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        public void DestroyHosts()
        {
            if (Host2 != IntPtr.Zero) { Native.DestroyWindow(Host2); Host2 = IntPtr.Zero; }
            if (Host != IntPtr.Zero) { Native.DestroyWindow(Host); Host = IntPtr.Zero; }
        }

        static IntPtr StaticProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == App.WM_APP_APPBAR)
            {
                App.NotifyAppBar(w);
                return IntPtr.Zero;
            }
            return Native.DefWindowProcW(h, msg, w, l);
        }
    }
}
