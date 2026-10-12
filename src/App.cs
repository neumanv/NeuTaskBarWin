using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    sealed class App
    {
        public const string HostClass = "NeuTaskBar.Host";
        public const uint WM_APP_APPBAR = Native.WM_APP + 1, WM_APP_TRAY = Native.WM_APP + 2, WM_APP_RESUME = Native.WM_APP + 3,
            WM_APP_QUIT = Native.WM_APP + 4, WM_APP_VOLUME = Native.WM_APP + 6, WM_APP_NET = Native.WM_APP + 7,
            WM_APP_CONFIG = Native.WM_APP + 8, WM_APP_MOVE = Native.WM_APP + 9;

        const uint T_REFRESH = 1, T_CLOCK = 2, T_SETUP = 3, T_NET = 4, T_TRIM = 5, T_ENTER = 6, T_FLY = 7, T_TOOLS = 8, T_HIDE = 9,
            T_FILL = 10, T_WATCH = 11;

        static readonly WndProcDelegate HostProcDelegate = StaticHostProc;
        static App instance;

        IntPtr host, host2;   // host2: solo para reservar la franja de un segundo borde
        enum FlyKind { Start = 0, Shell = 1, Overflow = 2 }
        readonly bool[] flyOpen = new bool[3];  // estado observado en el último refresco
        int shellOwner;                         // botón que abrió el panel del sistema: 1 = red/volumen, 2 = reloj
        bool toggleClose;                       // el clic en curso debe cerrar el panel en lugar de abrirlo
        LeftIsland left;
        ThumbFlyout flyout;
        TaskbarMenu menu;
        readonly StripFill[] fills = new StripFill[2];   // uno por franja reservada
        int flyTicks;
        RightIsland right;
        PlateIsland plate;                      // fondo común de las islas unidas
        PinStore pins;
        WindowTracker tracker;
        VolumeMonitor volume;
        IntPtr trayIcon;

        bool paused, started, islandsHiddenForFullscreen;
        uint wmShellHook, wmTaskbarCreated;
        WinEventProc winEventProc;
        readonly List<IntPtr> hooks = new List<IntPtr>();
        bool shellHookRegistered, netSubscribed;
        string lastSig = "";
        IntPtr desktopWindow;
        RECT lastBarRect;
        int lastBarHeight = -1;
        Edge barEdge = Edge.Bottom;         // borde elegido en Windows
        // Sitio de una isla: borde de pantalla, extremo de ese borde (inicio o final) y, solo la de aplicaciones sin
        // esquina elegida, centrada como manda la alineación de Windows.
        struct Spot { public Edge Edge; public bool End, Centered; }
        Spot spotL, spotR;
        bool joined, following;             // islas unidas; se está recolocando la que sigue a la otra
        readonly Edge[] strips = new Edge[2];   // bordes con isla (cada uno reserva su franja)
        int stripCount = 1, lastStripSig = -1;
        int layH, layMargin;                // grosor de las islas y margen del último reparto (px)
        bool moveMode;                      // las islas se arrastran (lo activa la aplicación hasta que se confirma)
        readonly IntPtr[] targets = new IntPtr[4];   // marcas de las esquinas libres mientras se arrastra una isla
        readonly RECT[] targetRects = new RECT[4];
        readonly bool[] targetFree = new bool[4];
        int targetHot = -1;
        POINT moveOrigin;                   // centro de la isla al empezar a arrastrarla
        int lastEdgeCheck, lastPosChanged;
        bool smallWhenFull;                 // "botones pequeños cuando la barra esté llena" y ahora lo está
        bool leftRevealed = true, rightRevealed = true;   // ocultación: la isla está a la vista
        bool userAutoHide;                  // lo que el usuario tiene elegido en "Ocultar automáticamente" (solo vale con la barra abajo)
        // false = las islas no reservan franja: las ventanas llegan hasta el borde y ocupan el hueco entre las dos islas.
        bool barReserved = true;
        int leftWantedTick, rightWantedTick, hideDist, barThick;
        RECT monRect;

        // ------------------------------------------------------------------ ciclo de vida

        public static int Run()
        {
            instance = new App();
            return instance.Main();
        }

        int Main()
        {
            Config.Load();
            try { Native.SetPreferredAppMode(1); Native.FlushMenuThemes(); } catch { }

            CreateHost();
            wmShellHook = Native.RegisterWindowMessageW("SHELLHOOK");
            wmTaskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
            desktopWindow = Native.GetDesktopWindow();

            pins = new PinStore();
            pins.Load();

            for (int i = 0; i < fills.Length; i++)   // antes que las islas: deben quedar por debajo de ellas
            {
                fills[i] = new StripFill();
                fills[i].Init();
            }
            plate = new PlateIsland();   // y la placa, por debajo de las dos
            plate.Init();
            left = new LeftIsland();
            left.Init();
            right = new RightIsland();
            right.Init();
            ApplyTints();
            left.Moved = right.Moved = JoinFollow;
            left.OnMoveStart = right.OnMoveStart = MoveStart;
            left.OnMoving = right.OnMoving = Moving;
            left.OnMoveEnd = right.OnMoveEnd = MoveEnd;
            tracker = new WindowTracker(pins, left.Icons, left.Hwnd, right.Hwnd);
            volume = new VolumeMonitor(host, WM_APP_VOLUME);
            right.Volume = () => volume;

            flyout = new ThumbFlyout();
            flyout.Init();
            flyout.OnActivate = h => Activate(h);
            flyout.OnClose = h => Native.PostMessageW(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            menu = new TaskbarMenu();
            menu.Init();
            left.OnHover = (it, r) => flyout.Hover(it, r);
            left.OnReorder = ReorderItems;
            left.OnAnyMouseDown = right.OnAnyMouseDown = () => menu.Close();

            left.OnStartDown = () => toggleClose = WasOpenAtPress(FlyKind.Start, true);
            left.OnStartClick = () =>
            {
                flyout.HideNow();
                if (!Toggled(FlyKind.Start)) Native.PressKeys(Native.VK_LWIN);
            };
            left.OnToolClick = id =>
            {
                flyout.HideNow();
                if (id == "search") Native.PressKeys(Native.VK_LWIN, 0x53 /* S */);
                else if (id == "taskview") Native.PressKeys(Native.VK_LWIN, 0x09 /* TAB */);
                else if (id == "widgets") Native.PressKeys(Native.VK_LWIN, 0x57 /* W */);
            };
            ApplyTools();
            left.OnStartContext = () => Native.PressKeys(Native.VK_LWIN, 0x58 /* X */);
            left.OnItemClick = it => { flyout.HideNow(); ItemClick(it); };
            left.OnItemMiddle = it => { if (!string.IsNullOrEmpty(it.LaunchPath)) Native.Launch(it.LaunchPath, null); };
            left.OnItemContext = (it, x, y) => { flyout.HideNow(); ItemContext(it, x, y); };
            left.OnBlankContext = (x, y) => ShowTaskbarMenu(x, y);
            left.OnFilesDropped = FilesDropped;

            right.OnButtonDown = kind =>
            {
                if (kind == RightIsland.Chevron) toggleClose = WasOpenAtPress(FlyKind.Overflow, true);
                else if (kind == RightIsland.Sys) toggleClose = WasOpenAtPress(FlyKind.Shell, shellOwner == 1);
                else toggleClose = WasOpenAtPress(FlyKind.Shell, shellOwner == 2);
            };
            right.OnChevronClick = () => { if (!Toggled(FlyKind.Overflow)) OpenHiddenIcons(); };
            right.OnChevronContext = (x, y) => ShowTaskbarMenu(x, y);
            right.OnSysClick = () =>
            {
                if (Toggled(FlyKind.Shell)) { shellOwner = 0; return; }
                shellOwner = 1;
                Native.PressKeys(Native.VK_LWIN, 0x41 /* A */);
            };
            right.OnClockClick = () =>
            {
                if (Toggled(FlyKind.Shell)) { shellOwner = 0; return; }
                shellOwner = 2;
                if (Island.IsWin11) Native.PressKeys(Native.VK_LWIN, 0x4E /* N */);
                else Native.PressKeys(Native.VK_LWIN, 0x12 /* ALT */, 0x44 /* D */);
            };
            right.OnKeyboardClick = () =>
            {
                string tip = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    @"microsoft shared\ink\TabTip.exe");
                Native.Launch(tip, null);
            };
            right.OnDesktopClick = () => Native.PressKeys(Native.VK_LWIN, 0x44 /* D */);
            right.OnVolumeWheel = delta => { volume.Step(delta > 0); right.Refresh(); };
            right.OnSysContext = SysContext;
            right.OnClockContext = ClockContext;

            left.DpiChanged = right.DpiChanged = () => { if (!paused) { lastBarHeight = -1; ApplyLayout(); } };

            AddTrayIcon();
            Resume();

            MSG msg;
            while (Native.GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
            return 0;
        }

        void ApplyTools()
        {
            Config.ReadWindowsTaskbarSettings();
            left.SearchMode = Config.SearchMode;
            var ids = new List<string>();
            if (Config.ShowSearch) ids.Add("search");
            if (Config.ShowTaskView) ids.Add("taskview");
            if (Config.ShowWidgets) ids.Add("widgets");
            left.SetTools(ids.ToArray());
        }

        // Algo cambió en Configuración > Personalización > Barra de tareas: se aplica al momento.
        void ApplyWindowsSettings()
        {
            ApplyTools();
            RemoveHooks();
            InstallHooks();
            right.UpdateClock();
            Native.SetTimer(host, new UIntPtr(T_CLOCK), 200, IntPtr.Zero);
            flyout.HideNow();
            lastBarHeight = -1;
            lastSig = "";
            ApplyLayout();
            UpdateAutoHide();
        }

        // Islas que se esconden: las dos con "Ocultar automáticamente la barra de tareas" de Windows, o cada una por su
        // cuenta con los interruptores de Apariencia.
        // Unidas forman una sola barra: se esconde entera con que se haya pedido esconder una de las dos.
        static bool HidesLeft { get { return Config.AutoHide || Config.HideLeft || (Config.Join && Config.HideRight); } }
        static bool HidesRight { get { return Config.AutoHide || Config.HideRight || (Config.Join && Config.HideLeft); } }

        // Ocultación: arranca o detiene la vigilancia del cursor y deja a la vista las islas que no se esconden.
        // Mientras se mueven las islas no se esconde ninguna.
        void UpdateAutoHide()
        {
            bool l = HidesLeft && !paused && !moveMode, r = HidesRight && !paused && !moveMode;
            if (l || r) Native.SetTimer(host, new UIntPtr(T_HIDE), 100, IntPtr.Zero);
            else Native.KillTimer(host, new UIntPtr(T_HIDE));
            leftWantedTick = rightWantedTick = Environment.TickCount;
            if (!l) { leftRevealed = true; left.SetAutoHidden(false, hideDist); plate.SetAutoHidden(false, hideDist); }
            if (!r) { rightRevealed = true; right.SetAutoHidden(false, hideDist); }
        }

        // Distancia del cursor al borde de pantalla en el que está una isla.
        int EdgeDist(Edge e, POINT p)
        {
            RECT rc = monRect;
            return e == Edge.Bottom ? rc.Bottom - 1 - p.Y : e == Edge.Top ? p.Y - rc.Top : e == Edge.Left ? p.X - rc.Left : rc.Right - 1 - p.X;
        }

        // Con la ocultación de Windows la barra entera aparece al llevar el cursor a su borde de pantalla (o al abrir
        // Inicio, un panel o un menú) y se esconde poco después de que el cursor la abandone, como la barra de Windows.
        // Con la de Apariencia cada isla atiende solo a su sitio y a sus propios paneles.
        void AutoHideTick()
        {
            if (paused || moveMode || !(HidesLeft || HidesRight)) { UpdateAutoHide(); return; }
            POINT p;
            Native.GetCursorPos(out p);
            RECT rc = monRect;
            bool inside = p.X >= rc.Left && p.X < rc.Right && p.Y >= rc.Top && p.Y < rc.Bottom;
            int near = left.S(2), zone = hideDist;
            // Cada isla mira a su propio borde de pantalla (pueden estar en bordes distintos).
            int dl = EdgeDist(left.Edge, p), dr = EdgeDist(right.Edge, p);
            bool start = IsFlyoutOpen(FlyKind.Start), panels = IsFlyoutOpen(FlyKind.Shell) || IsFlyoutOpen(FlyKind.Overflow);
            bool wantLeft, wantRight, holdLeft, holdRight;
            if (Config.AutoHide)
            {
                bool busy = menu.Visible || flyout.Visible || start || panels;
                wantLeft = (inside && dl <= near) || busy;
                wantRight = (inside && dr <= near) || busy;
                holdLeft = inside && dl <= zone;
                holdRight = inside && dr <= zone;
            }
            else
            {
                // La franja sigue reservada, así que en el sitio de la isla escondida no hay nada más: basta con pasar por él.
                bool overLeft = OverIsland(left, p), overRight = OverIsland(right, p);
                wantLeft = (inside && dl < barThick && overLeft) || flyout.Visible || start;
                wantRight = (inside && dr < barThick && overRight) || panels;
                holdLeft = (inside && dl <= zone && overLeft) || menu.Visible;
                holdRight = (inside && dr <= zone && overRight) || menu.Visible;
            }
            if (joined)
            {
                wantLeft = wantRight = wantLeft || wantRight;
                holdLeft = holdRight = holdLeft || holdRight;
            }
            if (HidesLeft && HideStep(left, ref leftRevealed, ref leftWantedTick, wantLeft, holdLeft)) flyout.HideNow();
            if (HidesRight) HideStep(right, ref rightRevealed, ref rightWantedTick, wantRight, holdRight);
            if (joined) plate.SetAutoHidden(!leftRevealed, hideDist);
        }

        // El cursor está en el tramo de la barra que ocupa la isla (con un pequeño margen a los lados).
        bool OverIsland(Island isl, POINT p)
        {
            RECT r = isl.RestRect;
            int pad = isl.S(6);
            return IsVert(isl.Edge) ? p.Y >= r.Top - pad && p.Y < r.Bottom + pad : p.X >= r.Left - pad && p.X < r.Right + pad;
        }

        // Muestra la isla si se la reclama (want) y la esconde poco después de que deje de reclamarse o retenerse (hold).
        // Devuelve true si la acaba de esconder.
        bool HideStep(Island isl, ref bool revealed, ref int wantedTick, bool want, bool hold)
        {
            int now = Environment.TickCount;
            if (want || (revealed && hold))
            {
                wantedTick = now;
                if (!revealed)
                {
                    revealed = true;
                    isl.SetAutoHidden(false, hideDist);
                    isl.RaiseTopmost();
                }
            }
            else if (revealed && (now - wantedTick > 450 || now < wantedTick))
            {
                revealed = false;
                isl.SetAutoHidden(true, hideDist);
                return true;
            }
            return false;
        }

        // Las islas transparentes ajustan su tinta a lo que tienen detrás, que cambia sin que ellas se enteren.
        void WatchBackdrops()
        {
            left.WatchBackdrop();
            right.WatchBackdrop();
        }

        // La aplicación ha guardado un ajuste en config.ini: se vuelve a leer y se aplica al momento.
        void ReloadConfig()
        {
            Config.Load();
            ApplyTints();
            UpdateTrayTip();   // por si ha cambiado el idioma
            if (paused) return;
            lastBarHeight = -1;
            lastSig = "";
            ApplyLayout();
            UpdateAutoHide();
        }

        // Color de cada isla. Unidas, el color lo lleva la placa (el de la isla de aplicaciones) y las dos islas van
        // transparentes encima de ella.
        void ApplyTints()
        {
            if (Config.Join)
            {
                plate.SetTint(Config.TintLeft);
                left.SetTint(0);
                right.SetTint(0);
                if (!plate.Clear) { left.AdoptInk(plate); right.AdoptInk(plate); }
            }
            else
            {
                left.SetTint(Config.TintLeft);
                right.SetTint(Config.TintRight);
            }
        }

        void ShowIslands(bool show)
        {
            left.Show(show);
            right.Show(show);
            plate.Show(show && joined);
            if (show && joined) plate.PlaceBehind(left.Hwnd);
        }

        // Por encima de las demás ventanas, con la de información sobre la de aplicaciones y la placa bajo las dos.
        void RaiseIslands()
        {
            left.RaiseTopmost();
            right.RaiseTopmost();
            if (joined) plate.PlaceBehind(left.Hwnd);
        }

        // ------------------------------------------------------------------ mover las islas

        // La aplicación abre y cierra el modo mover: mientras dura, las islas se arrastran a la esquina que se quiera.
        void SetMoveMode(bool on)
        {
            if (on && paused) on = false;
            if (on == moveMode) return;
            moveMode = on;
            left.Movable = right.Movable = on;
            flyout.HideNow();
            menu.Close();
            ShowTargets(null);
            UpdateAutoHide();
            left.Invalidate();
            right.Invalidate();
        }

        // Esquina (0 arriba-izq., 1 arriba-dcha., 2 abajo-izq., 3 abajo-dcha.) que ocupa un sitio, y al revés. Las islas
        // van tumbadas o de pie como la barra de Windows.
        static bool IsVert(Edge e) { return e == Edge.Left || e == Edge.Right; }

        static int CornerOf(Spot s)
        {
            if (IsVert(s.Edge)) return (s.Edge == Edge.Right ? 1 : 0) | (s.End ? 2 : 0);
            return (s.Edge == Edge.Bottom ? 2 : 0) | (s.End ? 1 : 0);
        }

        Spot SpotAt(int corner)
        {
            bool right = (corner & 1) != 0, bottom = (corner & 2) != 0;
            var s = new Spot();
            if (IsVert(barEdge)) { s.Edge = right ? Edge.Right : Edge.Left; s.End = bottom; }
            else { s.Edge = bottom ? Edge.Bottom : Edge.Top; s.End = right; }
            return s;
        }

        // Dónde va cada isla: en la esquina elegida o, si no se ha elegido, donde manda la barra de Windows (la de
        // aplicaciones al inicio o centrada; la de información al final). Unidas, la segunda sigue a la primera.
        void ResolveSpots()
        {
            spotL = Config.CornerLeft >= 0 ? SpotAt(Config.CornerLeft) : new Spot { Edge = barEdge, Centered = Config.Center };
            spotR = Config.CornerRight >= 0 ? SpotAt(Config.CornerRight) : new Spot { Edge = barEdge, End = true };
            joined = Config.Join;
            if (joined) { spotR = spotL; spotR.Centered = false; }
            else if (!spotL.Centered && CornerOf(spotL) == CornerOf(spotR)) spotR.End = !spotR.End;   // nunca las dos en la misma
            strips[0] = spotL.Edge;
            strips[1] = spotR.Edge;
            stripCount = spotL.Edge == spotR.Edge ? 1 : 2;
        }

        bool EdgeUsed(Edge e) { return strips[0] == e || (stripCount > 1 && strips[1] == e); }

        // Coordenada del lado de una isla pegado a su borde de pantalla, y la del lado que mira al centro.
        int Cross(Edge e)
        {
            RECT rc = monRect;
            return e == Edge.Top ? rc.Top + layMargin : e == Edge.Left ? rc.Left + layMargin : e == Edge.Right ? rc.Right - layMargin : rc.Bottom - layMargin;
        }

        int AnchorOf(Edge e) { return e == Edge.Top || e == Edge.Left ? Cross(e) + layH : Cross(e) - layH; }

        // Rectángulo que ocuparía en una esquina una isla de esa longitud.
        RECT CornerRect(int corner, int len)
        {
            Spot s = SpotAt(corner);
            RECT rc = monRect;
            bool vert = IsVert(s.Edge);
            int a0 = vert ? rc.Top : rc.Left, a1 = vert ? rc.Bottom : rc.Right;
            int along = s.End ? a1 - layMargin - len : a0 + layMargin;
            int c0 = Math.Min(Cross(s.Edge), AnchorOf(s.Edge));
            return vert ? new RECT(c0, along, c0 + layH, along + len) : new RECT(along, c0, along + len, c0 + layH);
        }

        void MoveStart(Island isl)
        {
            RECT w;
            Native.GetWindowRect(isl.Hwnd, out w);
            moveOrigin = new POINT((w.Left + w.Right) / 2, (w.Top + w.Bottom) / 2);
            ShowTargets(isl);
        }

        void Moving(Island isl, int x, int y)
        {
            if (joined)
            {
                // Unidas van juntas: la otra isla y la placa acompañan a la que se arrastra.
                (isl == left ? (Island)right : left).FloatBy(x - moveOrigin.X, y - moveOrigin.Y);
                plate.FloatBy(x - moveOrigin.X, y - moveOrigin.Y);
            }
            int hot = NearestTarget(x, y);
            if (hot == targetHot) return;
            targetHot = hot;
            for (int i = 0; i < 4; i++)
                if (targetFree[i]) Native.SetLayeredWindowAttributes(targets[i], 0, (byte)(i == hot ? 150 : 60), 2 /* LWA_ALPHA */);
        }

        // Al soltar la isla se queda en la esquina libre más cercana, y se guarda.
        void MoveEnd(Island isl, int x, int y)
        {
            int corner = NearestTarget(x, y);
            ShowTargets(null);
            left.Settle();
            right.Settle();
            plate.Settle();
            if (corner < 0) return;
            if (joined || isl == left)
            {
                Config.CornerLeft = corner;
                Config.Save("cornerleft", Config.CornerName(corner));
            }
            else
            {
                Config.CornerRight = corner;
                Config.Save("cornerright", Config.CornerName(corner));
            }
            lastBarHeight = -1;
            lastSig = "";
            ApplyLayout();
        }

        int NearestTarget(int x, int y)
        {
            int best = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                if (!targetFree[i]) continue;
                RECT r = targetRects[i];
                long dx = x - (r.Left + r.Right) / 2, dy = y - (r.Top + r.Bottom) / 2;
                if (dx * dx + dy * dy < bestD) { bestD = dx * dx + dy * dy; best = i; }
            }
            return best;
        }

        const string TargetClass = "NeuTaskBar.Target";
        static readonly WndProcDelegate TargetProc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);

        // Marcas translúcidas en las esquinas donde puede soltarse la isla que se arrastra (null = quitarlas): todas
        // menos la que ocupa la otra isla; unidas se mueven juntas y vale cualquiera.
        void ShowTargets(Island isl)
        {
            targetHot = -1;
            if (isl != null && targets[0] == IntPtr.Zero)
            {
                var wc = new WNDCLASS();
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(TargetProc);
                wc.hInstance = Native.GetModuleHandleW(null);
                wc.hbrBackground = Native.CreateSolidBrush(Native.Rgb(76, 194, 255));
                wc.lpszClassName = TargetClass;
                Native.RegisterClassW(ref wc);
                int ex = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
                for (int i = 0; i < 4; i++)
                {
                    targets[i] = Native.CreateWindowExW(ex, TargetClass, "", Native.WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                    if (!Island.IsWin11) continue;
                    int round = 2; // DWMWCP_ROUND
                    Native.DwmSetWindowAttribute(targets[i], Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                }
            }
            if (targets[0] == IntPtr.Zero) return;
            Spot other = isl == left ? spotR : spotL;
            int taken = isl == null || joined || (isl == right && spotL.Centered) ? -1 : CornerOf(other);
            int len = joined ? left.VisW + right.VisW : isl == null ? 0 : isl.VisW;
            for (int i = 0; i < 4; i++)
            {
                targetFree[i] = isl != null && i != taken;
                if (!targetFree[i]) { Native.ShowWindow(targets[i], Native.SW_HIDE); continue; }
                RECT r = targetRects[i] = CornerRect(i, len);
                Native.SetLayeredWindowAttributes(targets[i], 0, 60, 2 /* LWA_ALPHA */);
                Native.SetWindowPos(targets[i], Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            }
        }

        // Islas unidas: la de información va pegada a continuación de la de aplicaciones (o esta termina donde empieza
        // aquella, si la barra va al final del borde), y la placa abarca las dos. Se repasa cada vez que una cambia de
        // sitio o de tamaño.
        void JoinFollow()
        {
            if (!joined || following || plate == null) return;
            following = true;
            Edge e = spotL.Edge;
            bool vert = IsVert(e);
            int cross = Cross(e);
            if (spotL.End)
            {
                RECT r = right.RestRect;
                left.SetPlacement(e, cross, vert ? r.Top : r.Left);
            }
            else
            {
                RECT l = left.RestRect;
                right.SetPlacement(e, cross, vert ? l.Bottom : l.Right);
            }
            RECT first = left.RestRect;
            plate.VisW = left.VisW + right.VisW;
            plate.VisH = left.VisH;
            plate.SetPlacement(e, cross, vert ? first.Top : first.Left);
            following = false;
        }

        void Resume()
        {
            paused = false;
            started = true;
            Native.SetTimer(host, new UIntPtr(T_CLOCK), 1000, IntPtr.Zero);
            Native.SetTimer(host, new UIntPtr(T_TOOLS), 700, IntPtr.Zero);

            volume.Attach();
            right.UpdateNetwork(NetworkProbe.Detect());
            right.UpdateBattery(BatteryState.Read());
            right.UpdateClock();
            SubscribeNetwork();

            if (!SetupShell()) Native.SetTimer(host, new UIntPtr(T_SETUP), 1000, IntPtr.Zero);
            InstallHooks();
            FollowTaskbarSize(barEdge);
            ApplyLayout();
            lastSig = "";
            RefreshApps();
            ShowIslands(!islandsHiddenForFullscreen);
            UpdateTrayTip();
            UpdateAutoHide();
            Native.SetTimer(host, new UIntPtr(T_TRIM), 8000, IntPtr.Zero);
        }

        bool SetupShell()
        {
            if (!ShellTaskbar.Hide()) return false;
            barEdge = Config.ReadTaskbarEdge();
            // Lo que el usuario tiene elegido en Windows: la ocultación automática de la barra original se respeta y se sigue.
            userAutoHide = ShellTaskbar.OriginalAutoHide();
            Config.AutoHide = userAutoHide && barEdge == Edge.Bottom;
            FollowTaskbarSize(barEdge);
            lastBarHeight = -1;
            ApplyLayout();
            return true;
        }

        void Pause()
        {
            paused = true;
            Native.KillTimer(host, new UIntPtr(T_HIDE));
            Native.KillTimer(host, new UIntPtr(T_REFRESH));
            Native.KillTimer(host, new UIntPtr(T_SETUP));
            Native.KillTimer(host, new UIntPtr(T_NET));
            RemoveHooks();
            flyout.HideNow();
            menu.Close();
            SetMoveMode(false);
            ShowIslands(false);
            foreach (var f in fills) f.Hide();
            ShellTaskbar.UnregisterBar(host);
            ShellTaskbar.UnregisterBar(host2);
            ShellTaskbar.Restore();
            volume.Detach();
            UpdateTrayTip();
        }

        void Quit()
        {
            if (!paused) Pause();
            RemoveTrayIcon();
            flyout.Destroy();
            menu.Destroy();
            foreach (var f in fills) f.Destroy();
            foreach (var t in targets) if (t != IntPtr.Zero) Native.DestroyWindow(t);
            right.Destroy();
            left.Destroy();
            plate.Destroy();
            Native.DestroyWindow(host2);
            Native.DestroyWindow(host);
            Native.PostQuitMessage(0);
        }

        // ------------------------------------------------------------------ ventana anfitriona

        void CreateHost()
        {
            var wc = new WNDCLASS();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(HostProcDelegate);
            wc.hInstance = Native.GetModuleHandleW(null);
            wc.lpszClassName = HostClass;
            Native.RegisterClassW(ref wc);
            host = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, HostClass, "NeuTaskBar", Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            // Otra clase, para que quien busque la ventana del motor por su clase encuentre siempre la principal.
            wc.lpszClassName = HostClass + "2";
            Native.RegisterClassW(ref wc);
            host2 = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, wc.lpszClassName, "NeuTaskBar", Native.WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        static IntPtr StaticHostProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (instance != null && instance.host != IntPtr.Zero) return instance.HostProc(h, msg, w, l);
            }
            catch { }
            return Native.DefWindowProcW(h, msg, w, l);
        }

        IntPtr HostProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == wmShellHook && wmShellHook != 0)
            {
                if (w.ToInt64() == 0x8006 /* HSHELL_FLASH */) tracker.Flash(l);
                ArmRefresh();
                return IntPtr.Zero;
            }
            if (msg == wmTaskbarCreated && wmTaskbarCreated != 0)
            {
                OnExplorerRestarted();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case Native.WM_TIMER: OnTimer((uint)w.ToInt64()); return IntPtr.Zero;
                case WM_APP_APPBAR:
                    if (w.ToInt64() == Native.ABN_POSCHANGED && !paused)
                    {
                        // Otra barra cambió: se vuelve a fijar nuestra franja (con un mínimo entre veces, para no entrar en bucle).
                        int tick = Environment.TickCount;
                        if (tick - lastPosChanged > 300 || tick < lastPosChanged) { lastPosChanged = tick; lastBarHeight = -1; }
                        ApplyLayout();
                    }
                    return IntPtr.Zero;
                case WM_APP_TRAY: OnTrayMessage((uint)(l.ToInt64() & 0xFFFF)); return IntPtr.Zero;
                case WM_APP_RESUME: if (paused) Resume(); return IntPtr.Zero;
                case WM_APP_QUIT: Quit(); return IntPtr.Zero;
                case WM_APP_CONFIG: ReloadConfig(); return IntPtr.Zero;
                case WM_APP_MOVE: SetMoveMode(w != IntPtr.Zero); return IntPtr.Zero;
                case WM_APP_VOLUME:
                    volume.Query();
                    right.Refresh();
                    return IntPtr.Zero;
                case WM_APP_NET:
                    Native.SetTimer(host, new UIntPtr(T_NET), 700, IntPtr.Zero);
                    return IntPtr.Zero;
                case Native.WM_DISPLAYCHANGE:
                    if (!paused) { lastBarHeight = -1; ApplyLayout(); }
                    return IntPtr.Zero;
                case Native.WM_POWERBROADCAST:
                    if (!paused)
                    {
                        right.UpdateBattery(BatteryState.Read());
                        right.UpdateClock();
                    }
                    return new IntPtr(1);
                case Native.WM_TIMECHANGE:
                    if (!paused) right.UpdateClock();
                    return IntPtr.Zero;
                case Native.WM_QUERYENDSESSION: return new IntPtr(1);
                case Native.WM_ENDSESSION:
                    if (w != IntPtr.Zero) { ShellTaskbar.Restore(); RemoveTrayIcon(); }
                    return IntPtr.Zero;
            }
            return Native.DefWindowProcW(h, msg, w, l);
        }

        void OnTimer(uint id)
        {
            switch (id)
            {
                case T_REFRESH:
                    Native.KillTimer(host, new UIntPtr(T_REFRESH));
                    if (!paused) RefreshApps();
                    break;
                case T_TOOLS:
                    if (paused) { Native.KillTimer(host, new UIntPtr(T_TOOLS)); break; }
                    if (Config.ReadWindowsTaskbarSettings()) ApplyWindowsSettings();
                    CheckTaskbarEdge();
                    FitWorkArea();
                    UpdateFill();
                    WatchBackdrops();
                    // Si la aplicación se cerró sin confirmar, las islas no se quedan sueltas.
                    if (moveMode && Native.FindMainWindow() == IntPtr.Zero) SetMoveMode(false);
                    break;
                case T_FILL:
                    Native.KillTimer(host, new UIntPtr(T_FILL));
                    UpdateFill();
                    break;
                case T_WATCH:
                    foreach (var f in fills) f.Watch();
                    WatchBackdrops();
                    break;
                case T_HIDE:
                    AutoHideTick();
                    break;
                case T_CLOCK:
                    if (paused) { Native.KillTimer(host, new UIntPtr(T_CLOCK)); break; }
                    right.UpdateClock();
                    right.UpdateBattery(BatteryState.Read());
                    var now = DateTime.Now;
                    uint next = Config.ClockSeconds ? (uint)(1000 - now.Millisecond + 20)
                        : (uint)((60 - now.Second) * 1000 - now.Millisecond + 150);
                    Native.SetTimer(host, new UIntPtr(T_CLOCK), next, IntPtr.Zero);
                    break;
                case T_SETUP:
                    if (!paused && SetupShell()) Native.KillTimer(host, new UIntPtr(T_SETUP));
                    break;
                case T_NET:
                    Native.KillTimer(host, new UIntPtr(T_NET));
                    if (!paused) right.UpdateNetwork(NetworkProbe.Detect());
                    break;
                case T_ENTER:
                    Native.KillTimer(host, new UIntPtr(T_ENTER));
                    // Solo confirma si Win+B dejó el foco en la barra de Windows (evita enviar Enter a otra app).
                    IntPtr fg = Native.GetForegroundWindow();
                    if (fg != IntPtr.Zero && Native.GetClassName(Native.GetAncestor(fg, 2 /* GA_ROOT */)) == "Shell_TrayWnd")
                    {
                        Native.PressKeys(0x0D /* ENTER */);
                        flyTicks = 0;
                        Native.SetTimer(host, new UIntPtr(T_FLY), 40, IntPtr.Zero);
                    }
                    break;
                case T_FLY:
                    AdjustOverflowFlyout();
                    if (++flyTicks > 30) Native.KillTimer(host, new UIntPtr(T_FLY));
                    break;
                case T_TRIM:
                    Native.KillTimer(host, new UIntPtr(T_TRIM));
                    Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
                    break;
            }
        }

        // Abre el panel real de Windows con los iconos de bandeja ocultos (mismos iconos y menús que la barra original).
        void OpenHiddenIcons()
        {
            Native.PressKeys(Native.VK_LWIN, 0x42 /* B */);
            Native.SetTimer(host, new UIntPtr(T_ENTER), 220, IntPtr.Zero);
        }

        const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";

        // ---- Alternar paneles del sistema (clic abre; segundo clic cierra, como en la barra de Windows) ----

        static string ProcessName(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new System.Text.StringBuilder(520);
                uint size = (uint)sb.Capacity;
                if (!Native.QueryFullProcessImageNameW(h, 0, sb, ref size)) return "";
                return System.IO.Path.GetFileName(sb.ToString()).ToLowerInvariant();
            }
            finally { Native.CloseHandle(h); }
        }

        static bool IsFlyoutOpen(FlyKind kind)
        {
            if (kind == FlyKind.Overflow) return OverflowFlyoutOpen();
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string exe = ProcessName(fg);
            if (kind == FlyKind.Start) return exe == "startmenuexperiencehost.exe" || exe == "searchhost.exe";
            return exe == "shellexperiencehost.exe";
        }

        void UpdateFlyoutStates()
        {
            for (int i = 0; i < 3; i++) flyOpen[i] = IsFlyoutOpen((FlyKind)i);
            if (!flyOpen[(int)FlyKind.Shell]) shellOwner = 0;
        }

        // Se llama al pulsar el botón (antes de que el panel se cierre solo al perder el foco).
        bool WasOpenAtPress(FlyKind kind, bool sameOwner)
        {
            bool open = sameOwner && (flyOpen[(int)kind] || IsFlyoutOpen(kind));
            flyOpen[(int)kind] = false;
            return open;
        }

        // Al soltar: si el clic era de cierre, cierra el panel si sigue abierto y no lo vuelve a abrir.
        bool Toggled(FlyKind kind)
        {
            if (!toggleClose) return false;
            toggleClose = false;
            if (IsFlyoutOpen(kind))
            {
                if (kind == FlyKind.Overflow) CloseOverflowFlyout();
                else if (kind == FlyKind.Start) Native.PressKeys(Native.VK_LWIN);
                else if (shellOwner == 1) Native.PressKeys(Native.VK_LWIN, 0x41);
                else if (Island.IsWin11) Native.PressKeys(Native.VK_LWIN, 0x4E);
                else Native.PressKeys(Native.VK_LWIN, 0x12, 0x44);
            }
            return true;
        }

        // El ESC solo llega si el panel tiene el foco (tras pulsar nuestra isla no lo tiene): se le da el foco primero
        // y, si aun así sigue visible, se oculta directamente.
        static void CloseOverflowFlyout()
        {
            IntPtr o = Native.FindWindowW(OverflowClass, null);
            if (o == IntPtr.Zero) return;
            Activate(o);
            Native.PressKeys(0x1B /* ESC */);
            System.Threading.Thread.Sleep(80);
            if (Native.IsWindowVisible(o)) Native.ShowWindow(o, Native.SW_HIDE);
        }

        static bool OverflowFlyoutOpen()
        {
            IntPtr o = Native.FindWindowW(OverflowClass, null);
            return o != IntPtr.Zero && Native.IsWindowVisible(o);
        }

        // El panel de Windows se ancla a la barra original (oculta bajo la pantalla) y quedaría tapado por las islas:
        // lo subimos justo encima de ellas.
        void AdjustOverflowFlyout()
        {
            IntPtr o = Native.FindWindowW(OverflowClass, null);
            if (o == IntPtr.Zero || !Native.IsWindowVisible(o) || right == null || right.Hwnd == IntPtr.Zero) return;
            RECT r, isl;
            Native.GetWindowRect(o, out r);
            Native.GetWindowRect(right.Hwnd, out isl);
            int gap = right.S(8), nx = r.Left, ny = r.Top;
            switch (right.Edge)
            {
                case Edge.Top: if (r.Top < isl.Bottom + gap - 1) ny = isl.Bottom + gap; break;
                case Edge.Left: if (r.Left < isl.Right + gap - 1) nx = isl.Right + gap; break;
                case Edge.Right: if (r.Right > isl.Left - gap + 1) nx = isl.Left - gap - r.Width; break;
                default: if (r.Bottom > isl.Top - gap + 1) ny = isl.Top - gap - r.Height; break;
            }
            if (nx != r.Left || ny != r.Top)
                Native.SetWindowPos(o, IntPtr.Zero, nx, ny, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void ArmRefresh()
        {
            if (!paused) Native.SetTimer(host, new UIntPtr(T_REFRESH), 80, IntPtr.Zero);
        }

        void OnExplorerRestarted()
        {
            if (paused) return;
            ShellTaskbar.ForgetBar();
            RemoveTrayIcon();
            AddTrayIcon();
            if (!SetupShell()) Native.SetTimer(host, new UIntPtr(T_SETUP), 1000, IntPtr.Zero);
            RemoveHooks();
            InstallHooks();
        }

        // ------------------------------------------------------------------ hooks y red

        void InstallHooks()
        {
            if (hooks.Count > 0) return;
            if (!shellHookRegistered) shellHookRegistered = Native.RegisterShellHookWindow(host);
            winEventProc = OnWinEvent;
            const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, flags));
            hooks.Add(Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, winEventProc, 0, 0, flags));
            hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE, IntPtr.Zero, winEventProc, 0, 0, flags));
            hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_CLOAKED, Native.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, winEventProc, 0, 0, flags));
            // Maximizar o restaurar una ventana no genera ninguno de los anteriores: hace falta para el relleno de la franja.
            hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, winEventProc, 0, 0, flags));
            // Con etiquetas en los botones hay que enterarse de los cambios de título de las ventanas.
            if (Config.Combine != 0)
                hooks.Add(Native.SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, winEventProc, 0, 0, flags));
        }

        const uint EVENT_OBJECT_NAMECHANGE = 0x800C;

        void RemoveHooks()
        {
            foreach (var h in hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            hooks.Clear();
            if (shellHookRegistered) { Native.DeregisterShellHookWindow(host); shellHookRegistered = false; }
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (evt == Native.EVENT_OBJECT_LOCATIONCHANGE)
            {
                // Llegan muchos seguidos mientras se arrastra una ventana: se atiende cuando paran.
                if (!paused && Native.GetAncestor(hwnd, 1 /* GA_PARENT */) == desktopWindow)
                    Native.SetTimer(host, new UIntPtr(T_FILL), 40, IntPtr.Zero);
                return;
            }
            if ((evt >= Native.EVENT_OBJECT_CREATE && evt <= Native.EVENT_OBJECT_HIDE) || evt == EVENT_OBJECT_NAMECHANGE)
            {
                // Solo ventanas de nivel superior (las hijas generan muchísimo ruido).
                if (Native.GetAncestor(hwnd, 1 /* GA_PARENT */) != desktopWindow) return;
            }
            ArmRefresh();
        }

        void SubscribeNetwork()
        {
            if (netSubscribed) return;
            netSubscribed = true;
            try
            {
                NetworkChange.NetworkAddressChanged += (s, e) => Native.PostMessageW(host, WM_APP_NET, IntPtr.Zero, IntPtr.Zero);
                NetworkChange.NetworkAvailabilityChanged += (s, e) => Native.PostMessageW(host, WM_APP_NET, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        // ------------------------------------------------------------------ diseño y refresco

        void ApplyLayout()
        {
            if (left == null || left.Hwnd == IntPtr.Zero) return;
            IntPtr mon = Native.MonitorFromPoint(new POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!Native.GetMonitorInfoW(mon, ref mi)) return;
            RECT rc = mi.rcMonitor;

            int dpi = Native.GetMonitorDpi(mon);
            left.Dpi = right.Dpi = plate.Dpi = dpi;
            int h = left.S(Config.BarHeight);
            // Botones más pequeños (siempre, o solo cuando la barra está llena): iconos a 2/3 de su tamaño, como en Windows.
            bool small = Config.SmallButtons == 0 || (Config.SmallButtons == 2 && smallWhenFull);
            left.Small = small;
            if (left.Icons.SetPx(left.S(small ? Math.Max(16, Config.BarIconSize * 2 / 3) : Config.BarIconSize))) lastSig = "";
            int margin = left.S(Config.Margin);
            monRect = rc;
            layH = h;
            layMargin = margin;
            bool wasJoined = joined;
            ResolveSpots();
            Edge el = spotL.Edge, er = spotR.Edge;
            bool vert = IsVert(barEdge);
            flyout.Dpi = dpi;
            menu.Dpi = dpi;
            menu.Monitor = rc;
            flyout.Monitor = rc;
            // Las vistas previas salen de la isla de aplicaciones; el menú, de la isla desde la que se abra (OpenMenu).
            menu.Edge = flyout.Edge = el;
            flyout.Anchor = AnchorOf(el);

            // Cada isla va al inicio (izquierda/arriba) o al final (derecha/abajo) de su borde; la de aplicaciones
            // puede ir además centrada, sin pisar a la otra si comparten borde.
            int a0 = vert ? rc.Top : rc.Left, a1 = vert ? rc.Bottom : rc.Right;
            if (joined != wasJoined) ApplyTints();
            following = true;   // el seguimiento de las islas unidas se hace una vez, al final
            if (joined)
            {
                right.AnchorEnd = spotL.End;
                right.SetPlacement(el, Cross(el), spotL.End ? a1 - margin : a0 + margin);
                right.Layout();
                left.MaxWidth = a1 - a0 - right.VisW - margin * 2 - left.S(24);
                left.Centered = spotL.Centered;
                left.CenterPos = (a0 + a1 - right.VisW) / 2;
                left.CenterMin = a0 + margin;
                left.CenterMax = a1 - margin - right.VisW;
                left.AnchorEnd = spotL.End;
                left.SetPlacement(el, Cross(el), spotL.End ? a1 - margin - right.VisW : a0 + margin);
                left.Layout();
            }
            else
            {
                bool same = el == er;
                right.AnchorEnd = spotR.End;
                right.SetPlacement(er, Cross(er), spotR.End ? a1 - margin : a0 + margin);
                right.Layout();
                left.MaxWidth = a1 - a0 - (same ? right.VisW + margin : 0) - margin * 2 - left.S(24);
                left.Centered = spotL.Centered;
                left.CenterPos = (a0 + a1) / 2;
                left.CenterMin = a0 + margin + (same && !spotR.End ? right.VisW + left.S(12) : 0);
                left.CenterMax = a1 - margin - (same && spotR.End ? right.VisW + left.S(12) : 0);
                left.AnchorEnd = spotL.End;
                left.SetPlacement(el, Cross(el), spotL.End ? a1 - margin : a0 + margin);
                left.Layout();
            }
            following = false;
            JoinFollow();
            if (joined != wasJoined && started && !paused) ShowIslands(!islandsHiddenForFullscreen);

            hideDist = h + margin + left.S(14);
            int reserved = barThick = h + margin;
            // Se reserva la franja de las islas para que las ventanas maximizadas terminen donde empiezan y no tapen nada
            // de la app (el hueco entre las dos lo rellena StripFill con los colores de esa ventana). Solo con la
            // ocultación automática del usuario no se reserva: ahí las islas se esconden y las ventanas ocupan todo.
            // Con las islas escondidas desde Apariencia la franja sigue reservada: Windows guarda igualmente la de su
            // barra, y sin el relleno se vería el escritorio bajo las ventanas maximizadas.
            barReserved = !Config.AutoHide;
            // Una franja por borde con isla: la primera con la ventana principal y la segunda, si hay islas en dos
            // bordes, con la auxiliar.
            IntPtr[] bars = { host, host2 };
            for (int i = 0; i < bars.Length; i++)
            {
                bool want = barReserved && i < stripCount && ShellTaskbar.IsHiddenByUs();
                if (want == ShellTaskbar.BarRegistered(bars[i])) continue;
                if (want) ShellTaskbar.RegisterBar(bars[i], WM_APP_APPBAR); else ShellTaskbar.UnregisterBar(bars[i]);
                lastBarHeight = -1;
            }
            int stripSig = stripCount | ((int)strips[0] << 4) | ((int)strips[1] << 8);
            if (barReserved && (reserved != lastBarHeight || stripSig != lastStripSig || rc.Left != lastBarRect.Left || rc.Top != lastBarRect.Top
                || rc.Bottom != lastBarRect.Bottom || rc.Right != lastBarRect.Right))
            {
                lastBarHeight = reserved;
                lastStripSig = stripSig;
                lastBarRect = rc;
                for (int i = 0; i < stripCount; i++) ShellTaskbar.SetBarPos(bars[i], rc, reserved, strips[i]);
            }
            UpdateFill();
            // Si cambió el tamaño de icono se vació la caché: reconstruye las imágenes antes de volver a pintar.
            if (lastSig.Length == 0 && started && !paused) RefreshApps();
        }

        // Relleno del hueco entre las islas: prolonga la ventana maximizada que quede a la vista junto a la franja.
        void UpdateFill()
        {
            if (fills[0] == null || left == null) return;
            bool on = !paused && started && barReserved && lastBarHeight >= 0 && !islandsHiddenForFullscreen;
            IntPtr src = on ? StripFill.TopMaximized(Native.MonitorFromPoint(new POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */)) : IntPtr.Zero;
            bool any = false;
            for (int i = 0; i < fills.Length; i++)
            {
                RECT strip = lastBarRect;
                bool used = on && i < stripCount;
                if (used)
                    switch (strips[i])
                    {
                        case Edge.Top: strip.Bottom = strip.Top + lastBarHeight; break;
                        case Edge.Left: strip.Right = strip.Left + lastBarHeight; break;
                        case Edge.Right: strip.Left = strip.Right - lastBarHeight; break;
                        default: strip.Top = strip.Bottom - lastBarHeight; break;
                    }
                fills[i].Update(strip, strips[i], used ? src : IntPtr.Zero, left.S(6));
                any |= fills[i].Visible;
            }
            if (any) Native.SetTimer(host, new UIntPtr(T_WATCH), 300, IntPtr.Zero);
            else Native.KillTimer(host, new UIntPtr(T_WATCH));
        }

        // Windows reserva la franja de su propia barra (invisible, pero ahí sigue), que puede ser más gruesa que las islas
        // (barra pequeña en un lateral, botones con etiqueta): el área de trabajo se ajusta para que no quede un hueco
        // entre las islas y las ventanas. Windows la recalcula por su cuenta de vez en cuando, así que se repasa con el
        // temporizador (nunca al recibir sus avisos: sería un bucle).
        void FitWorkArea()
        {
            if (paused || !barReserved || lastBarHeight < 0 || !ShellTaskbar.IsHiddenByUs()) return;
            // Si ya no queda ninguna isla en el borde de la barra de Windows, su franja se libera entera.
            ShellTaskbar.FitWorkArea(lastBarRect, EdgeUsed(barEdge) ? lastBarHeight : 0, barEdge);
        }

        // "Ocultar automáticamente la barra de tareas": Configuración cambia el estado real de la barra original, y eso
        // es lo que se sigue (fuera del borde inferior Windows no la admite y la desactiva él mismo).
        // Devuelve true si hay que recolocar las islas.
        bool FollowAutoHide(Edge edge)
        {
            if (!ShellTaskbar.IsHiddenByUs()) return false;
            bool? live = ShellTaskbar.LiveAutoHide();
            if (live == null) return false;
            bool relayout = false;
            if (ShellTaskbar.IsTrayVisible()) { ShellTaskbar.HideWindow(); relayout = true; }
            if (live.Value != userAutoHide)
            {
                // El usuario la ha cambiado en Configuración: es también el estado que se dejará al salir.
                userAutoHide = live.Value;
                ShellTaskbar.SetOriginalAutoHide(userAutoHide);
            }
            bool effective = edge == Edge.Bottom && userAutoHide;
            if (effective != Config.AutoHide) { Config.AutoHide = effective; relayout = true; }
            return relayout;
        }

        // "Tamaño de la barra de tareas": las islas toman el grosor que Windows da a su barra (y sus iconos, la misma
        // proporción). En vertical no se mide: ahí Windows deja su barra igual de ancha con el tamaño pequeño, así que
        // vale el grosor que corresponde al tamaño elegido (y FitWorkArea quita la franja que sobra).
        // Devuelve true si hay que recolocar las islas.
        bool FollowTaskbarSize(Edge edge)
        {
            bool vert = edge == Edge.Left || edge == Edge.Right;
            return Config.SetWinBarHeight(vert ? 0 : ShellTaskbar.Thickness());
        }

        // Si el usuario mueve la barra de Windows a otro borde, le cambia el tamaño o cambia su ocultación automática,
        // las islas lo siguen.
        void CheckTaskbarEdge()
        {
            int now = Environment.TickCount;
            if (now - lastEdgeCheck < 500 && now >= lastEdgeCheck) return;
            lastEdgeCheck = now;
            Edge e = Config.ReadTaskbarEdge();
            bool edgeChanged = e != barEdge;
            bool hideChanged = FollowAutoHide(e);
            bool sizeChanged = FollowTaskbarSize(e);
            if (!edgeChanged && !hideChanged && !sizeChanged) return;
            barEdge = e;
            if (edgeChanged || sizeChanged) { flyout.HideNow(); menu.Close(); }
            if (edgeChanged) lastSig = "";
            lastBarHeight = -1;
            ApplyLayout();
            UpdateAutoHide();
        }

        void RefreshApps()
        {
            if (paused || !started) return;

            UpdateFlyoutStates();
            if (ShellTaskbar.IsHiddenByUs() && ShellTaskbar.IsTrayVisible()) ShellTaskbar.HideWindow();
            CheckTaskbarEdge();

            bool full = IsForegroundFullscreen();
            if (full != islandsHiddenForFullscreen)
            {
                islandsHiddenForFullscreen = full;
                if (full) flyout.HideNow();
                ShowIslands(!full);
            }
            else if (!full && !OverflowFlyoutOpen() && !menu.Visible)
            {
                RaiseIslands();
            }

            UpdateFill();

            string sig;
            var list = tracker.Snapshot(out sig);
            list = Uncombine(list, ref sig);
            flyout.UpdateItems(list);
            if (sig == lastSig) return;
            lastSig = sig;
            left.SetItems(list);

            // "Botones más pequeños: cuando la barra esté llena".
            bool barFull = Config.SmallButtons == 2 && left.FullAtNormalSize;
            if (barFull != smallWhenFull)
            {
                smallWhenFull = barFull;
                lastSig = "";
                ApplyLayout();
            }
        }

        // "Combinar botones y ocultar etiquetas": nunca (2) o solo con la barra llena (1). Cada ventana pasa a tener
        // su propio botón con el título como etiqueta; las apps fijadas sin abrir siguen siendo un icono.
        List<AppItem> Uncombine(List<AppItem> list, ref string sig)
        {
            if (Config.Combine == 0) return list;
            IntPtr fg = Native.GetForegroundWindow();
            IntPtr fgRoot = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOTOWNER);
            var result = new List<AppItem>();
            var sb = new System.Text.StringBuilder(sig);
            int labeled = 0, plain = 0;
            foreach (var it in list)
            {
                if (it.Windows.Count == 0) { result.Add(it); plain++; continue; }
                var wins = new List<IntPtr>(it.Windows);
                wins.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));   // orden estable (el de la lista cambia con el foco)
                for (int i = 0; i < wins.Count; i++)
                {
                    IntPtr w = wins[i];
                    string title = Native.GetWindowText(w);
                    if (string.IsNullOrEmpty(title)) title = it.Name ?? "";
                    var c = new AppItem
                    {
                        Key = it.Key, Name = it.Name, LaunchPath = it.LaunchPath, ExePath = it.ExePath, Pinned = it.Pinned,
                        Id = i == 0 ? it.Id : it.Id + "#" + w.ToInt64(), Image = it.Image, Title = title, Label = title,
                        Aumid = it.Aumid, Packaged = it.Packaged
                    };
                    c.Windows.Add(w);
                    c.Active = it.Active && (wins.Count == 1 || w == fg || w == fgRoot);
                    c.Flashing = it.Flashing && !c.Active;
                    result.Add(c);
                    labeled++;
                    sb.Append('#').Append(w.ToInt64()).Append(c.Active ? 'A' : '-').Append(title).Append(';');
                }
            }
            if (Config.Combine == 1 && !left.FitsUncombined(labeled, plain)) { sig += "|combined"; return list; }
            sig = sb.ToString();
            return result;
        }

        bool IsForegroundFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == host || fg == left.Hwnd || fg == right.Hwnd) return false;
            if (!Native.IsWindowVisible(fg) || Native.IsIconic(fg)) return false;
            // Sin franja reservada una ventana maximizada ocupa toda la pantalla sin ser de pantalla completa.
            if (!barReserved && Native.IsZoomed(fg)) return false;
            string cls = Native.GetClassName(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            IntPtr mon = Native.MonitorFromWindow(fg, 2 /* NEAREST */);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!Native.GetMonitorInfoW(mon, ref mi) || (mi.dwFlags & 1) == 0) return false; // solo el monitor principal
            RECT r;
            Native.GetWindowRect(fg, out r);
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        // ------------------------------------------------------------------ acciones sobre apps

        void ItemClick(AppItem it)
        {
            if (!it.Running)
            {
                if (!string.IsNullOrEmpty(it.LaunchPath)) Native.Launch(it.LaunchPath, null);
                return;
            }
            IntPtr fg = Native.GetForegroundWindow();
            IntPtr fgRoot = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOTOWNER);
            var wins = it.Windows;
            if (wins.Count == 1)
            {
                IntPtr w = wins[0];
                if (w == fg || w == fgRoot) Native.ShowWindow(w, Native.SW_MINIMIZE);
                else Activate(w);
                return;
            }
            // Varias ventanas: alterna en un orden estable; si ninguna está al frente, trae la más reciente.
            var stable = new List<IntPtr>(wins);
            stable.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));
            int cur = stable.FindIndex(w => w == fg || w == fgRoot);
            Activate(cur < 0 ? wins[0] : stable[(cur + 1) % stable.Count]);
        }

        static void Activate(IntPtr hwnd)
        {
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            Native.SetForegroundWindow(hwnd);
            if (Native.GetForegroundWindow() == hwnd) return;
            uint pid;
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out pid);
            uint me = Native.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me) Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            if (fgThread != 0 && fgThread != me) Native.AttachThreadInput(me, fgThread, false);
        }

        // Orden elegido arrastrando: las fijadas se guardan; las abiertas no fijadas se recuerdan mientras sigan abiertas.
        void ReorderItems(List<AppItem> ordered)
        {
            var pinnedPaths = new List<string>();
            var runKeys = new List<string>();
            foreach (var it in ordered)
            {
                if (it.Pinned) { if (!pinnedPaths.Contains(it.LaunchPath)) pinnedPaths.Add(it.LaunchPath); }
                else if (!runKeys.Contains(it.Key)) runKeys.Add(it.Key);
            }
            pins.ReorderByPaths(pinnedPaths);
            tracker.SetRunOrder(runKeys);
            lastSig = "";
            RefreshApps();
        }

        void FilesDropped(string[] files)
        {
            bool changed = false;
            foreach (string f in files)
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".lnk" && ext != ".exe") continue;
                string target, name, aumid;
                Shortcut.Resolve(f, out target, out name, out aumid);
                if (pins.Contains((target ?? "").ToLowerInvariant(), (aumid ?? "").ToLowerInvariant())) continue;
                pins.Add(f, null);
                changed = true;
            }
            if (changed) { lastSig = ""; RefreshApps(); }
        }

        // ------------------------------------------------------------------ menús
        // Menú de la barra con el aspecto y las medidas del de Windows 11 (TaskbarMenu); el del icono de bandeja es nativo.

        void OpenMenu(List<MenuDef> defs, int x, int y)
        {
            flyout.HideNow();
            // El menú sale hacia el centro de la pantalla desde la isla en la que se ha pedido.
            Edge e = right.RestRect.Contains(x, y) ? right.Edge : left.Edge;
            menu.Edge = e;
            menu.Show(defs, x, y, AnchorOf(e));
        }

        static void Open(string target, string args, string dir)
        {
            Native.ShellExecuteW(IntPtr.Zero, "open", target, string.IsNullOrEmpty(args) ? null : args,
                string.IsNullOrEmpty(dir) ? null : dir, Native.SW_SHOWNORMAL);
        }

        // Zona vacía de la barra: el menú de la barra de tareas de Windows.
        void ShowTaskbarMenu(int x, int y)
        {
            var l = new List<MenuDef>();
            l.Add(MenuDef.Item(L.T("Administrador de tareas"), () => Native.Launch("taskmgr.exe", null),
                left.Icons.Get(Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\Taskmgr.exe")));
            l.Add(MenuDef.Item(L.T("Configuración de la barra de tareas"), () => Native.Launch("ms-settings:taskbar", null), "\uE713"));
            OpenMenu(l, x, y);
        }

        // Botón de una app: lista de salto de Windows (tareas y categorías), nombre de la app, anclar/desanclar y cerrar.
        void ItemContext(AppItem it, int x, int y)
        {
            var l = new List<MenuDef>();
            int iconPx = left.S(16) * 2;
            foreach (var cat in JumpLists.For(it.ExePath))
            {
                if (cat.Entries.Count == 0) continue;
                l.Add(MenuDef.Header(cat.Tasks ? L.T("Tareas") : cat.Name));
                int shown = 0;
                foreach (var e in cat.Entries)
                {
                    if (shown++ >= 10) break;
                    var entry = e;
                    IconImage own = TaskbarMenu.LoadIcon(entry.IconPath, entry.IconIndex, iconPx);
                    var d = own != null ? MenuDef.Item(entry.Title, () => Open(entry.Path, entry.Args, entry.WorkDir), own)
                                        : MenuDef.Item(entry.Title, () => Open(entry.Path, entry.Args, entry.WorkDir), it.Image);
                    d.OwnsImage = own != null;
                    l.Add(d);
                }
            }
            if (l.Count > 0) l.Add(MenuDef.Separator());

            string name = string.IsNullOrEmpty(it.Name) ? L.T("Aplicación") : it.Name;
            if (!string.IsNullOrEmpty(it.LaunchPath)) l.Add(MenuDef.Item(name, () => Native.Launch(it.LaunchPath, null), it.Image));
            if (it.Pinned)
                l.Add(MenuDef.Item(L.T("Desanclar de la barra de tareas"), () => { pins.RemoveByPath(it.LaunchPath); lastSig = ""; RefreshApps(); }, "\uE77A"));
            else if (it.Aumid.Length > 0 || !string.IsNullOrEmpty(it.ExePath))
                l.Add(MenuDef.Item(L.T("Anclar a la barra de tareas"), () =>
                {
                    if (it.Packaged) pins.Add(it.LaunchPath, it.Name);
                    else pins.Add(it.ExePath, null);
                    lastSig = ""; RefreshApps();
                }, "\uE718"));
            if (it.Running && Config.EndTask)
                l.Add(MenuDef.Item(L.T("Finalizar tarea"), () =>
                {
                    var done = new HashSet<uint>();
                    foreach (var w in it.Windows.ToArray())
                    {
                        uint pid;
                        Native.GetWindowThreadProcessId(w, out pid);
                        if (pid == 0 || !done.Add(pid)) continue;
                        try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); } catch { }
                    }
                }, "\uE8BB"));
            if (it.Running)
                l.Add(MenuDef.Item(it.Windows.Count > 1 ? L.T("Cerrar todas las ventanas") : L.T("Cerrar ventana"), () =>
                {
                    foreach (var w in it.Windows.ToArray()) Native.PostMessageW(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }, "\uE711"));
            OpenMenu(l, x, y);
        }

        void ClockContext(int x, int y)
        {
            var l = new List<MenuDef>();
            l.Add(MenuDef.Item(L.T("Ajustar fecha y hora"), () => Native.Launch("ms-settings:dateandtime", null), (string)null));
            OpenMenu(l, x, y);
        }

        // Iconos de sistema: menú del icono bajo el cursor (red, volumen o batería).
        void SysContext(int x, int y)
        {
            var l = new List<MenuDef>();
            int slot = right.LastSysSlot;
            if (slot == 0) l.Add(MenuDef.Item(L.T("Configuración de red e Internet"), () => Native.Launch("ms-settings:network", null), (string)null));
            else if (slot == 1)
            {
                l.Add(MenuDef.Item(L.T("Abrir el mezclador de volumen"), () => Native.Launch("sndvol.exe", null), (string)null));
                l.Add(MenuDef.Item(L.T("Configuración de sonido"), () => Native.Launch("ms-settings:sound", null), (string)null));
            }
            else l.Add(MenuDef.Item(L.T("Configuración de energía y batería"), () => Native.Launch("ms-settings:powersleep", null), (string)null));
            OpenMenu(l, x, y);
        }

        // ---- Menú del icono de NeuTaskBar en la bandeja de Windows (nativo) ----

        sealed class MenuEntry
        {
            public int Id; public string Text; public uint Flags;
            public MenuEntry(int id, string text) { Id = id; Text = text; }
            public MenuEntry(int id, string text, uint flags) { Id = id; Text = text; Flags = flags; }
            public static MenuEntry Separator() { return new MenuEntry(0, null); }
        }

        int Track(List<MenuEntry> entries, int x, int y)
        {
            IntPtr m = Native.CreatePopupMenu();
            foreach (var e in entries)
            {
                if (e.Text == null) Native.AppendMenuW(m, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                else Native.AppendMenuW(m, e.Flags, new UIntPtr((uint)e.Id), e.Text);
            }
            Native.SetForegroundWindow(host);
            int cmd = Native.TrackPopupMenuEx(m, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, x, y, host, IntPtr.Zero);
            Native.PostMessageW(host, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(m);
            return cmd;
        }

        void ShowTrayMenu(int x, int y)
        {
            var l = new List<MenuEntry>();
            l.Add(new MenuEntry(14, L.T("Abrir NeuTaskBar")));
            l.Add(new MenuEntry(10, paused ? L.T("Reanudar") : L.T("Pausar (mostrar barra de Windows)")));
            l.Add(new MenuEntry(11, L.T("Iniciar con Windows"), Config.GetAutoStart() ? Native.MF_CHECKED : 0u));
            l.Add(MenuEntry.Separator());
            l.Add(new MenuEntry(13, L.T("Salir")));
            switch (Track(l, x, y))
            {
                case 10: if (paused) Resume(); else Pause(); break;
                case 11: Config.SetAutoStart(!Config.GetAutoStart()); break;
                case 13: Quit(); break;
                case 14: Native.Launch(Config.ExePath, null); break;
            }
        }

        // ------------------------------------------------------------------ icono de bandeja (solo visible con la barra original)

        void AddTrayIcon()
        {
            if (trayIcon == IntPtr.Zero) trayIcon = Native.ExtractIconW(Native.GetModuleHandleW(null), Config.ExePath, 0);
            if (trayIcon == IntPtr.Zero) trayIcon = Native.LoadIconW(IntPtr.Zero, new IntPtr(32512));
            var d = TrayData();
            Native.Shell_NotifyIconW(Native.NIM_ADD, ref d);
        }

        NOTIFYICONDATA TrayData()
        {
            var d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = host;
            d.uID = 1;
            d.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
            d.uCallbackMessage = WM_APP_TRAY;
            d.hIcon = trayIcon;
            d.szTip = paused ? L.T("NeuTaskBar (en pausa)") : "NeuTaskBar";
            return d;
        }

        void UpdateTrayTip()
        {
            var d = TrayData();
            Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref d);
        }

        void RemoveTrayIcon()
        {
            var d = TrayData();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref d);
        }

        void OnTrayMessage(uint mouseMsg)
        {
            if (mouseMsg == Native.WM_LBUTTONUP && paused) { Resume(); return; }
            if (mouseMsg == Native.WM_RBUTTONUP || mouseMsg == Native.WM_LBUTTONUP || mouseMsg == Native.WM_CONTEXTMENU)
            {
                POINT p;
                Native.GetCursorPos(out p);
                ShowTrayMenu(p.X, p.Y);
            }
        }
    }
}
