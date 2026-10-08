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

        const uint T_REFRESH = 1, T_CLOCK = 2, T_SETUP = 3, T_NET = 4, T_TRIM = 5, T_ENTER = 6, T_FLY = 7, T_TOOLS = 8;

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
            Config.ReadWindowsTaskbarButtons();
            var ids = new List<string>();
            if (Config.ShowSearch) ids.Add("search");
            if (Config.ShowTaskView) ids.Add("taskview");
            if (Config.ShowWidgets) ids.Add("widgets");
            left.SetTools(ids.ToArray());
        }

        void Resume()
        {
            paused = false;
            started = true;
            Native.SetTimer(host, new UIntPtr(T_CLOCK), 1000, IntPtr.Zero);
            Native.SetTimer(host, new UIntPtr(T_TOOLS), 1500, IntPtr.Zero);

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
            Native.SetTimer(host, new UIntPtr(T_TRIM), 8000, IntPtr.Zero);
        }

        bool SetupShell()
        {
            if (!ShellTaskbar.Hide()) return false;
            ShellTaskbar.RegisterBar(host, WM_APP_APPBAR);
            lastBarHeight = -1;
            ApplyLayout();
            return true;
        }

        void Pause()
        {
            paused = true;
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
                    if (w.ToInt64() == Native.ABN_POSCHANGED && !paused) ApplyLayout();
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
                    if (Config.ReadWindowsTaskbarButtons()) { ApplyTools(); lastBarHeight = -1; ApplyLayout(); }
                    break;
                case T_CLOCK:
                    if (paused) { Native.KillTimer(host, new UIntPtr(T_CLOCK)); break; }
                    right.UpdateClock();
                    right.UpdateBattery(BatteryState.Read());
                    var now = DateTime.Now;
                    uint next = (uint)((60 - now.Second) * 1000 - now.Millisecond + 150);
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
                if (kind == FlyKind.Overflow) Native.PressKeys(0x1B /* ESC */);
                else if (kind == FlyKind.Start) Native.PressKeys(Native.VK_LWIN);
                else if (shellOwner == 1) Native.PressKeys(Native.VK_LWIN, 0x41);
                else if (Island.IsWin11) Native.PressKeys(Native.VK_LWIN, 0x4E);
                else Native.PressKeys(Native.VK_LWIN, 0x12, 0x44);
            }
            return true;
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
            int limit = isl.Top - right.S(8);
            if (r.Bottom > limit + 1)
                Native.SetWindowPos(o, IntPtr.Zero, r.Left, r.Top - (r.Bottom - limit), 0, 0,
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
        }

        void RemoveHooks()
        {
            foreach (var h in hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            hooks.Clear();
            if (shellHookRegistered) { Native.DeregisterShellHookWindow(host); shellHookRegistered = false; }
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (evt >= Native.EVENT_OBJECT_CREATE && evt <= Native.EVENT_OBJECT_HIDE)
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
            if (left.Icons.SetPx(left.S(Config.IconSize))) lastSig = "";
            int margin = left.S(Config.Margin);
            int bottom = rc.Bottom - margin;
            flyout.Dpi = dpi;
            menu.Dpi = dpi;
            menu.Monitor = rc;
            flyout.Monitor = rc;
            flyout.AnchorY = bottom - h;

            right.SetAnchor(rc.Right - margin, bottom);
            right.Layout();
            left.MaxWidth = rc.Width - right.VisW - margin * 3 - left.S(24);
            left.SetAnchor(rc.Left + margin, bottom);
            left.Layout();

            int reserved = h + margin;
            if (reserved != lastBarHeight || rc.Left != lastBarRect.Left || rc.Bottom != lastBarRect.Bottom || rc.Right != lastBarRect.Right)
            {
                lastBarHeight = reserved;
                lastBarRect = rc;
                ShellTaskbar.SetBarPos(host, rc, reserved);
            }
            // Si cambió el tamaño de icono se vació la caché: reconstruye las imágenes antes de volver a pintar.
            if (lastSig.Length == 0 && started && !paused) RefreshApps();
        }

        void RefreshApps()
        {
            if (paused || !started) return;

            UpdateFlyoutStates();
            if (ShellTaskbar.IsHiddenByUs() && ShellTaskbar.IsTrayVisible()) ShellTaskbar.Hide();

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
            flyout.UpdateItems(list);
            if (sig == lastSig) return;
            lastSig = sig;
            left.SetItems(list);
        }

        bool IsForegroundFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == host || fg == left.Hwnd || fg == right.Hwnd) return false;
            if (!Native.IsWindowVisible(fg) || Native.IsIconic(fg)) return false;
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
                if (it.Pinned) pinnedPaths.Add(it.LaunchPath);
                else runKeys.Add(it.Key);
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

        void OpenMenu(List<MenuDef> defs, int centerX)
        {
            flyout.HideNow();
            menu.Show(defs, centerX, flyout.AnchorY);
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
            OpenMenu(l, x);
        }

        // Botón de una app: lista de salto de Windows (tareas y categorías), nombre de la app, anclar/desanclar y cerrar.
        void ItemContext(AppItem it, int centerX, int top)
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
            if (it.Running)
                l.Add(MenuDef.Item(it.Windows.Count > 1 ? "Cerrar todas las ventanas" : "Cerrar ventana", () =>
                {
                    foreach (var w in it.Windows.ToArray()) Native.PostMessageW(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }, "\uE711"));
            OpenMenu(l, centerX);
        }

        void ClockContext(int x, int y)
        {
            var l = new List<MenuDef>();
            l.Add(MenuDef.Item("Ajustar fecha y hora", () => Native.Launch("ms-settings:dateandtime", null), (string)null));
            OpenMenu(l, x);
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
            OpenMenu(l, x);
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
