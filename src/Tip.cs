using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    // Tooltip nativo con una "herramienta" por celda; el texto se pide bajo demanda (TTN_GETDISPINFO).
    sealed class Tip
    {
        const uint TTM_SETDELAYTIME = 0x0403, TTM_ADDTOOLW = 0x0432, TTM_DELTOOLW = 0x0433, TTM_SETMAXTIPWIDTH = 0x0418;
        const uint TTF_TRANSPARENT = 0x100;
        const int TTN_GETDISPINFOW = -530;
        static readonly IntPtr TextCallback = new IntPtr(-1);

        readonly IntPtr owner;
        readonly Func<int, string> textFor;
        IntPtr hwnd;
        int toolCount;
        IntPtr textBuf = IntPtr.Zero;

        public Tip(IntPtr owner, Func<int, string> textFor)
        {
            this.owner = owner;
            this.textFor = textFor;
        }

        public static void InitControls()
        {
            var icc = new INITCOMMONCONTROLSEX();
            icc.dwSize = Marshal.SizeOf(typeof(INITCOMMONCONTROLSEX));
            icc.dwICC = 0x0008; // ICC_BAR_CLASSES (incluye tooltips)
            Native.InitCommonControlsEx(ref icc);
        }

        void EnsureWindow()
        {
            if (hwnd != IntPtr.Zero) return;
            hwnd = Native.CreateWindowExW(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW, "tooltips_class32", null,
                Native.WS_POPUP | 0x1 /* TTS_ALWAYSTIP */ | 0x2 /* TTS_NOPREFIX */, 0, 0, 0, 0, owner, IntPtr.Zero,
                Native.GetModuleHandleW(null), IntPtr.Zero);
            Native.SendMessageW(hwnd, TTM_SETDELAYTIME, new IntPtr(3), new IntPtr(350));
            Native.SendMessageW(hwnd, TTM_SETDELAYTIME, new IntPtr(1), new IntPtr(80));
            Native.SendMessageW(hwnd, TTM_SETMAXTIPWIDTH, IntPtr.Zero, new IntPtr(420));
        }

        public void Rebuild(List<RECT> rects)
        {
            EnsureWindow();
            if (hwnd == IntPtr.Zero) return;
            for (int i = 0; i < toolCount; i++) Send(TTM_DELTOOLW, i + 1, default(RECT));
            toolCount = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                Send(TTM_ADDTOOLW, i + 1, rects[i]);
                toolCount++;
            }
        }

        void Send(uint msg, int id, RECT rect)
        {
            var ti = new TOOLINFO();
            ti.cbSize = (uint)Marshal.SizeOf(typeof(TOOLINFO));
            ti.uFlags = TTF_TRANSPARENT | 0x10 /* TTF_SUBCLASS */;
            ti.hwnd = owner;
            ti.uId = new UIntPtr((uint)id);
            ti.rect = rect;
            ti.lpszText = TextCallback;
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TOOLINFO)));
            try
            {
                Marshal.StructureToPtr(ti, p, false);
                Native.SendMessageW(hwnd, msg, IntPtr.Zero, p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Llamar desde WM_NOTIFY. Devuelve true si el mensaje era de este tooltip.
        public bool HandleNotify(IntPtr lParam)
        {
            var hdr = (NMHDR)Marshal.PtrToStructure(lParam, typeof(NMHDR));
            if (hdr.hwndFrom != hwnd || hdr.code != TTN_GETDISPINFOW) return false;
            string text = textFor((int)(ulong)hdr.idFrom - 1) ?? "";
            if (textBuf != IntPtr.Zero) Marshal.FreeHGlobal(textBuf);
            textBuf = Marshal.StringToHGlobalUni(text);
            Marshal.WriteIntPtr(lParam, Marshal.SizeOf(typeof(NMHDR)), textBuf);
            return true;
        }

        public void Hide()
        {
            if (hwnd != IntPtr.Zero) Native.SendMessageW(hwnd, 0x041C /* TTM_POP */, IntPtr.Zero, IntPtr.Zero);
        }

        public void Destroy()
        {
            if (hwnd != IntPtr.Zero) { Native.DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
            if (textBuf != IntPtr.Zero) { Marshal.FreeHGlobal(textBuf); textBuf = IntPtr.Zero; }
        }
    }
}
