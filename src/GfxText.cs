using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    [StructLayout(LayoutKind.Sequential)]
    struct RectF
    {
        public float X, Y, W, H;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER32
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    // Texto con GDI+ (alfa correcto, apto para dibujar sobre superficies translúcidas).
    static partial class Gfx
    {
        [DllImport("gdiplus.dll")] static extern int GdipGetImageGraphicsContext(IntPtr img, out IntPtr g);
        [DllImport("gdiplus.dll")] static extern int GdipGraphicsClear(IntPtr g, uint argb);
        [DllImport("gdiplus.dll")] static extern int GdipSetTextRenderingHint(IntPtr g, int hint);
        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
        [DllImport("gdiplus.dll")] static extern int GdipDeleteFontFamily(IntPtr family);
        [DllImport("gdiplus.dll")] static extern int GdipCreateFont(IntPtr family, float emSize, int style, int unit, out IntPtr font);
        [DllImport("gdiplus.dll")] static extern int GdipDeleteFont(IntPtr font);
        [DllImport("gdiplus.dll")] static extern int GdipCreateStringFormat(int attrs, int lang, out IntPtr fmt);
        [DllImport("gdiplus.dll")] static extern int GdipDeleteStringFormat(IntPtr fmt);
        [DllImport("gdiplus.dll")] static extern int GdipSetStringFormatFlags(IntPtr fmt, int flags);
        [DllImport("gdiplus.dll")] static extern int GdipSetStringFormatTrimming(IntPtr fmt, int trimming);
        [DllImport("gdiplus.dll")] static extern int GdipSetStringFormatAlign(IntPtr fmt, int align);
        [DllImport("gdiplus.dll")] static extern int GdipSetStringFormatLineAlign(IntPtr fmt, int align);
        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        static extern int GdipDrawString(IntPtr g, string s, int len, IntPtr font, ref RectF layout, IntPtr fmt, IntPtr brush);
        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        static extern int GdipMeasureString(IntPtr g, string s, int len, IntPtr font, ref RectF layout, IntPtr fmt, out RectF bounds, out int fit, out int lines);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER32 bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        static readonly Dictionary<string, IntPtr> fontCache = new Dictionary<string, IntPtr>();
        static readonly Dictionary<string, IntPtr> familyCache = new Dictionary<string, IntPtr>();
        static IntPtr stringFormat;

        static IntPtr Family(string name)
        {
            IntPtr f;
            if (familyCache.TryGetValue(name, out f)) return f;
            if (GdipCreateFontFamilyFromName(name, IntPtr.Zero, out f) != 0) f = IntPtr.Zero;
            familyCache[name] = f;
            return f;
        }

        static IntPtr TextFont(string family, float px, bool bold)
        {
            string key = family + "|" + px + "|" + bold;
            IntPtr font;
            if (fontCache.TryGetValue(key, out font)) return font;
            IntPtr fam = Family(family);
            if (fam == IntPtr.Zero && family == "Segoe Fluent Icons") fam = Family("Segoe MDL2 Assets");
            if (fam == IntPtr.Zero) fam = Family("Segoe UI");
            GdipCreateFont(fam, px, bold ? 1 : 0, 2 /* UnitPixel */, out font);
            fontCache[key] = font;
            return font;
        }

        // Dibuja texto de una línea: align 0 izq., 1 centro, 2 der.; con puntos suspensivos si no cabe.
        public static void Text(IntPtr g, string s, string family, float px, bool bold, uint argb, float x, float y, float w, float h, int align)
        {
            if (string.IsNullOrEmpty(s) || (argb >> 24) == 0) return;
            IntPtr font = TextFont(family, px, bold);
            if (font == IntPtr.Zero) return;
            if (stringFormat == IntPtr.Zero)
            {
                GdipCreateStringFormat(0, 0, out stringFormat);
                GdipSetStringFormatFlags(stringFormat, 0x1000 /* NoWrap */);
                GdipSetStringFormatTrimming(stringFormat, 3 /* EllipsisCharacter */);
                GdipSetStringFormatLineAlign(stringFormat, 1 /* Center */);
            }
            GdipSetStringFormatAlign(stringFormat, align);
            GdipSetTextRenderingHint(g, 4 /* AntiAlias */);
            GdipSetSolidFillColor(brush, argb);
            var r = new RectF(x, y, w, h);
            GdipDrawString(g, s, -1, font, ref r, stringFormat, brush);
        }

        public static float TextWidth(IntPtr g, string s, string family, float px, bool bold)
        {
            IntPtr font = TextFont(family, px, bold);
            if (font == IntPtr.Zero || string.IsNullOrEmpty(s)) return 0;
            var layout = new RectF(0, 0, 10000, 1000);
            RectF b;
            int fit, lines;
            GdipMeasureString(g, s, -1, font, ref layout, IntPtr.Zero, out b, out fit, out lines);
            return b.W;
        }

        public static void Clear(IntPtr g, uint argb) { GdipGraphicsClear(g, argb); }

        static AlphaSurface measureSurface;

        // Anchura de un texto sin necesitar un contexto de dibujo.
        public static float MeasureText(string s, string family, float px, bool bold)
        {
            if (measureSurface == null)
            {
                measureSurface = new AlphaSurface();
                IntPtr dc = Native.GetDC(IntPtr.Zero);
                measureSurface.Resize(dc, 8, 8);
                Native.ReleaseDC(IntPtr.Zero, dc);
            }
            IntPtr g = measureSurface.BeginGraphics();
            float w = TextWidth(g, s, family, px, bold);
            End(g);
            return w;
        }

        // Superficie 32 bpp con alfa premultiplicado: se dibuja con GDI+ (alfa correcto) y se copia a la ventana.
        public sealed class AlphaSurface
        {
            public IntPtr Dc, Dib, OldBmp, Bits, Bitmap;
            public int W, H;

            public void Resize(IntPtr refDc, int w, int h)
            {
                if (Dc != IntPtr.Zero && w == W && h == H) return;
                Free();
                W = w; H = h;
                Dc = Native.CreateCompatibleDC(refDc);
                var bmi = new BITMAPINFOHEADER32();
                bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER32));
                bmi.biWidth = w;
                bmi.biHeight = -h;
                bmi.biPlanes = 1;
                bmi.biBitCount = 32;
                Dib = CreateDIBSection(Dc, ref bmi, 0, out Bits, IntPtr.Zero, 0);
                OldBmp = Native.SelectObject(Dc, Dib);
                GdipCreateBitmapFromScan0(w, h, w * 4, 0x000E200B /* PARGB */, Bits, out Bitmap);
            }

            public IntPtr BeginGraphics()
            {
                IntPtr g;
                GdipGetImageGraphicsContext(Bitmap, out g);
                GdipSetSmoothingMode(g, 4);
                GdipSetInterpolationMode(g, 7);
                GdipSetPixelOffsetMode(g, 4);
                return g;
            }

            public void Present(IntPtr destDc)
            {
                Native.BitBlt(destDc, 0, 0, W, H, Dc, 0, 0, 0x00CC0020);
            }

            // Entrega el HBITMAP (32 bpp con alfa) y libera el resto; quien lo reciba debe hacer DeleteObject.
            public IntPtr Detach()
            {
                IntPtr dib = Dib;
                if (Bitmap != IntPtr.Zero) { GdipDisposeImage(Bitmap); Bitmap = IntPtr.Zero; }
                if (Dc != IntPtr.Zero)
                {
                    Native.SelectObject(Dc, OldBmp);
                    Native.DeleteDC(Dc);
                }
                Dc = Dib = Bits = IntPtr.Zero;
                W = H = 0;
                return dib;
            }

            public void Free()
            {
                if (Bitmap != IntPtr.Zero) { GdipDisposeImage(Bitmap); Bitmap = IntPtr.Zero; }
                if (Dc != IntPtr.Zero)
                {
                    Native.SelectObject(Dc, OldBmp);
                    if (Dib != IntPtr.Zero) Native.DeleteObject(Dib);
                    Native.DeleteDC(Dc);
                }
                Dc = Dib = Bits = IntPtr.Zero;
                W = H = 0;
            }
        }
    }
}
