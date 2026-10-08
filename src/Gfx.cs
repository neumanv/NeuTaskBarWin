using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NeuTaskBar
{
    // Capa mínima sobre GDI+ (API plana) para dibujo suavizado y escalado de iconos de alta calidad.
    static partial class Gfx
    {
        [StructLayout(LayoutKind.Sequential)]
        struct StartupInput { public uint Version; public IntPtr Callback; public int SuppressThread, SuppressCodecs; }

        [DllImport("gdiplus.dll")] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
        [DllImport("gdiplus.dll")] static extern void GdiplusShutdown(IntPtr token);
        [DllImport("gdiplus.dll")] static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr g);
        [DllImport("gdiplus.dll")] static extern int GdipDeleteGraphics(IntPtr g);
        [DllImport("gdiplus.dll")] static extern int GdipSetSmoothingMode(IntPtr g, int mode);
        [DllImport("gdiplus.dll")] static extern int GdipSetInterpolationMode(IntPtr g, int mode);
        [DllImport("gdiplus.dll")] static extern int GdipSetPixelOffsetMode(IntPtr g, int mode);
        [DllImport("gdiplus.dll")] static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
        [DllImport("gdiplus.dll")] static extern int GdipSetSolidFillColor(IntPtr brush, uint argb);
        [DllImport("gdiplus.dll")] static extern int GdipDeleteBrush(IntPtr brush);
        [DllImport("gdiplus.dll")] static extern int GdipFillRectangle(IntPtr g, IntPtr brush, float x, float y, float w, float h);
        [DllImport("gdiplus.dll")] static extern int GdipCreatePath(int fillMode, out IntPtr path);
        [DllImport("gdiplus.dll")] static extern int GdipDeletePath(IntPtr path);
        [DllImport("gdiplus.dll")] static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
        [DllImport("gdiplus.dll")] static extern int GdipClosePathFigure(IntPtr path);
        [DllImport("gdiplus.dll")] static extern int GdipFillPath(IntPtr g, IntPtr brush, IntPtr path);
        [DllImport("gdiplus.dll")] static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bmp);
        [DllImport("gdiplus.dll")] static extern int GdipCreateBitmapFromHICON(IntPtr hicon, out IntPtr bmp);
        [DllImport("gdiplus.dll")] static extern int GdipDisposeImage(IntPtr img);
        [DllImport("gdiplus.dll")] static extern int GdipGetImageWidth(IntPtr img, out uint w);
        [DllImport("gdiplus.dll")] static extern int GdipGetImageHeight(IntPtr img, out uint h);
        [DllImport("gdiplus.dll")] static extern int GdipCreateImageAttributes(out IntPtr attr);
        [DllImport("gdiplus.dll")] static extern int GdipDisposeImageAttributes(IntPtr attr);
        [DllImport("gdiplus.dll")]
        static extern int GdipSetImageAttributesColorMatrix(IntPtr attr, int type, bool enable, float[] colorMatrix, float[] grayMatrix, int flags);
        [DllImport("gdiplus.dll")] static extern int GdipSetImageAttributesWrapMode(IntPtr attr, int wrap, uint argb, bool clamp);
        [DllImport("gdiplus.dll")]
        static extern int GdipDrawImageRectRect(IntPtr g, IntPtr img, float dx, float dy, float dw, float dh,
            float sx, float sy, float sw, float sh, int unit, IntPtr attrs, IntPtr callback, IntPtr data);

        static IntPtr token, brush, attrs;
        static readonly float[] matrix = new float[25];

        public static void Init()
        {
            var si = new StartupInput { Version = 1 };
            GdiplusStartup(out token, ref si, IntPtr.Zero);
            GdipCreateSolidFill(0, out brush);
            GdipCreateImageAttributes(out attrs);
            GdipSetImageAttributesWrapMode(attrs, 3 /* TileFlipXY */, 0, false);
        }

        public static void Shutdown()
        {
            if (attrs != IntPtr.Zero) GdipDisposeImageAttributes(attrs);
            if (brush != IntPtr.Zero) GdipDeleteBrush(brush);
            if (token != IntPtr.Zero) GdiplusShutdown(token);
            attrs = brush = token = IntPtr.Zero;
        }

        public static IntPtr Begin(IntPtr dc)
        {
            IntPtr g;
            GdipCreateFromHDC(dc, out g);
            GdipSetSmoothingMode(g, 4);        // AntiAlias
            GdipSetInterpolationMode(g, 7);    // HighQualityBicubic
            GdipSetPixelOffsetMode(g, 4);      // Half
            return g;
        }

        public static void End(IntPtr g) { if (g != IntPtr.Zero) GdipDeleteGraphics(g); }

        public static uint Argb(float alpha, int r, int g, int b)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, alpha)) * 255f);
            return (uint)((a << 24) | (r << 16) | (g << 8) | b);
        }

        public static void FillRect(IntPtr g, float x, float y, float w, float h, uint argb)
        {
            if (w <= 0 || h <= 0 || (argb >> 24) == 0) return;
            GdipSetSolidFillColor(brush, argb);
            GdipFillRectangle(g, brush, x, y, w, h);
        }

        public static void FillRound(IntPtr g, float x, float y, float w, float h, float r, uint argb)
        {
            if (w <= 0 || h <= 0 || (argb >> 24) == 0) return;
            r = Math.Min(r, Math.Min(w, h) / 2f);
            if (r < 0.5f) { FillRect(g, x, y, w, h, argb); return; }
            float d = r * 2f;
            IntPtr path;
            GdipCreatePath(0, out path);
            GdipAddPathArc(path, x, y, d, d, 180, 90);
            GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
            GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
            GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
            GdipClosePathFigure(path);
            GdipSetSolidFillColor(brush, argb);
            GdipFillPath(g, brush, path);
            GdipDeletePath(path);
        }

        public static void DrawImage(IntPtr g, IconImage img, float x, float y, float w, float h, float alpha)
        {
            if (img == null || img.Image == IntPtr.Zero || w <= 0 || h <= 0 || alpha <= 0f) return;
            IntPtr a = IntPtr.Zero;
            if (alpha < 0.995f)
            {
                Array.Clear(matrix, 0, 25);
                matrix[0] = matrix[6] = matrix[12] = 1f;
                matrix[18] = alpha;
                matrix[24] = 1f;
                GdipSetImageAttributesColorMatrix(attrs, 0, true, matrix, null, 0);
                a = attrs;
            }
            else
            {
                a = attrs;
                GdipSetImageAttributesColorMatrix(attrs, 0, false, null, null, 0);
            }
            GdipDrawImageRectRect(g, img.Image, x, y, w, h, 0, 0, img.W, img.H, 2, a, IntPtr.Zero, IntPtr.Zero);
        }

        // ---- imágenes ----
        public static IconImage FromScan0(int w, int h, IntPtr scan0, bool premultiplied)
        {
            IntPtr bmp;
            int fmt = premultiplied ? 0x000E200B /* 32bppPARGB */ : 0x0026200A /* 32bppARGB */;
            if (GdipCreateBitmapFromScan0(w, h, w * 4, fmt, scan0, out bmp) != 0 || bmp == IntPtr.Zero) return null;
            return new IconImage { Image = bmp, W = w, H = h };
        }

        public static IconImage FromHicon(IntPtr hicon)
        {
            IntPtr bmp;
            if (GdipCreateBitmapFromHICON(hicon, out bmp) != 0 || bmp == IntPtr.Zero) return null;
            uint w, h;
            GdipGetImageWidth(bmp, out w);
            GdipGetImageHeight(bmp, out h);
            return new IconImage { Image = bmp, W = (int)w, H = (int)h };
        }

        public static void Dispose(IconImage img)
        {
            if (img != null && img.Image != IntPtr.Zero) { GdipDisposeImage(img.Image); img.Image = IntPtr.Zero; }
        }
    }

    sealed class IconImage
    {
        public IntPtr Image;
        public int W, H;
    }

    // Valor animado con aproximación exponencial (independiente de la tasa de refresco).
    struct Ease
    {
        public float V, T;
        public Ease(float v) { V = v; T = v; }
        public bool Step(float dt, float rate, float eps)
        {
            if (V == T) return false;
            float d = T - V;
            if (Math.Abs(d) <= eps) { V = T; return true; }
            V += d * (1f - (float)Math.Exp(-rate * dt));
            if (Math.Abs(T - V) <= eps) V = T;
            return true;
        }
        public bool Settled { get { return V == T; } }
    }
}
