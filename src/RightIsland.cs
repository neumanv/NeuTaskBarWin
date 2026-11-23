using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace NeuTaskBar
{
    // Isla derecha: flecha de iconos ocultos + teclado táctil + controles multimedia + uso del equipo + iconos de sistema
    // (red, volumen, batería) + reloj y fecha + esquina de mostrar escritorio. Cada parte aparece o no según los ajustes
    // de la barra de tareas de Windows y los de la página Información de la aplicación.
    sealed class RightIsland : Island
    {
        public const int None = -1, Chevron = 0, Sys = 1, Clock = 2, Keyboard = 3, Desktop = 4, Stats = 5, Media = 6;
        const int Count = 7;
        const int KindNet = 0, KindVolume = 1, KindBattery = 2;

        public Action OnChevronClick, OnSysClick, OnClockClick, OnKeyboardClick, OnDesktopClick, OnStatsClick;
        public Action<int> OnButtonDown;
        public Action<int> OnMediaClick;   // 0 anterior, 1 reproducir o pausar, 2 siguiente
        public Action<int, int> OnSysContext, OnClockContext, OnChevronContext;
        public Action<int> OnVolumeWheel;
        public Func<VolumeMonitor> Volume;
        public int LastSysSlot;   // icono de sistema bajo el cursor en el último clic derecho: 0 red, 1 volumen, 2 batería
        public bool Secondary;

        NetKind net = NetKind.None;
        BatteryState battery;
        string timeText = "", dateText = "";
        string timeSample = "", dateSample = "";
        string dateShort = "", dateShortSample = "";   // fecha para barras verticales (sin año)
        string timeVert = "", timeVertSample = "";     // hora para barras verticales (la de 12 horas, sin AM ni PM)
        readonly RECT[] rects = new RECT[Count];
        readonly Ease[] hoverE = new Ease[Count];
        readonly Ease[] pressE = new Ease[Count];
        string clockKey = "";
        int hover = None, pressed = None;
        int clockPx;   // tamaño de letra del reloj (se reduce en barras verticales para que quepa)
        bool clockOneLine;   // barra baja: hora y fecha seguidas en una línea, porque las dos líneas no caben
        int clockDateW;      // ancho reservado a la fecha en ese caso
        // Iconos de sistema a la vista, en orden (red, volumen, batería), y lo que ocupa cada uno a lo largo de la isla.
        readonly List<int> sysKinds = new List<int>();
        readonly List<int> sysLens = new List<int>();
        int percentPx;       // tamaño de letra del porcentaje de batería
        // Uso del equipo: procesador y memoria, en tanto por ciento.
        int cpu, ram;
        int statsPx, statsLabelW, statsValueW;
        bool statsOneLine;
        int mediaSlot = None;   // botón multimedia bajo el cursor
        // Aviso breve que ocupa el sitio del reloj (cambio de volumen, batería).
        string noticeGlyph = "", noticeText = "";
        Ease noticeE;
        long noticeUntil;
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
            if (Movable) return "";
            if (idx == Chevron) return L.T("Mostrar iconos ocultos");
            if (idx == Keyboard) return L.T("Teclado táctil");
            if (idx == Desktop) return L.T("Mostrar escritorio");
            if (idx == Media) return L.T("Controles multimedia");
            if (idx == Stats) return L.T("Procesador: ") + cpu + "%\n" + L.T("Memoria: ") + ram + "%";
            if (idx == Sys)
            {
                var parts = new List<string>();
                if (sysKinds.Contains(KindNet))
                    parts.Add(net == NetKind.Wifi ? L.T("Wi-Fi conectado") : net == NetKind.Ethernet ? L.T("Ethernet conectado") : L.T("Sin conexión a Internet"));
                var vol = Volume != null ? Volume() : null;
                if (sysKinds.Contains(KindVolume) && vol != null && vol.Available)
                    parts.Add(vol.Muted ? L.T("Volumen: silenciado") : L.T("Volumen: ") + vol.Level + "%");
                if (sysKinds.Contains(KindBattery))
                    parts.Add(L.T("Batería: ") + battery.Percent + "%" + (battery.Charging ? L.T(" (cargando)") : battery.AcPower ? L.T(" (conectada)") : ""));
                return string.Join("\n", parts.ToArray());
            }
            return DateTime.Now.ToString("D", L.Culture);
        }

        public void UpdateNetwork(NetKind k) { if (k != net) { net = k; Layout(); } }

        public void UpdateBattery(BatteryState b)
        {
            bool relayout = b.Present != battery.Present || (Config.BatteryPercent && b.Percent != battery.Percent);
            // Aviso al enchufar o desenchufar el cargador y al bajar del 20 % y del 10 %.
            bool notify = b.Present && battery.Present && (b.AcPower != battery.AcPower
                || (!b.Charging && ((battery.Percent > 20 && b.Percent <= 20) || (battery.Percent > 10 && b.Percent <= 10))));
            battery = b;
            if (relayout) Layout(); else Invalidate();
            if (notify) Notify(BatteryGlyph(), b.Percent + "%");
        }

        public void UpdateStats(int cpuPercent, int ramPercent)
        {
            if (cpuPercent == cpu && ramPercent == ram) return;
            cpu = cpuPercent;
            ram = ramPercent;
            Invalidate();
        }

        // Enseña un momento el volumen en el sitio del reloj.
        public void NotifyVolume()
        {
            var vol = Volume != null ? Volume() : null;
            if (vol != null && vol.Available) Notify(VolumeGlyph(vol), vol.Muted ? "0%" : vol.Level + "%");
        }

        void Notify(string glyph, string text)
        {
            if (!Config.Notices || rects[Clock].Width <= 0) return;
            noticeGlyph = glyph;
            noticeText = text;
            noticeE.T = 1f;
            noticeUntil = Stopwatch.GetTimestamp() + (long)(1.6 * Stopwatch.Frequency);
            StartAnim();
            Invalidate();
        }

        // Formato de la hora elegido en Información; con "Mostrar segundos en el reloj" de Windows lleva segundos.
        // En barras verticales la de 12 horas va sin AM ni PM: con ellos no cabe en el ancho de la barra.
        static string FormatTime(DateTime t, bool vert)
        {
            bool sec = Config.ClockSeconds;
            if (Config.ClockTime == 1) return t.ToString(sec ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
            if (Config.ClockTime == 2) return t.ToString((sec ? "h:mm:ss" : "h:mm") + (vert ? "" : " tt"), CultureInfo.InvariantCulture);
            return t.ToString(sec ? "T" : "t", CultureInfo.CurrentCulture);
        }

        // Fecha elegida en Información: la corta de Windows, con el día de la semana delante, o ninguna. En barras
        // verticales va sin año (y solo con el número del día si lleva el de la semana), para que los números se vean
        // más grandes.
        static string FormatDate(DateTime t, bool vert)
        {
            if (Config.ClockDate == 2) return "";
            string noYear = NoYearPattern();
            if (Config.ClockDate == 1)
                return t.ToString("ddd", L.Culture).TrimEnd('.') + " " + t.ToString(vert ? "%d" : noYear, CultureInfo.CurrentCulture);
            return t.ToString(vert ? noYear : "d", CultureInfo.CurrentCulture);
        }

        public void UpdateClock()
        {
            var now = DateTime.Now;
            string key = Config.ClockTime + "|" + Config.ClockDate + "|" + Config.ClockSeconds + "|" + L.Code;
            if (key != clockKey) { clockKey = key; timeSample = ""; }
            string t = FormatTime(now, false), d = FormatDate(now, false);
            if (t == timeText && d == dateText && timeSample != "") return;
            timeText = t;
            dateText = d;
            dateShort = FormatDate(now, true);
            timeVert = FormatTime(now, true);
            if (timeSample == "")
            {
                var sample = new DateTime(2000, 8, 28, 20, 48, 28);
                timeSample = FormatTime(sample, false);
                timeVertSample = FormatTime(sample, true);
                dateSample = FormatDate(sample, false);
                dateShortSample = FormatDate(sample, true);
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

        string TimeShown { get { return Vert ? timeVert : timeText; } }
        string TimeSampleShown { get { return Vert ? timeVertSample : timeSample; } }
        string DateShown { get { return Vert ? dateShort : dateText; } }
        string DateSampleShown { get { return Vert ? dateShortSample : dateSample; } }

        public void Refresh() { Invalidate(); }

        public override void Layout()
        {
            IntPtr sdc = Native.GetDC(IntPtr.Zero);
            int h = BarH;
            int pad = S(6);
            int cellH = h - Inset * 2;
            int chevW = S(26);
            int slot = S(26);
            bool vert = Vert;
            bool hasDate = Config.ClockDate != 2;

            clockPx = S(Config.TextSize);
            if (vert)
            {
                // El reloj debe caber en el ancho de la barra: se reduce la letra si hace falta.
                int avail = cellH - S(4);
                while (clockPx > S(7))
                {
                    var f = Gdi.Font("Segoe UI", clockPx, 400);
                    int wmax = Math.Max(Math.Max(Gdi.TextWidth(sdc, TimeShown, f), Gdi.TextWidth(sdc, TimeSampleShown, f)),
                                        Math.Max(Gdi.TextWidth(sdc, DateShown, f), Gdi.TextWidth(sdc, DateSampleShown, f)));
                    if (wmax <= avail) break;
                    clockPx--;
                }
            }
            var clockFont = Gdi.Font("Segoe UI", clockPx, 400);
            int lineH = Gdi.LineHeight(sdc, clockFont);
            int tw = Math.Max(Gdi.TextWidth(sdc, TimeShown, clockFont), Gdi.TextWidth(sdc, TimeSampleShown, clockFont));
            int dw = hasDate ? Math.Max(Gdi.TextWidth(sdc, DateShown, clockFont), Gdi.TextWidth(sdc, DateSampleShown, clockFont)) : 0;
            // Con la barra pequeña de Windows no caben las dos líneas: la fecha pasa a continuación de la hora.
            clockOneLine = !vert && hasDate && lineH * 2 > cellH + S(2);
            clockDateW = dw;

            // Iconos de sistema elegidos; el de batería se alarga si lleva el porcentaje al lado (debajo, en vertical).
            sysKinds.Clear();
            sysLens.Clear();
            percentPx = vert ? Math.Min(S(10), clockPx) : Math.Max(S(9), S(Config.TextSize) - S(1));
            if (!Secondary && Config.ShowNet) { sysKinds.Add(KindNet); sysLens.Add(slot); }
            if (!Secondary && Config.ShowVolume) { sysKinds.Add(KindVolume); sysLens.Add(slot); }
            if (!Secondary && Config.ShowBattery && battery.Present)
            {
                int extra = 0;
                if (Config.BatteryPercent)
                {
                    var pf = Gdi.Font("Segoe UI", percentPx, 400);
                    extra = vert ? Gdi.LineHeight(sdc, pf) : Gdi.TextWidth(sdc, "100%", pf) + S(2);
                }
                sysKinds.Add(KindBattery);
                sysLens.Add(slot + extra);
            }
            int sysW = S(10);
            foreach (int len in sysLens) sysW += len;

            // Uso del equipo: una línea por dato si caben dos, o los dos seguidos (en vertical, rótulo y valor apilados).
            int nStats = Secondary ? 0 : (Config.ShowCpu ? 1 : 0) + (Config.ShowRam ? 1 : 0);
            statsPx = vert ? Math.Min(S(10), clockPx) : Math.Max(S(9), S(Config.TextSize) - S(1));
            var statsFont = Gdi.Font("Segoe UI", statsPx, 400);
            int statsLineH = Gdi.LineHeight(sdc, statsFont);
            statsLabelW = Math.Max(Gdi.TextWidth(sdc, "CPU", statsFont), Gdi.TextWidth(sdc, "RAM", statsFont));
            statsValueW = Gdi.TextWidth(sdc, "100%", statsFont);
            statsOneLine = !vert && (nStats < 2 || statsLineH * 2 > cellH);
            int statsItemW = statsLabelW + S(5) + statsValueW;
            int statsLen = vert ? nStats * (statsLineH * 2 + S(2)) + S(6)
                : (statsOneLine ? nStats * statsItemW + (nStats - 1) * S(10) : statsItemW) + S(14);
            Native.ReleaseDC(IntPtr.Zero, sdc);

            // Las partes visibles se colocan una tras otra a lo largo de la isla.
            int y = Inset, cur = pad;
            for (int i = 0; i < Count; i++) rects[i] = default(RECT);
            Action<int, int> add = (idx, len) =>
            {
                if (cur > pad) cur += S(2);
                rects[idx] = vert ? new RECT(y, cur, y + cellH, cur + len) : new RECT(cur, y, cur + len, y + cellH);
                cur += len;
            };
            if (!Secondary && Config.ShowChevron) add(Chevron, chevW);
            if (!Secondary && Config.TouchKeyboard) add(Keyboard, S(30));
            if (!Secondary && Config.ShowMedia) add(Media, slot * 3 + S(6));
            if (nStats > 0) add(Stats, statsLen);
            if (sysKinds.Count > 0) add(Sys, sysW);
            if (Config.ShowClock)
                add(Clock, vert ? (hasDate ? 2 : 1) * (lineH + S(2)) + S(8) : (clockOneLine ? tw + S(8) + dw : Math.Max(tw, dw)) + S(16));
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

        // Rectángulo del icono de sistema número i (de los que están a la vista).
        RECT SysSlotRect(int i)
        {
            RECT sr = rects[Sys];
            int off = S(5);
            for (int k = 0; k < i; k++) off += sysLens[k];
            return Vert ? new RECT(sr.Left, sr.Top + off, sr.Right, sr.Top + off + sysLens[i])
                : new RECT(sr.Left + off, sr.Top, sr.Left + off + sysLens[i], sr.Bottom);
        }

        // Icono de sistema (red, volumen o batería) que hay en un punto de su celda.
        int SysKindAt(int x, int y)
        {
            for (int i = 0; i < sysKinds.Count; i++)
                if (SysSlotRect(i).Contains(x, y)) return sysKinds[i];
            if (sysKinds.Count == 0) return KindNet;
            // En los márgenes de la celda vale el icono del extremo más cercano.
            RECT first = SysSlotRect(0);
            return (Vert ? y < first.Top : x < first.Left) ? sysKinds[0] : sysKinds[sysKinds.Count - 1];
        }

        RECT MediaSlotRect(int i)
        {
            RECT mr = rects[Media];
            int slot = S(26), off = S(3) + i * slot;
            return Vert ? new RECT(mr.Left, mr.Top + off, mr.Right, mr.Top + off + slot)
                : new RECT(mr.Left + off, mr.Top, mr.Left + off + slot, mr.Bottom);
        }

        int MediaSlotAt(int x, int y)
        {
            for (int i = 0; i < 3; i++) if (MediaSlotRect(i).Contains(x, y)) return i;
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
            if (noticeE.T > 0f)
            {
                if (Stopwatch.GetTimestamp() > noticeUntil) noticeE.T = 0f;
                more = true;   // el temporizador sigue vivo hasta que el aviso se retira
            }
            more |= noticeE.Step(dt, 14f, 0.01f);
            return more;
        }

        protected override void OnMouseMove(int x, int y)
        {
            int h = HitTest(x, y);
            int slot = h == Media ? MediaSlotAt(x, y) : None;
            if (slot != mediaSlot) { mediaSlot = slot; Invalidate(); }
            if (h == hover) return;
            if (hover != None) hoverE[hover].T = 0f;
            hover = h;
            if (h != None) hoverE[h].T = 1f;
            StartAnim();
        }

        protected override void HideTip() { if (tip != null) tip.Hide(); }

        protected override void OnMouseLeave()
        {
            if (hover != None) hoverE[hover].T = 0f;
            if (pressed != None) pressE[pressed].T = 0f;
            hover = pressed = mediaSlot = None;
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
                else if (hit == Stats) { if (OnStatsClick != null) OnStatsClick(); }
                else if (hit == Media)
                {
                    int slot = MediaSlotAt(x, y);
                    if (slot != None && OnMediaClick != null) OnMediaClick(slot);
                }
                else if (OnClockClick != null) OnClockClick();
            }
            else if (button == 2)
            {
                POINT p;
                Native.GetCursorPos(out p);
                if (hit == Sys) LastSysSlot = SysKindAt(x, y);
                if (hit == Sys && OnSysContext != null) OnSysContext(p.X, p.Y);
                else if (hit != Clock && hit != None && hit != Sys && OnChevronContext != null) OnChevronContext(p.X, p.Y);
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

        static string VolumeGlyph(VolumeMonitor vol)
        {
            if (vol == null || !vol.Available || vol.Muted) return "";
            if (vol.Level == 0) return "";
            if (vol.Level < 34) return "";
            return vol.Level < 67 ? "" : "";
        }

        string BatteryGlyph()
        {
            int lvl = Math.Min(10, battery.Percent / 10);
            if (battery.Charging) return lvl >= 10 ? "" : ((char)(0xE85A + Math.Min(lvl, 8))).ToString();
            return lvl >= 10 ? "" : ((char)(0xE850 + lvl)).ToString();
        }

        protected override void OnPaintContent(IntPtr dc)
        {
            float radius = S(6);
            float veil = Config.HoverEffect == 2 ? 0f : 0.16f;
            IntPtr g = BeginGfx();
            for (int i = 0; i < Count; i++)
            {
                float fill = hoverE[i].V * veil * (1f - 0.45f * pressE[i].V);
                var r = rects[i];
                if (i == Desktop)
                {
                    // Como en Windows: una línea fina que solo se ve al pasar el ratón por la esquina.
                    float a = Math.Min(1f, hoverE[i].V) * 0.6f;
                    if (a <= 0.002f) continue;
                    if (Vert) Gfx.FillRect(g, r.Left + S(4), r.Top, r.Width - S(8), Math.Max(1, S(1)), Veil(a));
                    else Gfx.FillRect(g, r.Left, r.Top + S(4), Math.Max(1, S(1)), r.Height - S(8), Veil(a));
                    continue;
                }
                if (fill <= 0.002f) continue;
                // Los botones multimedia se resaltan de uno en uno.
                if (i == Media) { if (mediaSlot == None) continue; r = MediaSlotRect(mediaSlot); }
                Gfx.FillRound(g, r.Left, r.Top, r.Width, r.Height, radius, Veil(fill));
            }
            Gfx.End(g);

            int white = Ink(245), dim = Ink(120);
            string face = Gdi.IconFace(dc);
            const uint Center = Gdi.DT_CENTER | Gdi.DT_VCENTER;

            // Flecha de iconos ocultos
            RECT cr = rects[Chevron];
            int nudge = (int)Math.Round(pressE[Chevron].V * S(1));
            if (rects[Keyboard].Width > 0)
                Text("", rects[Keyboard], Gdi.Font(face, S(16), 400), white, Center);
            if (cr.Width > 0) Text(Edge == Edge.Top ? "" : Edge == Edge.Left ? "" : Edge == Edge.Right ? "" : "", new RECT(cr.Left, cr.Top + nudge, cr.Right, cr.Bottom + nudge), Gdi.Font(face, S(12), 400), white,
                Center);

            IntPtr iconFont = Gdi.Font(face, S(16), 400);
            int slot = S(26);

            // Controles multimedia: anterior, reproducir o pausar, siguiente.
            if (rects[Media].Width > 0)
            {
                IntPtr mediaFont = Gdi.Font(face, S(13), 400);
                string[] glyphs = { "", "", "" };
                for (int i = 0; i < 3; i++) Text(glyphs[i], MediaSlotRect(i), mediaFont, white, Center);
            }

            // Uso del equipo: rótulo apagado y valor, uno por línea o los dos seguidos.
            RECT st = rects[Stats];
            if (st.Width > 0)
            {
                IntPtr sf = Gdi.Font("Segoe UI", statsPx > 0 ? statsPx : S(11), 400);
                var names = new List<string>();
                var values = new List<string>();
                if (Config.ShowCpu) { names.Add("CPU"); values.Add(cpu + "%"); }
                if (Config.ShowRam) { names.Add("RAM"); values.Add(ram + "%"); }
                int n = names.Count, soft = Ink(170);
                for (int i = 0; i < n; i++)
                {
                    if (Vert)
                    {
                        int lh = (st.Height - S(6)) / (n * 2), top = st.Top + S(3) + i * lh * 2;
                        Text(names[i], new RECT(0, top, VisH, top + lh), sf, soft, Center);
                        Text(values[i], new RECT(0, top + lh, VisH, top + lh * 2), sf, white, Center);
                        continue;
                    }
                    int itemW = statsLabelW + S(5) + statsValueW;
                    RECT line;
                    if (statsOneLine) { int x0 = st.Left + S(7) + i * (itemW + S(10)); line = new RECT(x0, st.Top, x0 + itemW, st.Bottom); }
                    else
                    {
                        int mid = st.Top + st.Height / 2;
                        line = i == 0 ? new RECT(st.Left + S(7), st.Top, st.Left + S(7) + itemW, mid + S(1))
                            : new RECT(st.Left + S(7), mid - S(1), st.Left + S(7) + itemW, st.Bottom);
                    }
                    Text(names[i], line, sf, soft, Gdi.DT_VCENTER);
                    Text(values[i], line, sf, white, Gdi.DT_RIGHT | Gdi.DT_VCENTER);
                }
            }

            // Iconos de sistema (en fila, o en columna si la barra es vertical)
            for (int i = 0; i < sysKinds.Count && rects[Sys].Width > 0; i++)
            {
                RECT r = SysSlotRect(i);
                if (sysKinds[i] == KindNet)
                    Text(net == NetKind.Ethernet ? "" : "", r, iconFont, net == NetKind.None ? dim : white, Center);
                else if (sysKinds[i] == KindVolume)
                    Text(VolumeGlyph(Volume != null ? Volume() : null), r, iconFont, white, Center);
                else
                {
                    int col = (!battery.Charging && battery.Percent <= 15) ? Native.Rgb(255, 99, 99) : white;
                    RECT icon = Vert ? new RECT(r.Left, r.Top, r.Right, r.Top + slot) : new RECT(r.Left, r.Top, r.Left + slot, r.Bottom);
                    Text(BatteryGlyph(), icon, iconFont, col, Center);
                    if (!Config.BatteryPercent) continue;
                    IntPtr pf = Gdi.Font("Segoe UI", percentPx > 0 ? percentPx : S(11), 400);
                    if (Vert) Text(battery.Percent + "%", new RECT(0, r.Top + slot - S(4), VisH, r.Bottom), pf, white, Center);
                    else Text(battery.Percent + "%", new RECT(r.Left + slot - S(2), r.Top, r.Right, r.Bottom), pf, white, Gdi.DT_VCENTER);
                }
            }

            // Reloj (dos líneas; alineado a la derecha como en Windows 11, centrado en barras verticales)
            RECT clk = rects[Clock];
            if (clk.Width <= 0) return;
            IntPtr f = Gdi.Font("Segoe UI", clockPx > 0 ? clockPx : S(12), 400);
            // En vertical el texto dispone de todo el ancho de la isla, por si ni con la letra mínima cabe en la celda.
            int cx0 = Vert ? 0 : clk.Left + S(4), cx1 = Vert ? VisH : clk.Right - S(8);
            uint align = Vert ? Gdi.DT_CENTER : Gdi.DT_RIGHT;
            int dateColor = Ink(210);
            float notice = Math.Max(0f, Math.Min(1f, noticeE.V)), shown = 1f - notice;
            if (notice > 0.01f)
            {
                // Aviso: el icono y su valor, centrados en el sitio del reloj (uno sobre otro en vertical).
                IntPtr nf = Gdi.Font(face, S(14), 400);
                if (Vert)
                {
                    int half = clk.Top + clk.Height / 2;
                    Text(noticeGlyph, new RECT(0, clk.Top, VisH, half), nf, white, Center, notice);
                    Text(noticeText, new RECT(0, half, VisH, clk.Bottom), f, white, Center, notice);
                }
                else
                {
                    int gw = S(20), textW = Gdi.TextWidth(dc, noticeText, f);
                    bool icon = gw + textW <= clk.Width - S(4);   // si no caben los dos, solo el valor
                    int x0 = clk.Left + (clk.Width - (icon ? gw : 0) - textW) / 2;
                    if (icon) Text(noticeGlyph, new RECT(x0, clk.Top, x0 + gw, clk.Bottom), nf, white, Gdi.DT_VCENTER, notice);
                    Text(noticeText, new RECT(x0 + (icon ? gw : 0), clk.Top, clk.Right, clk.Bottom), f, white, Gdi.DT_VCENTER, notice);
                }
            }
            if (Config.ClockDate == 2)
            {
                Text(TimeShown, new RECT(cx0, clk.Top, cx1, clk.Bottom), f, white, align | Gdi.DT_VCENTER, shown);
                return;
            }
            if (clockOneLine)
            {
                Text(TimeShown, new RECT(cx0, clk.Top, cx1 - clockDateW - S(8), clk.Bottom), f, white, align | Gdi.DT_VCENTER, shown);
                Text(DateShown, new RECT(cx1 - clockDateW, clk.Top, cx1, clk.Bottom), f, dateColor, align | Gdi.DT_VCENTER, shown);
                return;
            }
            int middle = clk.Top + clk.Height / 2;
            Text(TimeShown, new RECT(cx0, clk.Top, cx1, middle + S(1)), f, white, align | Gdi.DT_VCENTER, shown);
            Text(DateShown, new RECT(cx0, middle - S(1), cx1, clk.Bottom), f, dateColor, align | Gdi.DT_VCENTER, shown);
        }

        public new void Destroy()
        {
            if (tip != null) tip.Destroy();
            base.Destroy();
        }
    }
}
