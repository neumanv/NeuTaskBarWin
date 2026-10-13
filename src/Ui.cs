using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace NeuTaskBar
{
    // Estilo y utilidades comunes de la interfaz de la aplicación (tema claro monocromo).
    static class Ui
    {
        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static readonly Color Bg = Color.FromArgb(247, 247, 249);
        public static readonly Color Side = Color.FromArgb(238, 238, 242);
        public static readonly Color CardFill = Color.White;
        public static readonly Color Border = Color.FromArgb(226, 226, 231);
        public static readonly Color Ink = Color.FromArgb(17, 17, 19);
        public static readonly Color Muted = Color.FromArgb(98, 100, 106);
        public static readonly Color Faint = Color.FromArgb(160, 162, 168);

        static string iconFamily;

        public static Font Font(float px, bool semibold)
        {
            return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", px * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // Fuente de iconos del sistema (Segoe Fluent Icons en Windows 11, Segoe MDL2 Assets en Windows 10).
        public static Font Icons(float px)
        {
            if (iconFamily == null)
            {
                iconFamily = "Segoe MDL2 Assets";
                using (var fc = new InstalledFontCollection())
                    foreach (var f in fc.Families) if (f.Name == "Segoe Fluent Icons") { iconFamily = f.Name; break; }
            }
            return new Font(iconFamily, px * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.01f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color fill, Color? border)
        {
            using (var path = Round(r, radius))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.HasValue) using (var pen = new Pen(border.Value, 1f)) g.DrawPath(pen, path);
            }
        }

        public static void Text(Graphics g, string text, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, f, r, c, flags | TextFormatFlags.NoPadding);
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Marca de GitHub para el enlace al repositorio: punto de partida y curvas de Bézier (dos puntos de control y
        // el final de cada una) en una cuadrícula de 16x16.
        static readonly float[] GitHubPath =
        {
            8f, 0f, 3.58f, 0f, 0f, 3.58f, 0f, 8f, 0f, 11.54f, 2.29f, 14.53f, 5.47f, 15.59f, 5.87f, 15.66f, 6.02f, 15.42f, 6.02f, 15.21f, 6.02f, 15.02f,
            6.01f, 14.39f, 6.01f, 13.72f, 4f, 14.09f, 3.48f, 13.23f, 3.32f, 12.78f, 3.23f, 12.55f, 2.84f, 11.84f, 2.5f, 11.65f, 2.22f, 11.5f, 1.82f,
            11.13f, 2.49f, 11.12f, 3.12f, 11.11f, 3.57f, 11.7f, 3.72f, 11.94f, 4.44f, 13.15f, 5.59f, 12.81f, 6.05f, 12.6f, 6.12f, 12.08f, 6.33f, 11.73f,
            6.56f, 11.53f, 4.78f, 11.33f, 2.92f, 10.64f, 2.92f, 7.58f, 2.92f, 6.71f, 3.23f, 5.99f, 3.74f, 5.43f, 3.66f, 5.23f, 3.38f, 4.41f, 3.82f,
            3.31f, 3.82f, 3.31f, 4.49f, 3.1f, 6.02f, 4.13f, 6.66f, 3.95f, 7.34f, 3.86f, 8.02f, 3.86f, 8.7f, 3.86f, 9.38f, 3.95f, 10.02f, 4.13f, 11.55f,
            3.09f, 12.22f, 3.31f, 12.22f, 3.31f, 12.66f, 4.41f, 12.38f, 5.23f, 12.3f, 5.43f, 12.81f, 5.99f, 13.12f, 6.7f, 13.12f, 7.58f, 13.12f, 10.65f,
            11.25f, 11.33f, 9.47f, 11.53f, 9.76f, 11.78f, 10.01f, 12.26f, 10.01f, 13.01f, 10.01f, 14.08f, 10f, 14.94f, 10f, 15.21f, 10f, 15.42f, 10.15f,
            15.67f, 10.55f, 15.59f, 13.806f, 14.491f, 15.999f, 11.437f, 16f, 8f, 16f, 3.58f, 12.42f, 0f, 8f, 0f
        };

        public static void GitHubMark(Graphics g, RectangleF r, Color color)
        {
            float k = Math.Min(r.Width, r.Height) / 16f;
            float[] p = GitHubPath;
            using (var path = new GraphicsPath())
            {
                var from = new PointF(r.X + p[0] * k, r.Y + p[1] * k);
                for (int i = 2; i + 5 < p.Length; i += 6)
                {
                    var to = new PointF(r.X + p[i + 4] * k, r.Y + p[i + 5] * k);
                    path.AddBezier(from, new PointF(r.X + p[i] * k, r.Y + p[i + 1] * k), new PointF(r.X + p[i + 2] * k, r.Y + p[i + 3] * k), to);
                    from = to;
                }
                path.CloseFigure();
                using (var b = new SolidBrush(color)) g.FillPath(b, path);
            }
        }
    }

    // Barra lateral: marca y navegación entre páginas.
    sealed class NavBar : Control
    {
        public Image Logo;
        public readonly List<string> Titles = new List<string>();
        public readonly List<string> Glyphs = new List<string>();
        public event Action<int> SelectedChanged;
        int selected, hover = -1;

        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public NavBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        Rectangle ItemRect(int i)
        {
            return new Rectangle(Ui.S(12), Ui.S(92) + i * Ui.S(46), Width - Ui.S(24), Ui.S(42));
        }

        int HitItem(Point p)
        {
            for (int i = 0; i < Titles.Count; i++) if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitItem(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int h = HitItem(e.Location);
            if (h >= 0 && h != selected) Select(h);
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && selected < Titles.Count - 1) Select(selected + 1);
            else if (e.KeyCode == Keys.Up && selected > 0) Select(selected - 1);
            base.OnKeyDown(e);
        }

        void Select(int i)
        {
            selected = i;
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Ui.Side);
            Ui.Smooth(g);

            int ls = Ui.S(34);
            if (Logo != null)
            {
                float lh = ls, lw = ls * (float)Logo.Width / Logo.Height;
                g.DrawImage(Logo, Ui.S(22), Ui.S(26), lw, lh);
                using (var f = Ui.Font(19, true))
                    Ui.Text(g, "NeuTaskBar", f, Ui.Ink, new Rectangle(Ui.S(22) + (int)lw + Ui.S(12), Ui.S(26), Width, ls),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            using (var title = Ui.Font(14, false))
            using (var selTitle = Ui.Font(14, true))
            using (var icons = Ui.Icons(17))
            {
                for (int i = 0; i < Titles.Count; i++)
                {
                    var r = ItemRect(i);
                    bool sel = i == selected;
                    if (sel) Ui.FillRound(g, r, Ui.S(8), Color.White, Ui.Border);
                    else if (i == hover) Ui.FillRound(g, r, Ui.S(8), Color.FromArgb(226, 226, 231), null);
                    Ui.Text(g, Glyphs[i], icons, Ui.Ink, new Rectangle(r.Left + Ui.S(14), r.Top, Ui.S(24), r.Height),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    Ui.Text(g, Titles[i], sel ? selTitle : title, Ui.Ink, new Rectangle(r.Left + Ui.S(48), r.Top, r.Width - Ui.S(52), r.Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }

    enum PillMode { Primary, Outline, Busy }

    // Botón redondeado con transición suave de color al pasar el ratón / pulsar.
    sealed class PillButton : Control
    {
        public float FontPx = 16;
        PillMode mode = PillMode.Primary;
        float hover, press;
        bool over, down;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();

        public PillMode Mode { get { return mode; } set { mode = value; Cursor = value == PillMode.Busy ? Cursors.Default : Cursors.Hand; Invalidate(); } }

        public PillButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            anim.Interval = 15;
            anim.Tick += (o, e) => Step();
        }

        void Step()
        {
            float th = over ? 1f : 0f, tp = down ? 1f : 0f;
            hover += (th - hover) * 0.3f;
            press += (tp - press) * 0.4f;
            if (Math.Abs(th - hover) < 0.01f) hover = th;
            if (Math.Abs(tp - press) < 0.01f) press = tp;
            if (hover == th && press == tp) anim.Stop();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { over = true; anim.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; down = false; anim.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Focus(); anim.Start(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; anim.Start(); base.OnMouseUp(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnClick(EventArgs e) { if (mode != PillMode.Busy) base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);

            Color fill, text, border = Color.Empty;
            float t = mode == PillMode.Busy ? 0f : hover, p = mode == PillMode.Busy ? 0f : press;
            switch (mode)
            {
                case PillMode.Primary:
                    fill = Ui.Lerp(Ui.Lerp(Ui.Ink, Color.FromArgb(52, 52, 56), t), Color.Black, p);
                    text = Color.White;
                    break;
                case PillMode.Outline:
                    fill = Ui.Lerp(Ui.Lerp(Color.White, Color.FromArgb(236, 236, 240), t), Color.FromArgb(222, 222, 227), p);
                    text = Ui.Ink;
                    border = Ui.Ink;
                    break;
                default:
                    fill = Color.FromArgb(200, 200, 205);
                    text = Color.White;
                    break;
            }

            var r = new RectangleF(1f, 1f + p * Ui.Scale, Width - 3f, Height - 3f);
            using (var path = Ui.Round(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty) using (var pen = new Pen(border, 1.5f * Ui.Scale)) g.DrawPath(pen, path);
            }
            using (var f = new Font("Segoe UI Semibold", FontPx * Ui.Scale, FontStyle.Regular, GraphicsUnit.Pixel))
                Ui.Text(g, Text, f, text, new Rectangle(0, (int)(p * Ui.Scale), Width, Height),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }
    }

    // Interruptor con animación.
    sealed class ToggleSwitch : Control
    {
        public event EventHandler CheckedChanged;
        bool on;
        float t;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; anim.Start(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        public void SetCheckedSilently(bool value) { on = value; t = value ? 1f : 0f; Invalidate(); }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            anim.Interval = 15;
            anim.Tick += (o, e) =>
            {
                float target = on ? 1f : 0f;
                t += (target - t) * 0.35f;
                if (Math.Abs(target - t) < 0.01f) { t = target; anim.Stop(); }
                Invalidate();
            };
        }

        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Checked = !Checked; base.OnMouseUp(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            var r = new RectangleF(1f, 1f, Width - 3f, Height - 3f);
            Color track = Ui.Lerp(Color.FromArgb(209, 209, 215), Ui.Ink, t);
            using (var path = Ui.Round(r, r.Height / 2f)) using (var b = new SolidBrush(track)) g.FillPath(b, path);
            float k = r.Height - Ui.S(6);
            float x = r.X + Ui.S(3) + (r.Width - k - Ui.S(6)) * t;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, r.Y + Ui.S(3), k, k);
            if (Focused && ShowFocusCues) using (var pen = new Pen(Ui.Ink, 1f)) using (var path = Ui.Round(new RectangleF(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4), r.Height / 2f + 2)) g.DrawPath(pen, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }
    }

    // Selector de una opción entre unas pocas: una píldora partida en segmentos, con el elegido resaltado.
    sealed class SegmentedPicker : Control
    {
        public event Action<int> SelectedChanged;
        readonly string[] options;
        int selected, hover = -1;

        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public SegmentedPicker(string[] options) : this(options, 88) { }

        // segment = ancho de cada segmento a 96 dpi (para opciones con el texto más largo de lo habitual).
        public SegmentedPicker(string[] options, int segment)
        {
            this.options = options;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(options.Length * Ui.S(segment), Ui.S(34));
        }

        int Hit(Point p)
        {
            return ClientRectangle.Contains(p) ? Math.Min(options.Length - 1, p.X * options.Length / Math.Max(1, Width)) : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = Hit(e.Location);
            if (e.Button != MouseButtons.Left || h < 0 || h == selected) return;
            selected = h;
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
            Ui.FillRound(g, r, r.Height / 2f, Color.FromArgb(242, 242, 245), Ui.Border);
            float w = r.Width / options.Length;
            using (var f = Ui.Font(13, false))
            using (var fs = Ui.Font(13, true))
                for (int i = 0; i < options.Length; i++)
                {
                    var s = new RectangleF(r.X + i * w + Ui.S(3), r.Y + Ui.S(3), w - Ui.S(6), r.Height - Ui.S(6));
                    if (i == selected) Ui.FillRound(g, s, s.Height / 2f, Ui.Ink, null);
                    else if (i == hover) Ui.FillRound(g, s, s.Height / 2f, Color.FromArgb(226, 226, 231), null);
                    Ui.Text(g, options[i], i == selected ? fs : f, i == selected ? Color.White : Ui.Ink,
                        Rectangle.Round(new RectangleF(r.X + i * w, 0, w, Height)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
        }
    }

    // Selector de color: una fila de círculos con colores predefinidos, "transparente" y, al final, uno a medida que
    // abre el selector de colores de Windows. El valor es AARRGGBB (alfa 0 = transparente).
    sealed class SwatchPicker : Control
    {
        const uint TintOpacity = 0xCC000000;   // opacidad de los colores de isla elegidos aquí (el predeterminado lleva la suya)
        static readonly uint[] TintPresets = { 0x8C1A1A1A, TintOpacity | 0x005FB8, TintOpacity | 0x0F7B0F, TintOpacity | 0xC42B1C, TintOpacity | 0xF3F3F3, 0 };
        static readonly string[] TintNames = { "Predeterminado", "Azul", "Verde", "Rojo", "Claro", "Transparente", "Otro color…" };

        public event Action<uint> ValueChanged;
        readonly ToolTip tip = new ToolTip();
        // Colores predefinidos, sus nombres (uno más: el del color a medida), opacidad de los colores a medida y color
        // con el que se pinta el predefinido 0 (0 = es "sin color" y lleva una raya).
        readonly uint[] Presets;
        readonly string[] Names;
        readonly uint Opacity, zeroFill;
        uint value;
        int hover = -1;

        public uint Value { get { return value; } set { this.value = value; Invalidate(); } }

        // Selector del color de una isla.
        public SwatchPicker() : this(TintPresets, TintNames, TintOpacity, 0) { }

        public SwatchPicker(uint[] presets, string[] names, uint opacity, uint zeroFill)
        {
            Presets = presets;
            Names = names;
            Opacity = opacity;
            this.zeroFill = zeroFill;
            value = presets[0];
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Size = new Size(Names.Length * Ui.S(22) + (Names.Length - 1) * Ui.S(7) + Ui.S(8), Ui.S(30));
        }

        // Círculo elegido: el predefinido que coincide o, si no coincide ninguno, el de color a medida (el último).
        int Selected
        {
            get { int i = Array.IndexOf(Presets, value); return i >= 0 ? i : Presets.Length; }
        }

        RectangleF Circle(int i)
        {
            return new RectangleF(Ui.S(4) + i * (Ui.S(22) + Ui.S(7)), Ui.S(4), Ui.S(22), Ui.S(22));
        }

        int Hit(Point p)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                var r = Circle(i);
                r.Inflate(Ui.S(3), Ui.S(3));
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; tip.SetToolTip(this, h < 0 ? "" : L.T(Names[h])); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = Hit(e.Location);
            if (e.Button != MouseButtons.Left || h < 0) return;
            uint picked;
            if (h < Presets.Length) picked = Presets[h];
            else
            {
                using (var dlg = new ColorDialog())
                {
                    dlg.FullOpen = true;
                    dlg.Color = Color.FromArgb(255, Color.FromArgb(unchecked((int)value)));
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    picked = Opacity | ((uint)dlg.Color.ToArgb() & 0xFFFFFF);
                }
            }
            if (picked == value) return;
            value = picked;
            Invalidate();
            if (ValueChanged != null) ValueChanged(value);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            int sel = Selected;
            for (int i = 0; i < Names.Length; i++)
            {
                var r = Circle(i);
                bool custom = i == Presets.Length, none = i < Presets.Length && Presets[i] == 0 && zeroFill == 0;
                Color fill = custom ? (sel == i ? Color.FromArgb(255, Color.FromArgb(unchecked((int)value))) : Color.White)
                    : none ? Color.White : Color.FromArgb(255, Color.FromArgb(unchecked((int)(Presets[i] == 0 ? zeroFill : Presets[i]))));
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
                using (var pen = new Pen(Color.FromArgb(200, 200, 206), 1f)) g.DrawEllipse(pen, r);
                if (none)
                {
                    // "Sin color": una raya roja en diagonal.
                    float k = r.Width * 0.146f;
                    using (var pen = new Pen(Color.FromArgb(214, 60, 48), Math.Max(1.5f, Ui.S(2))))
                        g.DrawLine(pen, r.Right - k, r.Top + k, r.Left + k, r.Bottom - k);
                }
                else if (custom && sel != i)
                {
                    using (var f = Ui.Font(17, false))
                        Ui.Text(g, "+", f, Ui.Ink, Rectangle.Round(new RectangleF(r.X, r.Y - Ui.S(1), r.Width, r.Height)),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                if (i == sel)
                {
                    var ring = r;
                    ring.Inflate(Ui.S(3), Ui.S(3));
                    using (var pen = new Pen(Ui.Ink, Math.Max(1.5f, Ui.S(2)))) g.DrawEllipse(pen, ring);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }

    // Deslizante: una pista con su tirador y, a la derecha, el valor elegido. Avisa cada vez que el valor cambia.
    sealed class Slider : Control
    {
        public event Action<int> ValueChanged;
        public Func<int, string> Format = v => v.ToString();
        readonly int min, max, step;
        int value;
        bool drag;

        public int Value { get { return value; } set { this.value = Snap(value); Invalidate(); } }

        public Slider(int min, int max, int step)
        {
            this.min = min;
            this.max = max;
            this.step = Math.Max(1, step);
            value = min;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Size = new Size(Ui.S(236), Ui.S(30));
        }

        int Snap(int v)
        {
            v = Math.Max(min, Math.Min(max, v));
            return Math.Min(max, min + (int)Math.Round((v - min) / (double)step) * step);
        }

        // Pista: desde el centro del tirador en el mínimo hasta el centro en el máximo; a su derecha queda el valor.
        int TrackLeft { get { return Ui.S(10); } }
        int TrackRight { get { return Width - Ui.S(64); } }

        void Set(int v)
        {
            v = Snap(v);
            if (v == value) return;
            value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(value);
        }

        void SetFromX(int x)
        {
            double t = (x - TrackLeft) / (double)Math.Max(1, TrackRight - TrackLeft);
            Set(min + (int)Math.Round(Math.Max(0.0, Math.Min(1.0, t)) * (max - min)));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { drag = true; Focus(); SetFromX(e.X); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { Set(value - step); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Set(value + step); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            float x0 = TrackLeft, x1 = TrackRight, cy = Height / 2f, th = Ui.S(4);
            float kx = x0 + (x1 - x0) * (value - min) / (float)Math.Max(1, max - min);
            Ui.FillRound(g, new RectangleF(x0, cy - th / 2f, x1 - x0, th), th / 2f, Color.FromArgb(209, 209, 215), null);
            if (kx > x0) Ui.FillRound(g, new RectangleF(x0, cy - th / 2f, kx - x0, th), th / 2f, Ui.Ink, null);
            float k = Ui.S(16);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - k / 2f, cy - k / 2f, k, k);
            using (var pen = new Pen(Ui.Ink, Focused && ShowFocusCues ? Ui.S(3) : Math.Max(1.5f, Ui.S(2)))) g.DrawEllipse(pen, kx - k / 2f, cy - k / 2f, k, k);
            using (var f = Ui.Font(13, false))
                Ui.Text(g, Format(value), f, Ui.Ink, new Rectangle((int)x1 + Ui.S(12), 0, Width - (int)x1 - Ui.S(12), Height),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    // Fila de una opción: título, descripción y un control a la derecha (interruptor, botón...).
    sealed class SettingRow : Control
    {
        public string Title = "", Description = "";
        public Control Trailing;
        // Fila-enlace: un icono delante y toda ella pulsable (ver Link).
        Action<Graphics, RectangleF> icon;
        Action clicked;
        bool over;

        public SettingRow(string title, string description, Control trailing)
        {
            Title = title;
            Description = description;
            Trailing = trailing;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;   // la tarjeta (redondeada) pinta el fondo
            if (trailing != null) { trailing.BackColor = Ui.CardFill; Controls.Add(trailing); }
        }

        public int PreferredHeight { get { return Ui.S(Description.Length > 0 ? 72 : 56); } }

        // Convierte la fila en un enlace: dibuja el icono delante del texto y ejecuta la acción al pulsar en cualquier
        // punto de ella.
        public SettingRow Link(Action<Graphics, RectangleF> icon, Action clicked)
        {
            this.icon = icon;
            this.clicked = clicked;
            Cursor = Cursors.Hand;
            return this;
        }

        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (clicked != null && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) clicked();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Trailing != null)
            {
                Trailing.Left = Width - Ui.S(24) - Trailing.Width;
                Trailing.Top = (Height - Trailing.Height) / 2;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            int left = Ui.S(24);
            int right = Trailing != null ? Trailing.Left - Ui.S(16) : Width - Ui.S(24);
            if (clicked != null)
            {
                if (over) Ui.FillRound(g, new RectangleF(Ui.S(8), Ui.S(6), Width - Ui.S(16), Height - Ui.S(12)), Ui.S(8), Color.FromArgb(243, 243, 246), null);
                // Flecha de "se abre fuera", a la derecha.
                using (var glyphs = Ui.Icons(15))
                    Ui.Text(g, "\uE8A7", glyphs, Ui.Muted, new Rectangle(right - Ui.S(24), 0, Ui.S(24), Height),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                right -= Ui.S(32);
            }
            if (icon != null)
            {
                float size = Ui.S(30);
                icon(g, new RectangleF(left, (Height - size) / 2f, size, size));
                left += (int)size + Ui.S(16);
            }
            using (var f = Ui.Font(15, true))
            using (var d = Ui.Font(13, false))
            {
                if (Description.Length > 0)
                {
                    Ui.Text(g, Title, f, Ui.Ink, new Rectangle(left, Ui.S(14), right - left, Ui.S(22)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    Ui.Text(g, Description, d, Ui.Muted, new Rectangle(left, Ui.S(38), right - left, Ui.S(20)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                }
                else
                    Ui.Text(g, Title, f, Ui.Ink, new Rectangle(left, 0, right - left, Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Tarjeta redondeada que apila filas de opciones.
    sealed class Card : Control
    {
        public readonly List<SettingRow> Rows = new List<SettingRow>();

        public Card()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        public void AddRow(SettingRow row)
        {
            Rows.Add(row);
            Controls.Add(row);
        }

        public int PreferredHeight
        {
            get { int h = 2; foreach (var r in Rows) h += r.PreferredHeight + 1; return h; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = 1;
            foreach (var r in Rows)
            {
                r.SetBounds(1, y, Width - 2, r.PreferredHeight);
                y += r.PreferredHeight + 1;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f), Ui.S(10), Ui.CardFill, Ui.Border);
            // separadores entre filas
            using (var pen = new Pen(Ui.Border, 1f))
                for (int i = 0; i < Rows.Count - 1; i++)
                {
                    int y = Rows[i].Bottom;
                    g.DrawLine(pen, Ui.S(24), y, Width - Ui.S(24), y);
                }
        }
    }
}
