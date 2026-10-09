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
            WM_APP_QUIT = Native.WM_APP + 4, WM_APP_VOLUME = Native.WM_APP + 6, WM_APP_NET = Native.WM_APP + 7;

        const uint T_REFRESH = 1, T_CLOCK = 2, T_SETUP = 3, T_NET = 4, T_TRIM = 5, T_ENTER = 6, T_FLY = 7, T_TOOLS = 8, T_HIDE = 9;

        static readonly WndProcDelegate HostProcDelegate = StaticHostProc;
        static App instance;

        IntPtr host;
        enum FlyKind { Start = 0, Shell = 1, Overflow = 2 }
        readonly bool[] flyOpen = new bool[3];  // estado observado en el último refresco
        int shellOwner;                         // botón que abrió el panel del sistema: 1 = red/volumen, 2 = reloj
        bool toggleClose;                       // el clic en curso debe cerrar el panel en lugar de abrirlo
        LeftIsland left;
        ThumbFlyout flyout;
        TaskbarMenu menu;
        int flyTicks;
        RightIsland right;
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
        Edge barEdge = Edge.Bottom, lastBarEdge = Edge.Bottom;   // borde elegido en Windows y el último aplicado
        int lastEdgeCheck, lastPosChanged;
        bool smallWhenFull;                 // "botones pequeños cuando la barra esté llena" y ahora lo está
        bool barRevealed = true;            // ocultación automática: la barra está a la vista
        // true mientras la ocultación automática de la barra original la hemos puesto nosotros (para que no reserve sitio);
        // false cuando su estado es el que el usuario ha elegido en Configuración y simplemente se sigue.
        bool autoHideForced;
        bool userAutoHide;                  // lo que el usuario tiene elegido en "Ocultar automáticamente" (solo vale con la barra abajo)
        int arrivedBottomTick;              // momento en que la barra de Windows volvió al borde inferior
        int lastWantedTick, hideDist;
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

            left = new LeftIsland();
            left.Init();
            right = new RightIsland();
            right.Init();
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

        // Ocultación automática: arranca o detiene la vigilancia del cursor.
        void UpdateAutoHide()
        {
            if (Config.AutoHide && !paused)
            {
                lastWantedTick = Environment.TickCount;
                Native.SetTimer(host, new UIntPtr(T_HIDE), 100, IntPtr.Zero);
            }
            else
            {
                Native.KillTimer(host, new UIntPtr(T_HIDE));
                barRevealed = true;
                left.SetAutoHidden(false, hideDist);
                right.SetAutoHidden(false, hideDist);
            }
        }

        // La barra aparece al llevar el cursor a su borde de pantalla (o al abrir Inicio, un panel o un menú)
        // y se esconde poco después de que el cursor la abandone, como la barra de Windows.
        void AutoHideTick()
        {
            if (paused || !Config.AutoHide) { UpdateAutoHide(); return; }
            POINT p;
            Native.GetCursorPos(out p);
            RECT rc = monRect;
            bool inside = p.X >= rc.Left && p.X < rc.Right && p.Y >= rc.Top && p.Y < rc.Bottom;
            int near = left.S(2), zone = hideDist;
            int d = barEdge == Edge.Bottom ? rc.Bottom - 1 - p.Y : barEdge == Edge.Top ? p.Y - rc.Top
                  : barEdge == Edge.Left ? p.X - rc.Left : rc.Right - 1 - p.X;
            bool atEdge = inside && d <= near;
            bool overBar = inside && d <= zone;
            bool busy = menu.Visible || flyout.Visible || IsFlyoutOpen(FlyKind.Start) || IsFlyoutOpen(FlyKind.Shell) || IsFlyoutOpen(FlyKind.Overflow);
            int now = Environment.TickCount;
            if (atEdge || busy || (barRevealed && overBar))
            {
                lastWantedTick = now;
                if (!barRevealed)
                {
                    barRevealed = true;
                    left.SetAutoHidden(false, hideDist);
                    right.SetAutoHidden(false, hideDist);
                    left.RaiseTopmost();
                    right.RaiseTopmost();
                }
            }
            else if (barRevealed && (now - lastWantedTick > 450 || now < lastWantedTick))
            {
                barRevealed = false;
                flyout.HideNow();
                left.SetAutoHidden(true, hideDist);
                right.SetAutoHidden(true, hideDist);
            }
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
            ApplyLayout();
            lastSig = "";
            RefreshApps();
            left.Show(!islandsHiddenForFullscreen);
            right.Show(!islandsHiddenForFullscreen);
            UpdateTrayTip();
            UpdateAutoHide();
            Native.SetTimer(host, new UIntPtr(T_TRIM), 8000, IntPtr.Zero);
        }

        bool SetupShell()
        {
            if (!ShellTaskbar.Hide()) return false;
            barEdge = Config.ReadTaskbarEdge();
            // Lo que el usuario tenía elegido en Windows antes de sustituir la barra. Si no la tenía en ocultación
            // automática, Hide() la ha puesto en ese modo solo para que no reserve su franja.
            userAutoHide = ShellTaskbar.OriginalAutoHide();
            Config.AutoHide = userAutoHide && barEdge == Edge.Bottom;
            autoHideForced = !userAutoHide;
            ShellTaskbar.RegisterBar(host, WM_APP_APPBAR);
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
            left.Show(false);
            right.Show(false);
            ShellTaskbar.UnregisterBar(host);
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
            right.Destroy();
            left.Destroy();
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
            left.Dpi = right.Dpi = dpi;
            int h = left.S(Config.Height);
            // Botones más pequeños (siempre, o solo cuando la barra está llena): iconos a 2/3 de su tamaño, como en Windows.
            bool small = Config.SmallButtons == 0 || (Config.SmallButtons == 2 && smallWhenFull);
            left.Small = small;
            if (left.Icons.SetPx(left.S(small ? Math.Max(16, Config.IconSize * 2 / 3) : Config.IconSize))) lastSig = "";
            int margin = left.S(Config.Margin);
            Edge edge = barEdge;
            bool vert = edge == Edge.Left || edge == Edge.Right;
            // Coordenada del lado de las islas pegado al borde de pantalla, y la del lado que mira al centro.
            int cross, anchor;
            switch (edge)
            {
                case Edge.Top: cross = rc.Top + margin; anchor = cross + h; break;
                case Edge.Left: cross = rc.Left + margin; anchor = cross + h; break;
                case Edge.Right: cross = rc.Right - margin; anchor = cross - h; break;
                default: cross = rc.Bottom - margin; anchor = cross - h; break;
            }
            flyout.Dpi = dpi;
            menu.Dpi = dpi;
            menu.Monitor = rc;
            flyout.Monitor = rc;
            menu.Edge = flyout.Edge = edge;
            flyout.Anchor = anchor;

            // La isla izquierda arranca al principio de la barra (izquierda/arriba) y la derecha termina al final (derecha/abajo).
            right.SetPlacement(edge, cross, vert ? rc.Bottom - margin : rc.Right - margin);
            right.Layout();
            left.MaxWidth = (vert ? rc.Height : rc.Width) - right.VisW - margin * 3 - left.S(24);
            // Alineación de la barra: al centro de la pantalla (sin pisar la isla derecha) o al inicio.
            int a0 = vert ? rc.Top : rc.Left, a1 = vert ? rc.Bottom : rc.Right;
            left.Centered = Config.Center;
            left.CenterPos = (a0 + a1) / 2;
            left.CenterMin = a0 + margin;
            left.CenterMax = a1 - margin - right.VisW - left.S(12);
            left.SetPlacement(edge, cross, vert ? rc.Top + margin : rc.Left + margin);
            left.Layout();

            monRect = rc;
            hideDist = h + margin + left.S(14);
            int reserved = h + margin;
            if (Config.AutoHide)
            {
                // Con ocultación automática la barra no reserva sitio: las ventanas ocupan toda la pantalla.
                if (ShellTaskbar.BarRegistered) ShellTaskbar.UnregisterBar(host);
                lastBarHeight = -1;
            }
            else if (!ShellTaskbar.BarRegistered && ShellTaskbar.IsHiddenByUs())
            {
                ShellTaskbar.RegisterBar(host, WM_APP_APPBAR);
                lastBarHeight = -1;
            }
            if (!Config.AutoHide && (reserved != lastBarHeight || edge != lastBarEdge || rc.Left != lastBarRect.Left || rc.Top != lastBarRect.Top
                || rc.Bottom != lastBarRect.Bottom || rc.Right != lastBarRect.Right))
            {
                lastBarHeight = reserved;
                lastBarEdge = edge;
                lastBarRect = rc;
                ShellTaskbar.SetBarPos(host, rc, reserved, edge);
            }
            // Si cambió el tamaño de icono se vació la caché: reconstruye las imágenes antes de volver a pintar.
            if (lastSig.Length == 0 && started && !paused) RefreshApps();
        }

        // "Ocultar automáticamente la barra de tareas": Configuración cambia el estado real de la barra original, y eso
        // es lo que se sigue. Hay que separar lo que elige el usuario de lo que ponemos nosotros (al sustituir la barra se
        // deja en ocultación automática para que no reserve su franja) y de lo que hace Windows por su cuenta: fuera del
        // borde inferior desactiva la ocultación, y al volver abajo repone la que había al salir.
        // Devuelve true si hay que recolocar las islas.
        bool FollowAutoHide(Edge edge, bool edgeChanged)
        {
            if (!ShellTaskbar.IsHiddenByUs()) return false;
            bool? liveState = ShellTaskbar.LiveAutoHide();
            if (liveState == null) return false;
            bool live = liveState.Value, relayout = false, wasForced = autoHideForced;
            if (ShellTaskbar.IsTrayVisible()) { ShellTaskbar.HideWindow(); relayout = true; }

            int now = Environment.TickCount;
            if (edge != Edge.Bottom)
            {
                autoHideForced = false;     // aquí Windows no admite la ocultación automática: nada que forzar ni que seguir
            }
            else
            {
                if (edgeChanged) arrivedBottomTick = now;
                bool arriving = edgeChanged || (now - arrivedBottomTick < 2500 && now >= arrivedBottomTick);
                if (arriving)
                {
                    // Recién llegada abajo: si Windows la deja en ocultación y el usuario no la quería, es la nuestra.
                    autoHideForced = live && !userAutoHide;
                }
                else if (autoHideForced)
                {
                    // El usuario la ha desmarcado en Configuración (donde se veía marcada): sigue sin quererla.
                    if (!live) autoHideForced = false;
                }
                else if (live != userAutoHide)
                {
                    // El usuario la ha cambiado en Configuración.
                    userAutoHide = live;
                    ShellTaskbar.SetOriginalAutoHide(live);
                }
            }

            bool effective = edge == Edge.Bottom && userAutoHide;
            if (effective != Config.AutoHide) { Config.AutoHide = effective; relayout = true; }
            return relayout || wasForced != autoHideForced;
        }

        // Si el usuario mueve la barra de Windows a otro borde o cambia su ocultación automática, las islas lo siguen.
        void CheckTaskbarEdge()
        {
            int now = Environment.TickCount;
            if (now - lastEdgeCheck < 500 && now >= lastEdgeCheck) return;
            lastEdgeCheck = now;
            Edge e = Config.ReadTaskbarEdge();
            bool edgeChanged = e != barEdge;
            bool hideChanged = FollowAutoHide(e, edgeChanged);
            if (!edgeChanged && !hideChanged) return;
            barEdge = e;
            if (edgeChanged) { flyout.HideNow(); menu.Close(); lastSig = ""; }
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
                left.Show(!full);
                right.Show(!full);
            }
            else if (!full && !OverflowFlyoutOpen() && !menu.Visible)
            {
                left.RaiseTopmost();
                right.RaiseTopmost();
            }

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
            // Con ocultación automática una ventana maximizada ocupa toda la pantalla sin ser de pantalla completa.
            if (Config.AutoHide && Native.IsZoomed(fg)) return false;
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
            menu.Show(defs, x, y, flyout.Anchor);
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
            l.Add(MenuDef.Item("Administrador de tareas", () => Native.Launch("taskmgr.exe", null),
                left.Icons.Get(Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\Taskmgr.exe")));
            l.Add(MenuDef.Item("Configuración de la barra de tareas", () => Native.Launch("ms-settings:taskbar", null), "\uE713"));
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
                l.Add(MenuDef.Header(cat.Tasks ? "Tareas" : cat.Name));
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

            string name = string.IsNullOrEmpty(it.Name) ? "Aplicación" : it.Name;
            if (!string.IsNullOrEmpty(it.LaunchPath)) l.Add(MenuDef.Item(name, () => Native.Launch(it.LaunchPath, null), it.Image));
            if (it.Pinned)
                l.Add(MenuDef.Item("Desanclar de la barra de tareas", () => { pins.RemoveByPath(it.LaunchPath); lastSig = ""; RefreshApps(); }, "\uE77A"));
            else if (it.Aumid.Length > 0 || !string.IsNullOrEmpty(it.ExePath))
                l.Add(MenuDef.Item("Anclar a la barra de tareas", () =>
                {
                    if (it.Packaged) pins.Add(it.LaunchPath, it.Name);
                    else pins.Add(it.ExePath, null);
                    lastSig = ""; RefreshApps();
                }, "\uE718"));
            if (it.Running && Config.EndTask)
                l.Add(MenuDef.Item("Finalizar tarea", () =>
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
                l.Add(MenuDef.Item(it.Windows.Count > 1 ? "Cerrar todas las ventanas" : "Cerrar ventana", () =>
                {
                    foreach (var w in it.Windows.ToArray()) Native.PostMessageW(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }, "\uE711"));
            OpenMenu(l, x, y);
        }

        void ClockContext(int x, int y)
        {
            var l = new List<MenuDef>();
            l.Add(MenuDef.Item("Ajustar fecha y hora", () => Native.Launch("ms-settings:dateandtime", null), (string)null));
            OpenMenu(l, x, y);
        }

        // Iconos de sistema: menú del icono bajo el cursor (red, volumen o batería).
        void SysContext(int x, int y)
        {
            var l = new List<MenuDef>();
            int slot = right.LastSysSlot;
            if (slot == 0) l.Add(MenuDef.Item("Configuración de red e Internet", () => Native.Launch("ms-settings:network", null), (string)null));
            else if (slot == 1)
            {
                l.Add(MenuDef.Item("Abrir el mezclador de volumen", () => Native.Launch("sndvol.exe", null), (string)null));
                l.Add(MenuDef.Item("Configuración de sonido", () => Native.Launch("ms-settings:sound", null), (string)null));
            }
            else l.Add(MenuDef.Item("Configuración de energía y batería", () => Native.Launch("ms-settings:powersleep", null), (string)null));
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
            l.Add(new MenuEntry(14, "Abrir NeuTaskBar"));
            l.Add(new MenuEntry(10, paused ? "Reanudar" : "Pausar (mostrar barra de Windows)"));
            l.Add(new MenuEntry(11, "Iniciar con Windows", Config.GetAutoStart() ? Native.MF_CHECKED : 0u));
            l.Add(MenuEntry.Separator());
            l.Add(new MenuEntry(13, "Salir"));
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
            d.szTip = paused ? "NeuTaskBar (en pausa)" : "NeuTaskBar";
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
