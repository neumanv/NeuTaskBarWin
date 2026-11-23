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
            T_FILL = 10, T_WATCH = 11, T_STATS = 12;

        static readonly WndProcDelegate HostProcDelegate = StaticHostProc;
        static App instance;

        IntPtr host;   // ventana que recibe los mensajes del motor (una sola para todas las pantallas)
        IntPtr host2 { get { return primary != null ? primary.Host2 : IntPtr.Zero; } }
        enum FlyKind { Start = 0, Shell = 1, Overflow = 2 }
        readonly bool[] flyOpen = new bool[3];  // estado observado en el último refresco
        int shellOwner;                         // botón que abrió el panel del sistema: 1 = red/volumen, 2 = reloj
        bool toggleClose;                       // el clic en curso debe cerrar el panel en lugar de abrirlo
        ThumbFlyout flyout;
        TaskbarMenu menu;
        readonly List<Bar> bars = new List<Bar>();   // una barra por monitor
        Bar primary, overflowBar;               // la del monitor principal y la que abrió los iconos ocultos
        int flyTicks;
        PinStore pins;
        WindowTracker tracker;
        VolumeMonitor volume;
        readonly SysStats stats = new SysStats();
        IntPtr trayIcon;

        bool paused, started;
        uint wmShellHook, wmTaskbarCreated;
        WinEventProc winEventProc;
        readonly List<IntPtr> hooks = new List<IntPtr>();
        bool shellHookRegistered, netSubscribed;
        string lastSig = "";
        IntPtr desktopWindow;
        Edge barEdge = Edge.Bottom;         // borde elegido en Windows (el mismo en todas las pantallas)
        string openGroup, pressedGroup;     // grupo con el menú de sus apps abierto, y el que lo tenía al pulsar el ratón
        bool moveMode;                      // las islas se arrastran (lo activa la aplicación hasta que se confirma)
        int lastEdgeCheck, lastPosChanged;
        bool userAutoHide;                  // lo que el usuario tiene elegido en "Ocultar automáticamente" (solo vale con la barra abajo)

        // Compatibilidad interna: el monitor principal conserva los nombres históricos de las islas.
        LeftIsland left { get { return primary != null ? primary.Left : null; } }
        RightIsland right { get { return primary != null ? primary.Right : null; } }
        PlateIsland plate { get { return primary != null ? primary.Plate : null; } }
        StripFill[] fills { get { return primary != null ? primary.Fills : null; } }
        RECT monRect { get { return primary != null ? primary.MonRect : new RECT(); } set { if (primary != null) primary.MonRect = value; } }
        int layH { get { return primary != null ? primary.LayH : 0; } set { if (primary != null) primary.LayH = value; } }
        int layMargin { get { return primary != null ? primary.LayMargin : 0; } set { if (primary != null) primary.LayMargin = value; } }
        Spot spotL { get { return primary != null ? primary.SpotL : new Spot(); } }
        Spot spotR { get { return primary != null ? primary.SpotR : new Spot(); } }
        bool joined { get { return primary != null && primary.Joined; } }
        bool following { get { return primary != null && primary.Following; } set { if (primary != null) primary.Following = value; } }
        bool full { get { return primary != null && primary.Full; } }
        bool OnePiece { get { return primary != null && primary.OnePiece; } }
        bool islandsHiddenForFullscreen { get { return primary != null && primary.IslandsHiddenForFullscreen; } set { if (primary != null) primary.IslandsHiddenForFullscreen = value; } }
        bool smallWhenFull { get { return primary != null && primary.SmallWhenFull; } set { if (primary != null) primary.SmallWhenFull = value; } }
        bool barReserved { get { return primary != null && primary.BarReserved; } set { if (primary != null) primary.BarReserved = value; } }
        int hideDist { get { return primary != null ? primary.HideDist : 0; } set { if (primary != null) primary.HideDist = value; } }
        int barThick { get { return primary != null ? primary.BarThick : 0; } set { if (primary != null) primary.BarThick = value; } }
        int lastBarHeight { get { return primary != null ? primary.LastBarHeight : -1; } set { if (primary != null) primary.LastBarHeight = value; } }
        int lastStripSig { get { return primary != null ? primary.LastStripSig : -1; } set { if (primary != null) primary.LastStripSig = value; } }
        RECT lastBarRect { get { return primary != null ? primary.LastBarRect : new RECT(); } set { if (primary != null) primary.LastBarRect = value; } }
        Edge[] strips { get { return primary != null ? primary.Strips : new Edge[2]; } }
        int stripCount { get { return primary != null ? primary.StripCount : 1; } set { if (primary != null) primary.StripCount = value; } }

        // ------------------------------------------------------------------ ciclo de vida

        public static int Run()
        {
            instance = new App();
            return instance.Main();
        }

        int Main()
        {
            Config.Load();
            ApplyMotion();
            try { Native.SetPreferredAppMode(1); Native.FlushMenuThemes(); } catch { }

            CreateHost();
            wmShellHook = Native.RegisterWindowMessageW("SHELLHOOK");
            wmTaskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
            desktopWindow = Native.GetDesktopWindow();

            pins = new PinStore();
            pins.Load();

            volume = new VolumeMonitor(host, WM_APP_VOLUME);

            flyout = new ThumbFlyout();
            flyout.Init();
            flyout.OnActivate = h => Activate(h);
            flyout.OnClose = h => Native.PostMessageW(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            menu = new TaskbarMenu();
            menu.Init();

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

        internal static void NotifyAppBar(IntPtr w)
        {
            if (instance != null && instance.host != IntPtr.Zero)
                Native.PostMessageW(instance.host, WM_APP_APPBAR, w, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ barras (una por monitor)

        // Crea o quita barras según los monitores que haya en cada momento (arranque, cambio de pantallas).
        void SyncBars()
        {
            var monitors = Native.Monitors();
            if (monitors.Count == 0) return;
            IntPtr primaryMon = Native.MonitorFromPoint(new POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */);
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var mon in monitors)
                {
                    RECT mr;
                    bool pri;
                    if (Native.MonitorInfo(mon, out mr, out pri)) parts.Add((pri ? "primary:" : "secondary:") + mr.Left + "," + mr.Top + "-" + mr.Right + "," + mr.Bottom);
                }
                Program.Log("monitores " + monitors.Count + " " + string.Join("|", parts.ToArray()) + " barras=" + bars.Count);
            }
            catch { }

            for (int i = bars.Count - 1; i >= 0; i--)
            {
                if (monitors.Contains(bars[i].Monitor)) continue;
                DestroyBar(bars[i]);
                bars.RemoveAt(i);
            }

            foreach (var mon in monitors)
            {
                RECT rc;
                bool isPrimary;
                if (!Native.MonitorInfo(mon, out rc, out isPrimary)) continue;
                Bar found = null;
                foreach (var b in bars) if (b.Monitor == mon) { found = b; break; }
                // Un monitor que cambia de identificador se reconoce por su rectángulo.
                if (found == null)
                    foreach (var b in bars)
                        if (Native.SameRect(b.MonRect, rc)) { found = b; b.Monitor = mon; break; }
                if (found == null) CreateBar(mon, rc, isPrimary);
                else { found.MonRect = rc; found.Primary = isPrimary; }
            }

            foreach (var b in bars) b.Primary = b.Monitor == primaryMon;
            foreach (var b in bars)
            {
                b.Right.Secondary = !b.Primary;
                ApplyTints(b);
            }
            bars.Sort((a, b) =>
            {
                if (a.Primary != b.Primary) return a.Primary ? -1 : 1;
                if (a.MonRect.Left != b.MonRect.Left) return a.MonRect.Left - b.MonRect.Left;
                return a.MonRect.Top - b.MonRect.Top;
            });
            primary = null;
            foreach (var b in bars) if (b.Primary) { primary = b; break; }
            if (primary == null && bars.Count > 0) primary = bars[0];
            if (primary != null) primary.Primary = true;
        }

        void CreateBar(IntPtr mon, RECT rc, bool isPrimary)
        {
            var b = new Bar();
            b.Monitor = mon;
            b.MonRect = rc;
            b.Dpi = Native.GetMonitorDpi(mon);
            b.Primary = isPrimary;
            b.CreateHosts();
            for (int i = 0; i < b.Fills.Length; i++)   // antes que las islas: deben quedar por debajo de ellas
            {
                b.Fills[i] = new StripFill();
                b.Fills[i].Init();
            }
            b.Plate = new PlateIsland();   // y la placa, por debajo de las dos
            b.Plate.Init();
            b.Left = new LeftIsland();
            b.Left.Init();
            b.Right = new RightIsland();
            b.Right.Init();
            bars.Add(b);
            WireBar(b);
            ApplyTints(b);
            if (isPrimary) primary = b;
        }

        void DestroyBar(Bar b)
        {
            foreach (var f in b.Fills) if (f != null) f.Destroy();
            if (b.Right != null) b.Right.Destroy();
            if (b.Left != null) b.Left.Destroy();
            if (b.Plate != null) b.Plate.Destroy();
            foreach (var t in b.Targets) if (t != IntPtr.Zero) Native.DestroyWindow(t);
            ShellTaskbar.UnregisterBar(b.Host, b.Primary);
            ShellTaskbar.UnregisterBar(b.Host2, b.Primary);
            b.DestroyHosts();
            if (overflowBar == b) overflowBar = null;
        }

        // Cada isla nueva atiende los mismos clics que las demás, pero sabe en qué barra está.
        void WireBar(Bar b)
        {
            var bar = b;
            bar.Left.Moved = bar.Right.Moved = () => JoinFollow(bar);
            bar.Left.OnMoveStart = bar.Right.OnMoveStart = isl => MoveStart(bar, isl);
            bar.Left.OnMoving = bar.Right.OnMoving = (isl, x, y) => Moving(bar, isl, x, y);
            bar.Left.OnMoveEnd = bar.Right.OnMoveEnd = (isl, x, y) => MoveEnd(bar, isl, x, y);

            bar.Left.OnHover = (it, r) =>
            {
                SetFlyoutContext(bar);
                flyout.Hover(Config.Previews && it != null && it.Members == null ? it : null, r);
            };
            bar.Left.OnGroupClick = (it, x, y) => GroupOpen(bar, it, x, y);
            bar.Left.OnItemWheel = ItemWheel;
            bar.Left.OnReorder = ReorderItems;
            bar.Left.OnAnyMouseDown = bar.Right.OnAnyMouseDown = () =>
            {
                // Se recuerda si el menú abierto era el de un grupo: pulsar de nuevo ese grupo lo cierra, no lo reabre.
                pressedGroup = menu.Visible ? openGroup : null;
                openGroup = null;
                menu.Close();
            };

            bar.Left.OnStartDown = () => toggleClose = WasOpenAtPress(FlyKind.Start, true);
            bar.Left.OnStartClick = () =>
            {
                flyout.HideNow();
                if (!Toggled(FlyKind.Start)) Native.PressKeys(Native.VK_LWIN);
            };
            bar.Left.OnToolClick = id =>
            {
                flyout.HideNow();
                if (id == "search") Native.PressKeys(Native.VK_LWIN, 0x53 /* S */);
                else if (id == "taskview") Native.PressKeys(Native.VK_LWIN, 0x09 /* TAB */);
                else if (id == "widgets") Native.PressKeys(Native.VK_LWIN, 0x57 /* W */);
            };
            bar.Left.OnStartContext = () => Native.PressKeys(Native.VK_LWIN, 0x58 /* X */);
            bar.Left.OnItemClick = it => { flyout.HideNow(); ItemClick(it); };
            bar.Left.OnItemMiddle = ItemMiddle;
            bar.Left.OnItemContext = (it, x, y) => { flyout.HideNow(); ItemContext(bar, it, x, y); };
            bar.Left.OnBlankContext = (x, y) => ShowTaskbarMenu(bar, x, y);
            bar.Left.OnFilesDropped = FilesDropped;

            bar.Right.Volume = () => volume;
            bar.Right.OnButtonDown = kind =>
            {
                if (kind == RightIsland.Chevron) toggleClose = WasOpenAtPress(FlyKind.Overflow, true);
                else if (kind == RightIsland.Sys) toggleClose = WasOpenAtPress(FlyKind.Shell, shellOwner == 1);
                else toggleClose = WasOpenAtPress(FlyKind.Shell, shellOwner == 2);
            };
            bar.Right.OnChevronClick = () => { if (!Toggled(FlyKind.Overflow)) OpenHiddenIcons(bar); };
            bar.Right.OnChevronContext = (x, y) => ShowTaskbarMenu(bar, x, y);
            bar.Right.OnSysClick = () =>
            {
                if (Toggled(FlyKind.Shell)) { shellOwner = 0; return; }
                shellOwner = 1;
                Native.PressKeys(Native.VK_LWIN, 0x41 /* A */);
            };
            bar.Right.OnClockClick = () =>
            {
                if (Toggled(FlyKind.Shell)) { shellOwner = 0; return; }
                shellOwner = 2;
                if (Island.IsWin11) Native.PressKeys(Native.VK_LWIN, 0x4E /* N */);
                else Native.PressKeys(Native.VK_LWIN, 0x12 /* ALT */, 0x44 /* D */);
            };
            bar.Right.OnKeyboardClick = () =>
            {
                string tip = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    @"microsoft shared\ink\TabTip.exe");
                Native.Launch(tip, null);
            };
            bar.Right.OnDesktopClick = () => Native.PressKeys(Native.VK_LWIN, 0x44 /* D */);
            bar.Right.OnStatsClick = () => Native.Launch("taskmgr.exe", null);
            // Teclas multimedia: las atiende el reproductor que esté sonando.
            bar.Right.OnMediaClick = i => Native.PressKeys(i == 0 ? (byte)0xB1 /* anterior */ : i == 1 ? (byte)0xB3 /* reproducir o pausar */ : (byte)0xB0 /* siguiente */);
            bar.Right.OnVolumeWheel = delta => { volume.Step(delta > 0); bar.Right.Refresh(); };
            bar.Right.OnSysContext = (x, y) => SysContext(bar, x, y);
            bar.Right.OnClockContext = (x, y) => ClockContext(bar, x, y);

            bar.Left.DpiChanged = bar.Right.DpiChanged = () => { if (!paused) { bar.LastBarHeight = -1; LayoutBar(bar); } };
        }

        // Contexto del flyout de vistas previas: el de la barra desde la que se pide.
        void SetFlyoutContext(Bar b)
        {
            flyout.Dpi = b.Dpi;
            flyout.Monitor = b.MonRect;
            flyout.Edge = b.SpotL.Edge;
            flyout.Anchor = AnchorOf(b, b.SpotL.Edge);
        }

        void ApplyTools()
        {
            Config.ReadWindowsTaskbarSettings();
            var ids = new List<string>();
            if (Config.ShowSearch) ids.Add("search");
            if (Config.ShowTaskView) ids.Add("taskview");
            if (Config.ShowWidgets) ids.Add("widgets");
            foreach (var b in bars)
            {
                b.Left.SearchMode = Config.SearchMode;
                b.Left.SetTools(ids.ToArray());
            }
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
        // Unidas (o en la barra completa) forman una sola barra: se esconde entera con que se haya pedido esconder una de las dos.
        static bool HidesLeft { get { return Config.AutoHide || Config.HideLeft || (Config.OneBar && Config.HideRight); } }
        static bool HidesRight { get { return Config.AutoHide || Config.HideRight || (Config.OneBar && Config.HideLeft); } }

        // Ocultación: arranca o detiene la vigilancia del cursor y deja a la vista las islas que no se esconden.
        // Mientras se mueven las islas no se esconde ninguna.
        void UpdateAutoHide()
        {
            bool l = HidesLeft && !paused && !moveMode, r = HidesRight && !paused && !moveMode;
            if (l || r) Native.SetTimer(host, new UIntPtr(T_HIDE), 100, IntPtr.Zero);
            else Native.KillTimer(host, new UIntPtr(T_HIDE));
            foreach (var b in bars)
            {
                b.LeftWantedTick = b.RightWantedTick = Environment.TickCount;
                if (!l) { b.LeftRevealed = true; b.Left.SetAutoHidden(false, b.HideDist); b.Plate.SetAutoHidden(false, b.HideDist); }
                if (!r) { b.RightRevealed = true; b.Right.SetAutoHidden(false, b.HideDist); }
            }
        }

        // Distancia del cursor al borde de pantalla en el que está una isla.
        static int EdgeDist(RECT rc, Edge e, POINT p)
        {
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
            bool start = IsFlyoutOpen(FlyKind.Start), panels = IsFlyoutOpen(FlyKind.Shell) || IsFlyoutOpen(FlyKind.Overflow);
            bool busy = menu.Visible || flyout.Visible || start || panels;
            foreach (var b in bars) AutoHideTick(b, p, start, panels, busy);
        }

        void AutoHideTick(Bar b, POINT p, bool start, bool panels, bool busy)
        {
            RECT rc = b.MonRect;
            bool inside = p.X >= rc.Left && p.X < rc.Right && p.Y >= rc.Top && p.Y < rc.Bottom;
            int near = b.Left.S(2), zone = b.HideDist;
            // Cada isla mira a su propio borde de pantalla (pueden estar en bordes distintos).
            int dl = EdgeDist(rc, b.Left.Edge, p), dr = EdgeDist(rc, b.Right.Edge, p);
            bool wantLeft, wantRight, holdLeft, holdRight;
            if (Config.AutoHide)
            {
                wantLeft = (inside && dl <= near) || busy;
                wantRight = (inside && dr <= near) || busy;
                holdLeft = inside && dl <= zone;
                holdRight = inside && dr <= zone;
            }
            else
            {
                // La franja sigue reservada, así que en el sitio de la isla escondida no hay nada más: basta con pasar por él.
                // (La barra completa ocupa todo el borde: vale cualquier punto de él.)
                bool overLeft = b.Full || OverIsland(b.Left, p), overRight = b.Full || OverIsland(b.Right, p);
                wantLeft = (inside && dl < b.BarThick && overLeft) || flyout.Visible || start;
                wantRight = (inside && dr < b.BarThick && overRight) || panels;
                holdLeft = (inside && dl <= zone && overLeft) || menu.Visible;
                holdRight = (inside && dr <= zone && overRight) || menu.Visible;
            }
            if (b.OnePiece)
            {
                wantLeft = wantRight = wantLeft || wantRight;
                holdLeft = holdRight = holdLeft || holdRight;
            }
            if (HidesLeft && HideStep(b.Left, ref b.LeftRevealed, ref b.LeftWantedTick, wantLeft, holdLeft, b.HideDist)) flyout.HideNow();
            if (HidesRight) HideStep(b.Right, ref b.RightRevealed, ref b.RightWantedTick, wantRight, holdRight, b.HideDist);
            if (b.OnePiece) b.Plate.SetAutoHidden(!b.LeftRevealed, b.HideDist);
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
        bool HideStep(Island isl, ref bool revealed, ref int wantedTick, bool want, bool hold, int dist)
        {
            int now = Environment.TickCount;
            if (want || (revealed && hold))
            {
                wantedTick = now;
                if (!revealed)
                {
                    revealed = true;
                    isl.SetAutoHidden(false, dist);
                    isl.RaiseTopmost();
                }
            }
            else if (revealed && (now - wantedTick > Config.HideDelay || now < wantedTick))
            {
                revealed = false;
                isl.SetAutoHidden(true, dist);
                return true;
            }
            return false;
        }

        // Las islas transparentes ajustan su tinta a lo que tienen detrás, que cambia sin que ellas se enteren.
        void WatchBackdrops()
        {
            foreach (var b in bars) { b.Left.WatchBackdrop(); b.Right.WatchBackdrop(); }
        }

        // La aplicación ha guardado un ajuste en config.ini: se vuelve a leer y se aplica al momento.
        void ReloadConfig()
        {
            Config.Load();
            ApplyMotion();
            ApplyTints();
            UpdateTrayTip();   // por si ha cambiado el idioma
            if (!Config.Previews) flyout.HideNow();
            if (Config.FullBar) SetMoveMode(false);
            pins.LoadGroups();   // la aplicación puede haber deshecho los grupos
            if (paused) return;
            ApplyStats();
            foreach (var b in bars) b.Right.UpdateClock();   // por si ha cambiado el formato de la hora o de la fecha
            foreach (var b in bars) b.LastBarHeight = -1;
            lastSig = "";
            ApplyLayout();
            UpdateAutoHide();
        }

        // Color de cada isla (el elegido, o el del tema de Windows si se sigue). Unidas o en la barra completa, el color
        // lo lleva la placa (el de la isla de aplicaciones) y las dos islas van transparentes encima de ella.
        void ApplyTints()
        {
            foreach (var b in bars) ApplyTints(b);
        }

        void ApplyTints(Bar b)
        {
            if (b.Left == null) return;
            uint tl = Config.IslandTint(Config.TintLeft), tr = Config.IslandTint(Config.TintRight);
            if (Config.OneBar)
            {
                b.Plate.RefreshTint(tl);
                b.Left.RefreshTint(0);
                b.Right.RefreshTint(0);
                if (!b.Plate.Clear) { b.Left.AdoptInk(b.Plate); b.Right.AdoptInk(b.Plate); }
            }
            else
            {
                b.Left.RefreshTint(tl);
                b.Right.RefreshTint(tr);
            }
            // El contorno lo lleva cada isla o, unidas, la placa que hace de fondo de las dos.
            b.Left.Outline = b.Right.Outline = Config.Border && !Config.OneBar;
            b.Plate.Outline = Config.Border && Config.OneBar;
            b.Left.Invalidate();
            b.Right.Invalidate();
            b.Plate.Invalidate();
        }

        // Animaciones: ninguna si así se ha elegido, o mientras el equipo va con batería si se ha pedido eso.
        void ApplyMotion()
        {
            bool calm = Config.ReduceMotion;
            if (!calm && Config.CalmOnBattery)
            {
                var b = BatteryState.Read();
                calm = b.Present && !b.AcPower;
            }
            Ease.Instant = calm;
        }

        // Uso del procesador y de la memoria: solo se mide mientras se muestra alguno de los dos.
        void ApplyStats()
        {
            if (paused || !(Config.ShowCpu || Config.ShowRam)) { Native.KillTimer(host, new UIntPtr(T_STATS)); return; }
            stats.Sample();
            foreach (var b in bars) b.Right.UpdateStats(stats.Cpu, stats.Ram);
            Native.SetTimer(host, new UIntPtr(T_STATS), 2000, IntPtr.Zero);
        }

        void ShowIslands(Bar b, bool show)
        {
            b.Left.Show(show);
            b.Right.Show(show);
            b.Plate.Show(show && b.OnePiece);
            if (show && b.OnePiece) b.Plate.PlaceBehind(b.Left.Hwnd);
        }

        void ShowIslands(bool show)
        {
            if (primary != null) ShowIslands(primary, show);
        }

        // Por encima de las demás ventanas, con la de información sobre la de aplicaciones y la placa bajo las dos.
        void RaiseIslands(Bar b)
        {
            b.Left.RaiseTopmost();
            b.Right.RaiseTopmost();
            if (b.OnePiece) b.Plate.PlaceBehind(b.Left.Hwnd);
        }

        void RaiseIslands()
        {
            if (primary != null) RaiseIslands(primary);
        }

        // ------------------------------------------------------------------ mover las islas

        // La aplicación abre y cierra el modo mover: mientras dura, las islas se arrastran a la esquina que se quiera.
        // Se mueven las de la pantalla principal; las demás siguen la misma configuración.
        void SetMoveMode(bool on)
        {
            if (on && (paused || Config.FullBar)) on = false;   // la barra completa no tiene islas que mover
            if (on == moveMode) return;
            moveMode = on;
            flyout.HideNow();
            menu.Close();
            foreach (var b in bars)
            {
                bool movable = on && b.Primary;
                b.Left.Movable = b.Right.Movable = movable;
                ShowTargets(b, null);
                b.Left.Invalidate();
                b.Right.Invalidate();
            }
            UpdateAutoHide();
        }

        // Esquina (0 arriba-izq., 1 arriba-dcha., 2 abajo-izq., 3 abajo-dcha.) que ocupa un sitio, y al revés. Las islas
        // van tumbadas o de pie como la barra de Windows. Los sitios 4 y 5 son el centro de arriba y el de abajo: solo
        // existen con las islas tumbadas (de pie, la isla se centra en el borde de la barra de Windows).
        static bool IsVert(Edge e) { return e == Edge.Left || e == Edge.Right; }

        static int Cross(Edge e)
        {
            if (instance == null || instance.primary == null) return 0;
            return Cross(instance.primary, e);
        }

        static int AnchorOf(Edge e)
        {
            if (instance == null || instance.primary == null) return 0;
            return AnchorOf(instance.primary, e);
        }

        static int CornerOf(Spot s)
        {
            if (IsVert(s.Edge)) return (s.Edge == Edge.Right ? 1 : 0) | (s.End ? 2 : 0);
            return (s.Edge == Edge.Bottom ? 2 : 0) | (s.End ? 1 : 0);
        }

        Spot SpotAt(int corner)
        {
            bool right = (corner & 1) != 0, bottom = (corner & 2) != 0;
            var s = new Spot();
            if (corner >= Config.CornerCount)
            {
                s.Edge = IsVert(barEdge) ? barEdge : corner == 5 ? Edge.Bottom : Edge.Top;
                s.Centered = true;
            }
            else if (IsVert(barEdge)) { s.Edge = right ? Edge.Right : Edge.Left; s.End = bottom; }
            else { s.Edge = bottom ? Edge.Bottom : Edge.Top; s.End = right; }
            return s;
        }

        // Dónde va cada isla: en la esquina elegida o, si no se ha elegido, donde manda la barra de Windows (la de
        // aplicaciones al inicio o centrada; la de información al final). Unidas, la segunda sigue a la primera.
        void ResolveSpots(Bar b)
        {
            b.SpotL = Config.CornerLeft >= 0 ? SpotAt(Config.CornerLeft) : new Spot { Edge = barEdge, Centered = Config.Center };
            b.SpotR = Config.CornerRight >= 0 ? SpotAt(Config.CornerRight) : new Spot { Edge = barEdge, End = true };
            b.Full = Config.FullBar;
            b.Joined = Config.Join && !b.Full;
            if (b.Full)
            {
                // Barra completa, como la de Windows: en su borde, las apps al inicio o centradas y la información al final.
                b.SpotL = new Spot { Edge = barEdge, Centered = Config.Center };
                b.SpotR = new Spot { Edge = barEdge, End = true };
            }
            else if (b.Joined) { b.SpotR = b.SpotL; b.SpotR.Centered = false; }
            else if (!b.SpotL.Centered && CornerOf(b.SpotL) == CornerOf(b.SpotR)) b.SpotR.End = !b.SpotR.End;   // nunca las dos en la misma
            b.Strips[0] = b.SpotL.Edge;
            b.Strips[1] = b.SpotR.Edge;
            b.StripCount = b.SpotL.Edge == b.SpotR.Edge ? 1 : 2;
        }

        void ResolveSpots()
        {
            if (primary != null) ResolveSpots(primary);
        }

        static bool EdgeUsed(Bar b, Edge e) { return b.Strips[0] == e || (b.StripCount > 1 && b.Strips[1] == e); }

        bool EdgeUsed(Edge e) { return primary != null && EdgeUsed(primary, e); }

        // Coordenada del lado de una isla pegado a su borde de pantalla, y la del lado que mira al centro.
        static int Cross(Bar b, Edge e)
        {
            RECT rc = b.MonRect;
            return e == Edge.Top ? rc.Top + b.LayMargin : e == Edge.Left ? rc.Left + b.LayMargin : e == Edge.Right ? rc.Right - b.LayMargin : rc.Bottom - b.LayMargin;
        }

        static int AnchorOf(Bar b, Edge e) { return e == Edge.Top || e == Edge.Left ? Cross(b, e) + b.LayH : Cross(b, e) - b.LayH; }

        // Rectángulo que ocuparía en una esquina (o en el centro de su borde) una isla de esa longitud.
        RECT CornerRect(Bar b, int corner, int len)
        {
            Spot s = SpotAt(corner);
            RECT rc = b.MonRect;
            bool vert = IsVert(s.Edge);
            int a0 = vert ? rc.Top : rc.Left, a1 = vert ? rc.Bottom : rc.Right;
            int along = s.Centered ? (a0 + a1 - len) / 2 : s.End ? a1 - b.LayMargin - len : a0 + b.LayMargin;
            int c0 = Math.Min(Cross(b, s.Edge), AnchorOf(b, s.Edge));
            return vert ? new RECT(c0, along, c0 + b.LayH, along + len) : new RECT(along, c0, along + len, c0 + b.LayH);
        }

        void MoveStart(Bar b, Island isl)
        {
            RECT w;
            Native.GetWindowRect(isl.Hwnd, out w);
            b.MoveOrigin = new POINT((w.Left + w.Right) / 2, (w.Top + w.Bottom) / 2);
            ShowTargets(b, isl);
        }

        void Moving(Bar b, Island isl, int x, int y)
        {
            if (b.Joined)
            {
                // Unidas van juntas: la otra isla y la placa acompañan a la que se arrastra.
                (isl == b.Left ? (Island)b.Right : b.Left).FloatBy(x - b.MoveOrigin.X, y - b.MoveOrigin.Y);
                b.Plate.FloatBy(x - b.MoveOrigin.X, y - b.MoveOrigin.Y);
            }
            int hot = NearestTarget(b, x, y);
            if (hot == b.TargetHot) return;
            b.TargetHot = hot;
            for (int i = 0; i < b.Targets.Length; i++)
                if (b.TargetFree[i]) Native.SetLayeredWindowAttributes(b.Targets[i], 0, (byte)(i == hot ? 150 : 60), 2 /* LWA_ALPHA */);
        }

        // Al soltar la isla se queda en el sitio libre más cercano, y se guarda.
        void MoveEnd(Bar b, Island isl, int x, int y)
        {
            int corner = NearestTarget(b, x, y);
            ShowTargets(b, null);
            b.Left.Settle();
            b.Right.Settle();
            b.Plate.Settle();
            if (corner < 0) return;
            if (b.Joined || isl == b.Left)
            {
                Config.CornerLeft = corner;
                Config.Save("cornerleft", Config.CornerName(corner));
            }
            else
            {
                Config.CornerRight = corner;
                Config.Save("cornerright", Config.CornerName(corner));
            }
            foreach (var bar in bars) bar.LastBarHeight = -1;
            lastSig = "";
            ApplyLayout();
        }

        static int NearestTarget(Bar b, int x, int y)
        {
            int best = -1;
            long bestD = long.MaxValue;
            for (int i = 0; i < b.Targets.Length; i++)
            {
                if (!b.TargetFree[i]) continue;
                RECT r = b.TargetRects[i];
                long dx = x - (r.Left + r.Right) / 2, dy = y - (r.Top + r.Bottom) / 2;
                if (dx * dx + dy * dy < bestD) { bestD = dx * dx + dy * dy; best = i; }
            }
            return best;
        }

        const string TargetClass = "NeuTaskBar.Target";
        static readonly WndProcDelegate TargetProc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);

        // Marcas translúcidas en las esquinas donde puede soltarse la isla que se arrastra (null = quitarlas): todas
        // menos la que ocupa la otra isla; unidas se mueven juntas y vale cualquiera. La isla de aplicaciones, si se
        // mueve sola y va tumbada, tiene además el centro de arriba y el de abajo.
        void ShowTargets(Bar b, Island isl)
        {
            b.TargetHot = -1;
            if (isl != null && b.Targets[0] == IntPtr.Zero)
            {
                var wc = new WNDCLASS();
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(TargetProc);
                wc.hInstance = Native.GetModuleHandleW(null);
                wc.hbrBackground = Native.CreateSolidBrush(Native.Rgb(76, 194, 255));
                wc.lpszClassName = TargetClass;
                Native.RegisterClassW(ref wc);
                int ex = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
                for (int i = 0; i < b.Targets.Length; i++)
                {
                    b.Targets[i] = Native.CreateWindowExW(ex, TargetClass, "", Native.WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                    if (!Island.IsWin11) continue;
                    int round = 2; // DWMWCP_ROUND
                    Native.DwmSetWindowAttribute(b.Targets[i], Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                }
            }
            if (b.Targets[0] == IntPtr.Zero) return;
            Spot other = isl == b.Left ? b.SpotR : b.SpotL;
            int taken = isl == null || b.Joined || (isl == b.Right && b.SpotL.Centered) ? -1 : CornerOf(other);
            int len = b.Joined ? b.Left.VisW + b.Right.VisW : isl == null ? 0 : isl.VisW;
            bool center = isl == b.Left && !b.Joined && !IsVert(barEdge);
            for (int i = 0; i < b.Targets.Length; i++)
            {
                b.TargetFree[i] = isl != null && i != taken && (i < Config.CornerCount || center);
                if (!b.TargetFree[i]) { Native.ShowWindow(b.Targets[i], Native.SW_HIDE); continue; }
                RECT r = b.TargetRects[i] = CornerRect(b, i, len);
                Native.SetLayeredWindowAttributes(b.Targets[i], 0, 60, 2 /* LWA_ALPHA */);
                Native.SetWindowPos(b.Targets[i], Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
            }
        }

        // Islas unidas: la de información va pegada a continuación de la de aplicaciones (o esta termina donde empieza
        // aquella, si la barra va al final del borde), y la placa abarca las dos. Se repasa cada vez que una cambia de
        // sitio o de tamaño. En la barra completa cada isla se queda en su extremo y la placa va de lado a lado.
        void JoinFollow(Bar b)
        {
            if (!b.OnePiece || b.Following || b.Plate == null) return;
            b.Following = true;
            Edge e = b.SpotL.Edge;
            bool vert = IsVert(e);
            int cross = Cross(b, e);
            if (b.Full)
            {
                int a0 = vert ? b.MonRect.Top : b.MonRect.Left, a1 = vert ? b.MonRect.Bottom : b.MonRect.Right;
                b.Plate.VisW = Math.Max(1, a1 - a0 - b.LayMargin * 2);
                b.Plate.VisH = b.Left.VisH;
                b.Plate.SetPlacement(e, cross, a0 + b.LayMargin);
                b.Following = false;
                return;
            }
            if (b.SpotL.End)
            {
                RECT r = b.Right.RestRect;
                b.Left.SetPlacement(e, cross, vert ? r.Top : r.Left);
            }
            else
            {
                RECT l = b.Left.RestRect;
                b.Right.SetPlacement(e, cross, vert ? l.Bottom : l.Right);
            }
            RECT first = b.Left.RestRect;
            b.Plate.VisW = b.Left.VisW + b.Right.VisW;
            b.Plate.VisH = b.Left.VisH;
            b.Plate.SetPlacement(e, cross, vert ? first.Top : first.Left);
            b.Following = false;
        }

        void JoinFollow()
        {
            if (primary != null) JoinFollow(primary);
        }

        void Resume()
        {
            paused = false;
            started = true;
            SyncBars();
            if (tracker == null && primary != null) tracker = new WindowTracker(pins, primary.Left.Icons, IntPtr.Zero, IntPtr.Zero);
            Native.SetTimer(host, new UIntPtr(T_CLOCK), 1000, IntPtr.Zero);
            Native.SetTimer(host, new UIntPtr(T_TOOLS), 700, IntPtr.Zero);

            volume.Attach();
            ApplyTints();
            ApplyTools();
            ApplyStats();
            foreach (var b in bars)
            {
                b.Right.UpdateNetwork(NetworkProbe.Detect());
                b.Right.UpdateBattery(BatteryState.Read());
                b.Right.UpdateClock();
            }
            SubscribeNetwork();

            if (!SetupShell()) Native.SetTimer(host, new UIntPtr(T_SETUP), 1000, IntPtr.Zero);
            InstallHooks();
            FollowTaskbarSize(barEdge);
            ApplyLayout();
            lastSig = "";
            RefreshApps();
            foreach (var b in bars) ShowIslands(b, !b.IslandsHiddenForFullscreen);
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
            foreach (var b in bars) b.LastBarHeight = -1;
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
            Native.KillTimer(host, new UIntPtr(T_STATS));
            Native.KillTimer(host, new UIntPtr(T_WATCH));
            RemoveHooks();
            flyout.HideNow();
            menu.Close();
            SetMoveMode(false);
            foreach (var b in bars)
            {
                ShowIslands(b, false);
                foreach (var f in b.Fills) f.Hide();
                ShellTaskbar.UnregisterBar(b.Host, b.Primary);
                ShellTaskbar.UnregisterBar(b.Host2, b.Primary);
            }
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
            foreach (var b in bars) DestroyBar(b);
            bars.Clear();
            primary = null;
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
                    if (!paused) right.NotifyVolume();
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
                    ApplyMotion();   // enchufar o desenchufar el cargador puede cambiar si hay animaciones
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
                    if (Config.ReadWindowsTheme()) ApplyTints();   // modo claro u oscuro, o color de énfasis, de Windows
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
                    foreach (var bar in bars)
                        foreach (var f in bar.Fills) f.Watch();
                    WatchBackdrops();
                    break;
                case T_HIDE:
                    AutoHideTick();
                    break;
                case T_STATS:
                    if (paused) { Native.KillTimer(host, new UIntPtr(T_STATS)); break; }
                    stats.Sample();
                    foreach (var bar in bars) bar.Right.UpdateStats(stats.Cpu, stats.Ram);
                    break;
                case T_CLOCK:
                    if (paused) { Native.KillTimer(host, new UIntPtr(T_CLOCK)); break; }
                    BatteryState battery = BatteryState.Read();
                    foreach (var bar in bars)
                    {
                        bar.Right.UpdateClock();
                        bar.Right.UpdateBattery(battery);
                    }
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
                    if (!paused)
                    {
                        NetKind network = NetworkProbe.Detect();
                        foreach (var bar in bars) bar.Right.UpdateNetwork(network);
                    }
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

        void OpenHiddenIcons(Bar bar)
        {
            if (bar.Primary) OpenHiddenIcons();
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
            SyncBars();
            foreach (var bar in bars) LayoutBar(bar);
        }

        void LayoutBar(Bar b)
        {
            if (b == null || b.Left == null || b.Left.Hwnd == IntPtr.Zero) return;

            RECT rc = b.MonRect;
            int dpi = Native.GetMonitorDpi(b.Monitor);
            b.Dpi = dpi;
            b.Left.Dpi = b.Right.Dpi = b.Plate.Dpi = dpi;
            b.Right.Secondary = !b.Primary;
            ApplyTints(b);

            int h = b.Left.S(Config.BarHeight);
            bool small = Config.SmallButtons == 0 || (Config.SmallButtons == 2 && b.SmallWhenFull);
            b.Left.Small = small;
            if (b.Left.Icons.SetPx(b.Left.S(small ? Math.Max(16, Config.BarIconSize * 2 / 3) : Config.BarIconSize))) lastSig = "";

            int margin = b.Left.S(Config.Margin);
            b.LayH = h;
            b.LayMargin = margin;
            b.MonRect = rc;
            bool wasPiece = b.OnePiece;
            ResolveSpots(b);

            Edge el = b.SpotL.Edge, er = b.SpotR.Edge;
            bool vert = IsVert(barEdge);
            int a0 = vert ? rc.Top : rc.Left, a1 = vert ? rc.Bottom : rc.Right;
            flyout.Dpi = dpi;
            if (b == primary)
            {
                menu.Dpi = dpi;
                menu.Monitor = rc;
                flyout.Monitor = rc;
            }

            if (b.OnePiece != wasPiece) ApplyTints(b);
            b.Following = true;
            if (b.Joined)
            {
                b.Right.AnchorEnd = b.SpotL.End;
                b.Right.SetPlacement(el, Cross(b, el), b.SpotL.End ? a1 - margin : a0 + margin);
                b.Right.Layout();
                b.Left.MaxWidth = a1 - a0 - b.Right.VisW - margin * 2 - b.Left.S(24);
                b.Left.Centered = b.SpotL.Centered;
                b.Left.CenterPos = (a0 + a1 - b.Right.VisW) / 2;
                b.Left.CenterMin = a0 + margin;
                b.Left.CenterMax = a1 - margin - b.Right.VisW;
                b.Left.AnchorEnd = b.SpotL.End;
                b.Left.SetPlacement(el, Cross(b, el), b.SpotL.End ? a1 - margin - b.Right.VisW : a0 + margin);
                b.Left.Layout();
            }
            else
            {
                bool same = el == er;
                b.Right.AnchorEnd = b.SpotR.End;
                b.Right.SetPlacement(er, Cross(b, er), b.SpotR.End ? a1 - margin : a0 + margin);
                b.Right.Layout();
                b.Left.MaxWidth = a1 - a0 - (same ? b.Right.VisW + margin : 0) - margin * 2 - b.Left.S(24);
                b.Left.Centered = b.SpotL.Centered;
                b.Left.CenterPos = (a0 + a1) / 2;
                b.Left.CenterMin = a0 + margin + (same && !b.SpotR.End ? b.Right.VisW + b.Left.S(12) : 0);
                b.Left.CenterMax = a1 - margin - (same && b.SpotR.End ? b.Right.VisW + b.Left.S(12) : 0);
                b.Left.AnchorEnd = b.SpotL.End;
                b.Left.SetPlacement(el, Cross(b, el), b.SpotL.End ? a1 - margin : a0 + margin);
                b.Left.Layout();
            }
            b.Following = false;
            JoinFollow(b);
            if (b.OnePiece != wasPiece && started && !paused) ShowIslands(b, !b.IslandsHiddenForFullscreen);

            b.HideDist = h + margin + b.Left.S(14);
            b.BarThick = h + margin;
            b.BarReserved = !Config.AutoHide;

            IntPtr[] hosts = { b.Host, b.Host2 };
            for (int i = 0; i < hosts.Length; i++)
            {
                bool want = b.BarReserved && i < b.StripCount && ShellTaskbar.IsHiddenByUs();
                if (want == ShellTaskbar.BarRegistered(hosts[i])) continue;
                if (want) ShellTaskbar.RegisterBar(hosts[i], WM_APP_APPBAR);
                else ShellTaskbar.UnregisterBar(hosts[i]);
                b.LastBarHeight = -1;
            }

            int stripSig = b.StripCount | ((int)b.Strips[0] << 4) | ((int)b.Strips[1] << 8);
            if (b.BarReserved && (b.BarThick != b.LastBarHeight || stripSig != b.LastStripSig
                || rc.Left != b.LastBarRect.Left || rc.Top != b.LastBarRect.Top
                || rc.Bottom != b.LastBarRect.Bottom || rc.Right != b.LastBarRect.Right))
            {
                b.LastBarHeight = b.BarThick;
                b.LastStripSig = stripSig;
                b.LastBarRect = rc;
                for (int i = 0; i < b.StripCount; i++)
                    ShellTaskbar.SetBarPos(hosts[i], rc, b.BarThick, b.Strips[i]);
            }
            UpdateFill(b);
        }

        void ApplyLayoutLegacy()
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
            bool wasPiece = OnePiece;
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
            if (OnePiece != wasPiece) ApplyTints();
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
            if (OnePiece != wasPiece && started && !paused) ShowIslands(!islandsHiddenForFullscreen);

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
            foreach (var bar in bars) UpdateFill(bar);
        }

        void UpdateFill(Bar bar)
        {
            if (bar.Fills[0] == null || bar.Left == null) return;
            // (La barra completa sin separación con los bordes tapa toda la franja: no queda hueco que rellenar.)
            bool on = !paused && started && bar.BarReserved && bar.LastBarHeight >= 0
                && !bar.IslandsHiddenForFullscreen && !(bar.Full && bar.LayMargin == 0);
            IntPtr src = on ? StripFill.TopMaximized(bar.Monitor) : IntPtr.Zero;
            bool any = false;
            for (int i = 0; i < bar.Fills.Length; i++)
            {
                RECT strip = bar.LastBarRect;
                bool used = on && i < bar.StripCount;
                if (used)
                    switch (bar.Strips[i])
                    {
                        case Edge.Top: strip.Bottom = strip.Top + bar.LastBarHeight; break;
                        case Edge.Left: strip.Right = strip.Left + bar.LastBarHeight; break;
                        case Edge.Right: strip.Left = strip.Right - bar.LastBarHeight; break;
                        default: strip.Top = strip.Bottom - bar.LastBarHeight; break;
                    }
                bar.Fills[i].Update(strip, bar.Strips[i], used ? src : IntPtr.Zero, bar.Left.S(6));
                any |= bar.Fills[i].Visible;
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
            // Apps que se ha pedido no ver en la barra (las ancladas no se esconden: para eso se desanclan).
            if (Config.HiddenApps.Length > 0) list.RemoveAll(it => !it.Pinned && Config.IsHiddenApp(Config.AppId(it.Aumid, it.ExePath)));
            list = GroupItems(list);
            list = Uncombine(list, ref sig);
            flyout.UpdateItems(list);
            if (sig == lastSig) return;
            lastSig = sig;
            foreach (var bar in bars) bar.Left.SetItems(list);

            // "Botones más pequeños: cuando la barra esté llena".
            bool barFull = Config.SmallButtons == 2 && left.FullAtNormalSize;
            if (barFull != smallWhenFull)
            {
                smallWhenFull = barFull;
                lastSig = "";
                ApplyLayout();
            }
        }

        // Las apps ancladas que forman un grupo pasan a ocupar un solo botón, en el sitio de la primera de ellas. El
        // botón reúne sus ventanas: cuenta como abierto o activo si lo está alguna de sus apps.
        List<AppItem> GroupItems(List<AppItem> list)
        {
            if (pins.Groups.Count == 0) return list;
            var result = new List<AppItem>();
            var made = new Dictionary<List<string>, AppItem>();
            foreach (var it in list)
            {
                var g = it.Pinned ? pins.GroupOf(it.LaunchPath) : null;
                if (g == null) { result.Add(it); continue; }
                AppItem group;
                if (!made.TryGetValue(g, out group))
                {
                    group = new AppItem { Key = "group:" + g[0], Id = "group:" + g[0], Pinned = true, Members = new List<AppItem>(), Group = g };
                    made[g] = group;
                    result.Add(group);
                }
                group.Members.Add(it);
            }
            foreach (var group in made.Values)
            {
                var order = group.Group;
                Func<AppItem, int> at = m => order.FindIndex(p => string.Equals(p, m.LaunchPath, StringComparison.OrdinalIgnoreCase));
                group.Members.Sort((a, b) => at(a).CompareTo(at(b)));   // en el orden en que se añadieron
                var names = new List<string>();
                foreach (var m in group.Members)
                {
                    names.Add(m.Name ?? "");
                    group.Windows.AddRange(m.Windows);
                    group.Active |= m.Active;
                    group.Flashing |= m.Flashing;
                }
                group.Name = string.Join(", ", names.ToArray());
                if (group.Active) group.Flashing = false;
            }
            return result;
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
                // Un grupo sigue siendo un solo botón, tenga las ventanas que tenga.
                if (it.Windows.Count == 0 || it.Members != null) { result.Add(it); plain++; continue; }
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

        // Clic central en una app: abre otra ventana suya o, si así se ha elegido, cierra la que tiene más a mano.
        void ItemMiddle(AppItem it)
        {
            if (Config.MiddleClick == 1)
            {
                if (it.Running) Native.PostMessageW(it.Windows[0], Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (!string.IsNullOrEmpty(it.LaunchPath)) Native.Launch(it.LaunchPath, null);
        }

        // Rueda sobre una app abierta: pasa a su ventana siguiente o a la anterior (si se ha elegido en Comportamiento).
        void ItemWheel(AppItem it, bool up)
        {
            if (Config.WheelAction != 1 || !it.Running) return;
            flyout.HideNow();
            IntPtr fg = Native.GetForegroundWindow();
            IntPtr fgRoot = fg == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(fg, Native.GA_ROOTOWNER);
            var stable = new List<IntPtr>(it.Windows);
            stable.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));
            int n = stable.Count, cur = stable.FindIndex(w => w == fg || w == fgRoot);
            if (cur >= 0 && n == 1) return;   // su única ventana ya está delante
            Activate(cur < 0 ? it.Windows[0] : stable[(cur + (up ? n - 1 : 1)) % n]);
        }

        // Deja de mostrar una app en la barra (se recupera desde la página Aplicaciones).
        void HideApp(AppItem it)
        {
            string id = Config.AppId(it.Aumid, it.ExePath);
            if (id.Length == 0 || Config.IsHiddenApp(id)) return;
            var list = new List<string>(Config.HiddenApps);
            list.Add(id);
            Config.HiddenApps = list.ToArray();
            Config.Save("hideapps", string.Join(";", Config.HiddenApps));
            lastSig = "";
            RefreshApps();
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
                // Un grupo se mueve entero: sus apps quedan seguidas en el sitio del grupo.
                if (it.Members != null) { foreach (var m in it.Members) if (!pinnedPaths.Contains(m.LaunchPath)) pinnedPaths.Add(m.LaunchPath); }
                else if (it.Pinned) { if (!pinnedPaths.Contains(it.LaunchPath)) pinnedPaths.Add(it.LaunchPath); }
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
                string target, name, aumid, args;
                Shortcut.Resolve(f, out target, out name, out aumid, out args);
                if (pins.Contains((target ?? "").ToLowerInvariant(), (aumid ?? "").ToLowerInvariant(), Shortcut.AppIdentity(target, args))) continue;
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
            openGroup = null;
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

        void ShowTaskbarMenu(Bar bar, int x, int y)
        {
            ShowTaskbarMenu(x, y);
        }

        static string Short(string text) { return text.Length > 34 ? text.Substring(0, 33) + "…" : text; }

        // Lleva una app al grupo en el que está otra (member = null: empieza uno nuevo con ella). Un grupo solo lleva
        // apps ancladas, así que si no lo estaba se ancla ahora.
        void Regroup(AppItem it, string member)
        {
            string path = it.LaunchPath;
            if (!it.Pinned)
            {
                path = it.Packaged ? it.LaunchPath : it.ExePath;
                if (string.IsNullOrEmpty(path)) return;
                pins.Add(path, it.Packaged ? it.Name : null);
            }
            if (member == null) pins.CreateGroup(path); else pins.AddToGroup(member, path);
            lastSig = "";
            RefreshApps();
        }

        // Clic en un grupo: sus apps, para abrir o traer al frente la que se elija. Otro clic en el grupo lo cierra.
        void GroupOpen(AppItem group, int x, int y)
        {
            flyout.HideNow();
            if (pressedGroup == group.Id) { pressedGroup = null; return; }
            var l = new List<MenuDef>();
            foreach (var m in group.Members)
            {
                var member = m;
                string name = string.IsNullOrEmpty(m.Name) ? L.T("Aplicación") : m.Name;
                l.Add(MenuDef.Item(name, () => ItemClick(member), member.Image));
            }
            OpenMenu(l, x, y);
            openGroup = group.Id;
        }

        void GroupOpen(Bar bar, AppItem group, int x, int y)
        {
            GroupOpen(group, x, y);
        }

        // Clic derecho en un grupo: sacar una de sus apps (vuelve a tener su botón), deshacerlo o cerrar sus ventanas.
        void GroupContext(AppItem group, int x, int y)
        {
            var l = new List<MenuDef>();
            l.Add(MenuDef.Header(L.T("Grupo")));
            foreach (var m in group.Members)
            {
                var member = m;
                l.Add(MenuDef.Item(L.T("Sacar del grupo: ") + Short(m.Name ?? ""), () => { pins.RemoveFromGroup(member.LaunchPath); lastSig = ""; RefreshApps(); }, member.Image));
            }
            l.Add(MenuDef.Separator());
            l.Add(MenuDef.Item(L.T("Deshacer el grupo"), () => { pins.DissolveGroup(group.Members[0].LaunchPath); lastSig = ""; RefreshApps(); }, ""));
            if (group.Running)
                l.Add(MenuDef.Item(L.T("Cerrar todas las ventanas"), () =>
                {
                    foreach (var w in group.Windows.ToArray()) Native.PostMessageW(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }, ""));
            OpenMenu(l, x, y);
        }

        // Botón de una app: lista de salto de Windows (tareas y categorías), nombre de la app, anclar/desanclar y cerrar.
        void ItemContext(AppItem it, int x, int y)
        {
            if (it.Members != null) { GroupContext(it, x, y); return; }
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
            // Grupos: la app pasa a compartir botón con otras (hasta PinStore.MaxGroupSize por grupo).
            if (it.Pinned || it.Packaged || !string.IsNullOrEmpty(it.ExePath))
            {
                l.Add(MenuDef.Item(L.T("Crear grupo"), () => Regroup(it, null), ""));
                foreach (var g in pins.Groups)
                {
                    if (g.Count >= PinStore.MaxGroupSize) continue;
                    string member = g[0];
                    l.Add(MenuDef.Item(L.T("Añadir al grupo: ") + Short(pins.GroupName(g)), () => Regroup(it, member), ""));
                }
            }
            if (it.Running && !it.Pinned && Config.AppId(it.Aumid, it.ExePath).Length > 0)
                l.Add(MenuDef.Item(L.T("Ocultar de la barra de tareas"), () => HideApp(it), ""));
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

        void ItemContext(Bar bar, AppItem it, int x, int y)
        {
            ItemContext(it, x, y);
        }

        void ClockContext(int x, int y)
        {
            var l = new List<MenuDef>();
            l.Add(MenuDef.Item(L.T("Ajustar fecha y hora"), () => Native.Launch("ms-settings:dateandtime", null), (string)null));
            OpenMenu(l, x, y);
        }

        void ClockContext(Bar bar, int x, int y)
        {
            if (bar.Primary) ClockContext(x, y);
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

        void SysContext(Bar bar, int x, int y)
        {
            if (bar.Primary) SysContext(x, y);
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
