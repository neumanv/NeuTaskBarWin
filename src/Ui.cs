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
    }

    // Barra lateral: marca, navegación entre páginas y estado de la barra de tareas.
    sealed class NavBar : Control
    {
        public Image Logo;
        public readonly List<string> Titles = new List<string>();
        public readonly List<string> Glyphs = new List<string>();
        public string StatusText = "";
        public Color StatusColor = Ui.Faint;
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

            if (StatusText.Length > 0)
            {
                int d = Ui.S(9);
                int y = Height - Ui.S(46);
                using (var b = new SolidBrush(StatusColor)) g.FillEllipse(b, Ui.S(24), y + Ui.S(5), d, d);
                using (var f = Ui.Font(13, false))
                    Ui.Text(g, StatusText, f, Ui.Muted, new Rectangle(Ui.S(24) + d + Ui.S(8), y, Width, Ui.S(20)),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
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

    // Fila de una opción: título, descripción y un control a la derecha (interruptor, botón...).
    sealed class SettingRow : Control
    {
        public string Title = "", Description = "";
        public Control Trailing;

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
            int right = Trailing != null ? Trailing.Left - Ui.S(16) : Width - Ui.S(24);
            using (var f = Ui.Font(15, true))
            using (var d = Ui.Font(13, false))
            {
                if (Description.Length > 0)
                {
                    Ui.Text(g, Title, f, Ui.Ink, new Rectangle(Ui.S(24), Ui.S(14), right - Ui.S(24), Ui.S(22)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    Ui.Text(g, Description, d, Ui.Muted, new Rectangle(Ui.S(24), Ui.S(38), right - Ui.S(24), Ui.S(20)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                }
                else
                    Ui.Text(g, Title, f, Ui.Ink, new Rectangle(Ui.S(24), 0, right - Ui.S(24), Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
