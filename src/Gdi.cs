using System;
using System.Collections.Generic;
using System.Text;

namespace NeuTaskBar
{
    // Utilidades GDI con caché de fuentes y pinceles. Todo se dibuja en negro = transparente (composición aditiva sobre el acrílico).
    static class Gdi
    {
        public const uint DT_CENTER = 1, DT_RIGHT = 2, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800,
            DT_NOCLIP = 0x100, DT_END_ELLIPSIS = 0x8000;

        static readonly Dictionary<string, IntPtr> fonts = new Dictionary<string, IntPtr>();
        static readonly Dictionary<int, IntPtr> brushes = new Dictionary<int, IntPtr>();
        static string iconFace;

        public static IntPtr Brush(int rgb)
        {
            IntPtr b;
            if (!brushes.TryGetValue(rgb, out b))
            {
                b = Native.CreateSolidBrush(rgb);
                brushes[rgb] = b;
            }
            return b;
        }

        public static IntPtr Font(string face, int px, int weight)
        {
            string key = face + "|" + px + "|" + weight;
            IntPtr f;
            if (!fonts.TryGetValue(key, out f))
            {
                // ANTIALIASED_QUALITY: evita franjas de ClearType sobre la superficie translúcida.
                f = Native.CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, face);
                fonts[key] = f;
            }
            return f;
        }

        public static void ClearFonts()
        {
            foreach (var f in fonts.Values) Native.DeleteObject(f);
            fonts.Clear();
        }

        // Fuente de iconos del sistema: Segoe Fluent Icons (Win11) o Segoe MDL2 Assets (Win10).
        public static string IconFace(IntPtr dc)
        {
            if (iconFace != null) return iconFace;
            iconFace = "Segoe MDL2 Assets";
            IntPtr f = Native.CreateFontW(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe Fluent Icons");
            IntPtr old = Native.SelectObject(dc, f);
            var sb = new StringBuilder(64);
            Native.GetTextFaceW(dc, sb.Capacity, sb);
            Native.SelectObject(dc, old);
            Native.DeleteObject(f);
            if (sb.ToString().Equals("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase)) iconFace = "Segoe Fluent Icons";
            return iconFace;
        }

        public static void Text(IntPtr dc, string s, RECT r, IntPtr font, int rgb, uint flags)
        {
            IntPtr old = Native.SelectObject(dc, font);
            Native.SetBkMode(dc, 1);
            Native.SetTextColor(dc, rgb);
            Native.DrawTextW(dc, s, -1, ref r, flags | DT_NOPREFIX | DT_SINGLELINE);
            Native.SelectObject(dc, old);
        }

        public static int TextWidth(IntPtr dc, string s, IntPtr font)
        {
            IntPtr old = Native.SelectObject(dc, font);
            SIZE sz;
            Native.GetTextExtentPoint32W(dc, s, s.Length, out sz);
            Native.SelectObject(dc, old);
            return sz.cx;
        }

        public static void FillRound(IntPtr dc, RECT r, int radius, int rgb)
        {
            IntPtr oldBrush = Native.SelectObject(dc, Brush(rgb));
            IntPtr oldPen = Native.SelectObject(dc, Native.GetStockObject(8)); // NULL_PEN
            Native.RoundRect(dc, r.Left, r.Top, r.Right + 1, r.Bottom + 1, radius * 2, radius * 2);
            Native.SelectObject(dc, oldPen);
            Native.SelectObject(dc, oldBrush);
        }

        public static void FillRectColor(IntPtr dc, RECT r, int rgb)
        {
            Native.FillRect(dc, ref r, Brush(rgb));
        }
    }
}
