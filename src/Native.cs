using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NeuTaskBar
{
    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Contains(int x, int y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int DataSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct TOOLINFO
    {
        public uint cbSize, uFlags;
        public IntPtr hwnd;
        public UIntPtr uId;
        public RECT rect;
        public IntPtr hinst, lpszText, lParam, lpReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NMHDR
    {
        public IntPtr hwndFrom;
        public UIntPtr idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INITCOMMONCONTROLSEX { public int dwSize, dwICC; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public string dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x8, WS_EX_ACCEPTFILES = 0x10, WS_EX_TOOLWINDOW = 0x80,
            WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_USERDATA = -21;
        public const int WS_CHILD = 0x40000000;

        public const uint WM_CREATE = 0x1, WM_DESTROY = 0x2, WM_SIZE = 0x5, WM_CLOSE = 0x10, WM_QUERYENDSESSION = 0x11,
            WM_ERASEBKGND = 0x14, WM_ENDSESSION = 0x16, WM_SETTINGCHANGE = 0x1A, WM_TIMECHANGE = 0x1E,
            WM_MOUSEACTIVATE = 0x21, WM_GETICON = 0x7F, WM_NCCREATE = 0x81, WM_NCDESTROY = 0x82, WM_NOTIFY = 0x4E,
            WM_PAINT = 0xF, WM_TIMER = 0x113, WM_DISPLAYCHANGE = 0x7E, WM_SETCURSOR = 0x20,
            WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
            WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_MOUSELEAVE = 0x2A3, WM_MOUSEHOVER = 0x2A1,
            WM_POWERBROADCAST = 0x218, WM_DPICHANGED = 0x2E0, WM_DROPFILES = 0x233, WM_CONTEXTMENU = 0x7B,
            WM_NULL = 0, WM_APP = 0x8000, WM_USER = 0x400;

        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10,
            SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_MINIMIZE = 6, SW_SHOWNA = 8, SW_RESTORE = 9;

        public const uint GW_OWNER = 4;
        public const uint GA_ROOTOWNER = 3;

        public const uint TME_HOVER = 1, TME_LEAVE = 2;

        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17,
            EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
            EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
        public const int OBJID_WINDOW = 0;

        public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        public const uint ABE_BOTTOM = 3;
        public const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
        public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

        public const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_PIDL = 0x8;
        public const int SHIL_LARGE = 0, SHIL_EXTRALARGE = 2;

        public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80, TPM_BOTTOMALIGN = 0x20,
            TPM_RIGHTALIGN = 8;
        public const uint MF_STRING = 0, MF_GRAYED = 1, MF_DISABLED = 2, MF_CHECKED = 8, MF_SEPARATOR = 0x800;

        public const uint DI_NORMAL = 3;

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33,
            DWMWA_BORDER_COLOR = 34, DWMWA_CLOAKED = 14;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x100000;

        public const uint KEYEVENTF_KEYUP = 2;
        public const byte VK_LWIN = 0x5B;

        // ---- user32 ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassW(ref WNDCLASS wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, UIntPtr id);
        [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetPropW(IntPtr h, string name, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetPropW(IntPtr h, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr RemovePropW(IntPtr h, string name);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
        [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int idx, IntPtr v);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr h, int idx, int v);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr64(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] static extern int GetClassLong32(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
        [DllImport("user32.dll")] public static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool InsertMenuItemW(IntPtr menu, uint item, bool byPos, ref MENUITEMINFO mii);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr c);
        [DllImport("user32.dll")] public static extern bool SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA d);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);

        // ---- gdi32 ----
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int idx);
        [DllImport("gdi32.dll")] public static extern bool RoundRect(IntPtr dc, int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr dc, int color);
        [DllImport("gdi32.dll")] public static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
            uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder sb);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern bool GetTextExtentPoint32W(IntPtr dc, string s, int len, out SIZE sz);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr dc, string s, int len, ref RECT r, uint fmt);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);

        // ---- shell32 / comctl32 / dwm / kernel32 ----
        [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO fi, uint size, uint flags);
        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
        public static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attrs, ref SHFILEINFO fi, uint size, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
        [DllImport("shell32.dll")] public static extern void ILFree(IntPtr pidl);
        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [DllImport("ole32.dll")] public static extern int PropVariantClear(IntPtr pv);
        [DllImport("shell32.dll")] public static extern int SHGetImageList(int list, ref Guid riid, out IntPtr ppv);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr ExtractIconW(IntPtr inst, string file, uint index);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr ShellExecuteW(IntPtr h, string op, string file, string args, string dir, int show);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint DragQueryFileW(IntPtr drop, uint idx, StringBuilder sb, uint cch);
        [DllImport("shell32.dll")] public static extern void DragFinish(IntPtr drop);
        [DllImport("comctl32.dll")] public static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);
        [DllImport("comctl32.dll")] public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageNameW(IntPtr proc, uint flags, StringBuilder sb, ref uint size);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

        // ---- helpers ----
        public static IntPtr GetWindowLongPtr(IntPtr h, int idx)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(h, idx) : new IntPtr(GetWindowLong32(h, idx));
        }

        public static IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(h, idx, v) : new IntPtr(SetWindowLong32(h, idx, v.ToInt32()));
        }

        public static IntPtr GetClassLongPtr(IntPtr h, int idx)
        {
            return IntPtr.Size == 8 ? GetClassLongPtr64(h, idx) : new IntPtr(GetClassLong32(h, idx));
        }

        // Marca de la ventana principal de la aplicacion (la ventana no se identifica por el titulo).
        public const string MainWindowProp = "NeuTaskBar.MainWindow";

        public static IntPtr FindMainWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (GetPropW(h, MainWindowProp) != IntPtr.Zero) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static int LoWord(IntPtr v) { return unchecked((short)(v.ToInt64() & 0xFFFF)); }
        public static int HiWord(IntPtr v) { return unchecked((short)((v.ToInt64() >> 16) & 0xFFFF)); }
        public static int Rgb(int r, int g, int b) { return r | (g << 8) | (b << 16); }

        public static string GetClassName(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetWindowText(IntPtr h)
        {
            int len = GetWindowTextLengthW(h);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static int GetMonitorDpi(IntPtr monitor)
        {
            try
            {
                uint x, y;
                if (GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0) return (int)x;
            }
            catch { }
            return 96;
        }

        public static void PressKeys(params byte[] keys)
        {
            foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
            for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void Launch(string file, string args)
        {
            ShellExecuteW(IntPtr.Zero, "open", file, string.IsNullOrEmpty(args) ? null : args, null, SW_SHOWNORMAL);
        }
    }
}
