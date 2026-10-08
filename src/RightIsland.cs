using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeuTaskBar
{
    // Isla derecha: flecha de iconos ocultos + iconos de sistema (red, volumen, batería) + reloj y fecha.
    sealed class RightIsland : Island
    {
        public const int None = -1, Chevron = 0, Sys = 1, Clock = 2;

        public Action OnChevronClick, OnSysClick, OnClockClick;
        public Action<int> OnButtonDown;
        public Action<int, int> OnSysContext, OnClockContext, OnChevronContext;
        public Action<int> OnVolumeWheel;
        public Func<VolumeMonitor> Volume;
        public int LastSysSlot;   // icono de sistema bajo el cursor en el último clic derecho: 0 red, 1 volumen, 2 batería

        NetKind net = NetKind.None;
        BatteryState battery;
        string timeText = "", dateText = "";
        string timeSample = "", dateSample = "";
        readonly RECT[] rects = new RECT[3];
        readonly Ease[] hoverE = new Ease[3];
        readonly Ease[] pressE = new Ease[3];
        int hover = None, pressed = None;
        Tip tip;

        public RightIsland() : base(true) { }

        public void Init()
        {
            Create(false);
            Tip.InitControls();
            tip = new Tip(Hwnd, TipText);
        }

        string TipText(int idx)
        {
            if (idx == Chevron) return "Mostrar iconos ocultos";
            if (idx == Sys)
            {
                var parts = new List<string>();
                parts.Add(net == NetKind.Wifi ? "Wi-Fi conectado" : net == NetKind.Ethernet ? "Ethernet conectado" : "Sin conexión a Internet");
                var vol = Volume != null ? Volume() : null;
                if (vol != null && vol.Available) parts.Add(vol.Muted ? "Volumen: silenciado" : "Volumen: " + vol.Level + "%");
                if (battery.Present)
                    parts.Add("Batería: " + battery.Percent + "%" + (battery.Charging ? " (cargando)" : battery.AcPower ? " (conectada)" : ""));
                return string.Join("\n", parts.ToArray());
            }
            return DateTime.Now.ToString("dddd, d 'de' MMMM 'de' yyyy", CultureInfo.CurrentCulture);
        }

        public void UpdateNetwork(NetKind k) { if (k != net) { net = k; Layout(); } }

        public void UpdateBattery(BatteryState b)
        {
            bool relayout = b.Present != battery.Present;
            battery = b;
            if (relayout) Layout(); else Invalidate();
        }

        public void UpdateClock()
        {
            var now = DateTime.Now;
            string t = now.ToString("t", CultureInfo.CurrentCulture);
            string d = now.ToString("d", CultureInfo.CurrentCulture);
            if (t == timeText && d == dateText) return;
            timeText = t;
            dateText = d;
            if (timeSample == "")
            {
                timeSample = new DateTime(2000, 8, 28, 20, 48, 0).ToString("t", CultureInfo.CurrentCulture);
                dateSample = new DateTime(2000, 8, 28).ToString("d", CultureInfo.CurrentCulture);
            }
            Layout();
        }

        public void Refresh() { Invalidate(); }

        int IconSlots { get { return battery.Present ? 3 : 2; } }

        public override void Layout()
        {
            IntPtr sdc = Native.GetDC(IntPtr.Zero);
            int h = S(Config.Height);
            int pad = S(6);
            var clockFont = Gdi.Font("Segoe UI", S(12), 400);
            int tw = Math.Max(Gdi.TextWidth(sdc, timeText, clockFont), Gdi.TextWidth(sdc, timeSample, clockFont));
            int dw = Math.Max(Gdi.TextWidth(sdc, dateText, clockFont), Gdi.TextWidth(sdc, dateSample, clockFont));
            Native.ReleaseDC(IntPtr.Zero, sdc);

            int clockW = Math.Max(tw, dw) + S(16);
            int chevW = S(26);
            int sysW = IconSlots * S(26) + S(10);
            int y = S(4), cellH = h - S(8);
            rects[Chevron] = new RECT(pad, y, pad + chevW, y + cellH);
            rects[Sys] = new RECT(rects[Chevron].Right + S(2), y, rects[Chevron].Right + S(2) + sysW, y + cellH);
            rects[Clock] = new RECT(rects[Sys].Right + S(2), y, rects[Sys].Right + S(2) + clockW, y + cellH);
            VisW = rects[Clock].Right + pad;
            VisH = h;
            Reposition();

            if (tip != null)
            {
                var list = new List<RECT>();
                foreach (var r in rects) list.Add(new RECT(r.Left + ovL, r.Top, r.Right + ovL, r.Bottom));
                tip.Rebuild(list);
            }
        }

        int HitTest(int x, int y)
        {
            for (int i = 0; i < 3; i++) if (rects[i].Contains(x, y)) return i;
            return None;
        }

        protected override bool OnAnimTick(float dt)
        {
            bool more = false;
            for (int i = 0; i < 3; i++)
            {
                more |= hoverE[i].Step(dt, hoverE[i].T > hoverE[i].V ? 28f : 12f, 0.005f);
                more |= pressE[i].Step(dt, pressE[i].T > pressE[i].V ? 40f : 16f, 0.005f);
            }
            return more;
        }

        protected override void OnMouseMove(int x, int y)
        {
            int h = HitTest(x, y);
            if (h == hover) return;
            if (hover != None) hoverE[hover].T = 0f;
            hover = h;
            if (h != None) hoverE[h].T = 1f;
            StartAnim();
        }

        protected override void OnMouseLeave()
        {
            if (hover != None) hoverE[hover].T = 0f;
            if (pressed != None) pressE[pressed].T = 0f;
            hover = pressed = None;
            StartAnim();
        }

        protected override void OnMouseButton(int button, bool down, int x, int y)
        {
            int hit = HitTest(x, y);
            if (button == 0)
            {
                if (down)
                {
                    if (hit != None) { pressed = hit; pressE[hit].T = 1f; tip.Hide(); StartAnim(); if (OnButtonDown != null) OnButtonDown(hit); }
                    return;
                }
                int was = pressed;
                if (was != None) { pressE[was].T = 0f; StartAnim(); }
                pressed = None;
                if (hit == None || hit != was) return;
                if (hit == Chevron) { if (OnChevronClick != null) OnChevronClick(); }
                else if (hit == Sys) { if (OnSysClick != null) OnSysClick(); }
                else if (OnClockClick != null) OnClockClick();
            }
            else if (button == 2)
            {
                POINT p;
                Native.GetCursorPos(out p);
                if (hit == Sys) LastSysSlot = Math.Max(0, Math.Min(IconSlots - 1, (x - rects[Sys].Left - S(5)) / S(26)));
                if (hit == Sys && OnSysContext != null) OnSysContext(p.X, p.Y);
                else if (hit == Chevron && OnChevronContext != null) OnChevronContext(p.X, p.Y);
                else if (OnClockContext != null) OnClockContext(p.X, p.Y);
            }
        }

        protected override void OnWheel(int delta)
        {
            if (hover == Sys && OnVolumeWheel != null) OnVolumeWheel(delta);
        }

        protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_NOTIFY && tip != null && tip.HandleNotify(lParam)) return IntPtr.Zero;
            return base.WndProc(msg, wParam, lParam);
        }

        protected override void OnPaintContent(IntPtr dc)
        {
            float radius = S(6);
            IntPtr g = Gfx.Begin(dc);
            for (int i = 0; i < 3; i++)
            {
                float fill = hoverE[i].V * 0.16f * (1f - 0.45f * pressE[i].V);
                if (fill <= 0.002f) continue;
                var r = rects[i];
                Gfx.FillRound(g, r.Left, r.Top, r.Width, r.Height, radius, Gfx.Argb(fill, 255, 255, 255));
            }
            Gfx.End(g);

            int white = Native.Rgb(245, 245, 245), dim = Native.Rgb(120, 120, 120);
            string face = Gdi.IconFace(dc);

            // Flecha de iconos ocultos
            RECT cr = rects[Chevron];
            int nudge = (int)Math.Round(pressE[Chevron].V * S(1));
            Gdi.Text(dc, "", new RECT(cr.Left, cr.Top + nudge, cr.Right, cr.Bottom + nudge), Gdi.Font(face, S(12), 400), white,
                Gdi.DT_CENTER | Gdi.DT_VCENTER);

            // Iconos de sistema
            IntPtr iconFont = Gdi.Font(face, S(16), 400);
            int slot = S(26);
            int x = rects[Sys].Left + S(5);
            int ty = rects[Sys].Top, th = rects[Sys].Height;

            string netGlyph = net == NetKind.Ethernet ? "" : "";
            Gdi.Text(dc, netGlyph, new RECT(x, ty, x + slot, ty + th), iconFont, net == NetKind.None ? dim : white, Gdi.DT_CENTER | Gdi.DT_VCENTER);
            x += slot;

            var vol = Volume != null ? Volume() : null;
            string volGlyph;
            if (vol == null || !vol.Available) volGlyph = "";
            else if (vol.Muted || vol.Level == 0) volGlyph = vol.Muted ? "" : "";
            else if (vol.Level < 34) volGlyph = "";
            else if (vol.Level < 67) volGlyph = "";
            else volGlyph = "";
            Gdi.Text(dc, volGlyph, new RECT(x, ty, x + slot, ty + th), iconFont, white, Gdi.DT_CENTER | Gdi.DT_VCENTER);
            x += slot;

            if (battery.Present)
            {
                int lvl = Math.Min(10, battery.Percent / 10);
                string gl;
                if (battery.Charging) gl = lvl >= 10 ? "" : ((char)(0xE85A + Math.Min(lvl, 8))).ToString();
                else gl = lvl >= 10 ? "" : ((char)(0xE850 + lvl)).ToString();
                int col = (!battery.Charging && battery.Percent <= 15) ? Native.Rgb(255, 99, 99) : white;
                Gdi.Text(dc, gl, new RECT(x, ty, x + slot, ty + th), iconFont, col, Gdi.DT_CENTER | Gdi.DT_VCENTER);
            }

            // Reloj (dos líneas, alineado a la derecha como en Windows 11)
            RECT clk = rects[Clock];
            IntPtr f = Gdi.Font("Segoe UI", S(12), 400);
            int cx0 = clk.Left + S(4), cx1 = clk.Right - S(8);
            int mid = clk.Top + clk.Height / 2;
            Gdi.Text(dc, timeText, new RECT(cx0, clk.Top, cx1, mid + S(1)), f, white, Gdi.DT_RIGHT | Gdi.DT_VCENTER);
            Gdi.Text(dc, dateText, new RECT(cx0, mid - S(1), cx1, clk.Bottom), f, Native.Rgb(210, 210, 210), Gdi.DT_RIGHT | Gdi.DT_VCENTER);
        }

        public new void Destroy()
        {
            if (tip != null) tip.Destroy();
            base.Destroy();
        }
    }
}
