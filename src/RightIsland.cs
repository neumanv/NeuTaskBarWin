using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeuTaskBar
{
    // Isla derecha: flecha de iconos ocultos + teclado táctil + iconos de sistema (red, volumen, batería) + reloj y fecha
    // + esquina de mostrar escritorio. Cada parte aparece o no según los ajustes de la barra de tareas de Windows.
    sealed class RightIsland : Island
    {
        public const int None = -1, Chevron = 0, Sys = 1, Clock = 2, Keyboard = 3, Desktop = 4;
        const int Count = 5;

        public Action OnChevronClick, OnSysClick, OnClockClick, OnKeyboardClick, OnDesktopClick;
        public Action<int> OnButtonDown;
        public Action<int, int> OnSysContext, OnClockContext, OnChevronContext;
        public Action<int> OnVolumeWheel;
        public Func<VolumeMonitor> Volume;
        public int LastSysSlot;   // icono de sistema bajo el cursor en el último clic derecho: 0 red, 1 volumen, 2 batería

        NetKind net = NetKind.None;
        BatteryState battery;
        string timeText = "", dateText = "";
        string timeSample = "", dateSample = "";
        string dateShort = "", dateShortSample = "";   // fecha sin año, para barras verticales
        readonly RECT[] rects = new RECT[Count];
        readonly Ease[] hoverE = new Ease[Count];
        readonly Ease[] pressE = new Ease[Count];
        string timeFormat = "";
        int hover = None, pressed = None;
        int clockPx;   // tamaño de letra del reloj (se reduce en barras verticales para que quepa)
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
            if (idx == Keyboard) return "Teclado táctil";
            if (idx == Desktop) return "Mostrar escritorio";
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
            // Con "Mostrar segundos en el reloj" se usa el formato de hora larga del sistema.
            string fmt = Config.ClockSeconds ? "T" : "t";
            if (fmt != timeFormat) { timeFormat = fmt; timeSample = ""; }
            string t = now.ToString(fmt, CultureInfo.CurrentCulture);
            string d = now.ToString("d", CultureInfo.CurrentCulture);
            if (t == timeText && d == dateText && timeSample != "") return;
            timeText = t;
            dateText = d;
            string noYear = NoYearPattern();
            dateShort = now.ToString(noYear, CultureInfo.CurrentCulture);
            if (timeSample == "")
            {
                timeSample = new DateTime(2000, 8, 28, 20, 48, 28).ToString(fmt, CultureInfo.CurrentCulture);
                dateSample = new DateTime(2000, 8, 28).ToString("d", CultureInfo.CurrentCulture);
                dateShortSample = new DateTime(2000, 8, 28).ToString(noYear, CultureInfo.CurrentCulture);
            }
            Layout();
        }

        // Formato de fecha corta del sistema sin el año (p. ej. "dd/MM/yyyy" -> "dd/MM"), respetando el orden regional.
        static string NoYearPattern()
        {
            string p = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
            var sb = new System.Text.StringBuilder();
            foreach (char c in p) if (c != 'y' && c != 'Y' && c != 'g') sb.Append(c);
            string r = sb.ToString().Trim(' ', '/', '.', '-', ',');
            // Un formato de una sola letra sería un formato estándar distinto ("d", "M"): se fuerza como personalizado.
            if (r.Length == 0) return "dd/MM";
            return r.Length == 1 ? "%" + r : r;
        }

        // Fecha que se muestra: sin año en barras verticales, para que los números se vean más grandes.
        string DateShown { get { return Vert ? dateShort : dateText; } }
        string DateSampleShown { get { return Vert ? dateShortSample : dateSample; } }

        public void Refresh() { Invalidate(); }

        int IconSlots { get { return battery.Present ? 3 : 2; } }

        public override void Layout()
        {
            IntPtr sdc = Native.GetDC(IntPtr.Zero);
            int h = S(Config.Height);
            int pad = S(6);
            int cellH = h - S(8);
            int chevW = S(26);
            int sysW = IconSlots * S(26) + S(10);
            bool vert = Vert;

            clockPx = S(12);
            if (vert)
            {
                // El reloj debe caber en el ancho de la barra: se reduce la letra si hace falta.
                int avail = cellH - S(4);
                while (clockPx > S(7))
                {
                    var f = Gdi.Font("Segoe UI", clockPx, 400);
                    int wmax = Math.Max(Math.Max(Gdi.TextWidth(sdc, timeText, f), Gdi.TextWidth(sdc, timeSample, f)),
                                        Math.Max(Gdi.TextWidth(sdc, DateShown, f), Gdi.TextWidth(sdc, DateSampleShown, f)));
                    if (wmax <= avail) break;
                    clockPx--;
                }
            }
            var clockFont = Gdi.Font("Segoe UI", clockPx, 400);
            int tw = Math.Max(Gdi.TextWidth(sdc, timeText, clockFont), Gdi.TextWidth(sdc, timeSample, clockFont));
            int dw = Math.Max(Gdi.TextWidth(sdc, DateShown, clockFont), Gdi.TextWidth(sdc, DateSampleShown, clockFont));
            Native.ReleaseDC(IntPtr.Zero, sdc);

            // Las partes visibles se colocan una tras otra a lo largo de la isla.
            int y = S(4), cur = pad;
            for (int i = 0; i < Count; i++) rects[i] = default(RECT);
            Action<int, int> add = (idx, len) =>
            {
                if (cur > pad) cur += S(2);
                rects[idx] = vert ? new RECT(y, cur, y + cellH, cur + len) : new RECT(cur, y, cur + len, y + cellH);
                cur += len;
            };
            if (Config.ShowChevron) add(Chevron, chevW);
            if (Config.TouchKeyboard) add(Keyboard, S(30));
            add(Sys, sysW);
            if (Config.ShowClock) add(Clock, vert ? S(44) : Math.Max(tw, dw) + S(16));
            if (Config.ShowDesktopCorner) add(Desktop, S(8));
            VisW = cur + pad;
            VisH = h;
            Reposition();

            if (tip != null)
            {
                var list = new List<RECT>();
                foreach (var r in rects) list.Add(new RECT(r.Left + ovL, r.Top + ovT, r.Right + ovL, r.Bottom + ovT));
                tip.Rebuild(list);
            }
        }

        int HitTest(int x, int y)
        {
            for (int i = 0; i < Count; i++) if (rects[i].Contains(x, y)) return i;
            return None;
        }

        protected override bool OnAnimTick(float dt)
        {
            bool more = false;
            for (int i = 0; i < Count; i++)
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
                    if (hit != None) { pressed = hit; pressE[hit].T = 1f; tip.Hide(); StartAnim(); if (OnButtonDown != null && hit <= Clock) OnButtonDown(hit); }
                    return;
                }
                int was = pressed;
                if (was != None) { pressE[was].T = 0f; StartAnim(); }
                pressed = None;
                if (hit == None || hit != was) return;
                if (hit == Chevron) { if (OnChevronClick != null) OnChevronClick(); }
                else if (hit == Sys) { if (OnSysClick != null) OnSysClick(); }
                else if (hit == Keyboard) { if (OnKeyboardClick != null) OnKeyboardClick(); }
                else if (hit == Desktop) { if (OnDesktopClick != null) OnDesktopClick(); }
                else if (OnClockClick != null) OnClockClick();
            }
            else if (button == 2)
            {
                POINT p;
                Native.GetCursorPos(out p);
                if (hit == Sys) LastSysSlot = Math.Max(0, Math.Min(IconSlots - 1, ((Vert ? y - rects[Sys].Top : x - rects[Sys].Left) - S(5)) / S(26)));
                if (hit == Sys && OnSysContext != null) OnSysContext(p.X, p.Y);
                else if ((hit == Chevron || hit == Keyboard || hit == Desktop) && OnChevronContext != null) OnChevronContext(p.X, p.Y);
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
            for (int i = 0; i < Count; i++)
            {
                float fill = hoverE[i].V * 0.16f * (1f - 0.45f * pressE[i].V);
                if (fill <= 0.002f) continue;
                var r = rects[i];
                if (i == Desktop)
                {
                    // Como en Windows: una línea fina que solo se ve al pasar el ratón por la esquina.
                    float a = Math.Min(1f, hoverE[i].V) * 0.6f;
                    if (Vert) Gfx.FillRect(g, r.Left + S(4), r.Top, r.Width - S(8), Math.Max(1, S(1)), Gfx.Argb(a, 255, 255, 255));
                    else Gfx.FillRect(g, r.Left, r.Top + S(4), Math.Max(1, S(1)), r.Height - S(8), Gfx.Argb(a, 255, 255, 255));
                    continue;
                }
                Gfx.FillRound(g, r.Left, r.Top, r.Width, r.Height, radius, Gfx.Argb(fill, 255, 255, 255));
            }
            Gfx.End(g);

            int white = Native.Rgb(245, 245, 245), dim = Native.Rgb(120, 120, 120);
            string face = Gdi.IconFace(dc);

            // Flecha de iconos ocultos
            RECT cr = rects[Chevron];
            int nudge = (int)Math.Round(pressE[Chevron].V * S(1));
            if (rects[Keyboard].Width > 0)
                Gdi.Text(dc, "\uE765", rects[Keyboard], Gdi.Font(face, S(16), 400), white, Gdi.DT_CENTER | Gdi.DT_VCENTER);
            if (cr.Width > 0) Gdi.Text(dc, "", new RECT(cr.Left, cr.Top + nudge, cr.Right, cr.Bottom + nudge), Gdi.Font(face, S(12), 400), white,
                Gdi.DT_CENTER | Gdi.DT_VCENTER);

            // Iconos de sistema (en fila, o en columna si la barra es vertical)
            IntPtr iconFont = Gdi.Font(face, S(16), 400);
            int slot = S(26);
            RECT sr = rects[Sys];
            Func<int, RECT> slotRect = i => Vert
                ? new RECT(sr.Left, sr.Top + S(5) + i * slot, sr.Right, sr.Top + S(5) + (i + 1) * slot)
                : new RECT(sr.Left + S(5) + i * slot, sr.Top, sr.Left + S(5) + (i + 1) * slot, sr.Bottom);

            string netGlyph = net == NetKind.Ethernet ? "\uE839" : "\uE701";
            Gdi.Text(dc, netGlyph, slotRect(0), iconFont, net == NetKind.None ? dim : white, Gdi.DT_CENTER | Gdi.DT_VCENTER);

            var vol = Volume != null ? Volume() : null;
            string volGlyph;
            if (vol == null || !vol.Available) volGlyph = "\uE74F";
            else if (vol.Muted || vol.Level == 0) volGlyph = vol.Muted ? "\uE74F" : "\uE992";
            else if (vol.Level < 34) volGlyph = "\uE993";
            else if (vol.Level < 67) volGlyph = "\uE994";
            else volGlyph = "\uE995";
            Gdi.Text(dc, volGlyph, slotRect(1), iconFont, white, Gdi.DT_CENTER | Gdi.DT_VCENTER);

            if (battery.Present)
            {
                int lvl = Math.Min(10, battery.Percent / 10);
                string gl;
                if (battery.Charging) gl = lvl >= 10 ? "\uE83E" : ((char)(0xE85A + Math.Min(lvl, 8))).ToString();
                else gl = lvl >= 10 ? "\uE83F" : ((char)(0xE850 + lvl)).ToString();
                int col = (!battery.Charging && battery.Percent <= 15) ? Native.Rgb(255, 99, 99) : white;
                Gdi.Text(dc, gl, slotRect(2), iconFont, col, Gdi.DT_CENTER | Gdi.DT_VCENTER);
            }

            // Reloj (dos líneas; alineado a la derecha como en Windows 11, centrado en barras verticales)
            RECT clk = rects[Clock];
            if (clk.Width <= 0) return;
            IntPtr f = Gdi.Font("Segoe UI", clockPx > 0 ? clockPx : S(12), 400);
            int cx0 = Vert ? clk.Left : clk.Left + S(4), cx1 = Vert ? clk.Right : clk.Right - S(8);
            uint align = Vert ? Gdi.DT_CENTER : Gdi.DT_RIGHT;
            int mid = clk.Top + clk.Height / 2;
            Gdi.Text(dc, timeText, new RECT(cx0, clk.Top, cx1, mid + S(1)), f, white, align | Gdi.DT_VCENTER);
            Gdi.Text(dc, DateShown, new RECT(cx0, mid - S(1), cx1, clk.Bottom), f, Native.Rgb(210, 210, 210), align | Gdi.DT_VCENTER);
        }

        public new void Destroy()
        {
            if (tip != null) tip.Destroy();
            base.Destroy();
        }
    }
}
