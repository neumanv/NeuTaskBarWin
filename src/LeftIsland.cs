using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace NeuTaskBar
{
    // Isla izquierda: botón de inicio + apps fijadas + apps abiertas. Se ensancha según el número de apps
    // y anima la entrada/salida de iconos, el hover, la pulsación, el rebote al lanzar y el indicador.
    sealed class LeftIsland : Island
    {
        sealed class Cell
        {
            public AppItem Item;
            public string Id;
            public Ease X, W, Appear, Hover, Press, Active, IndW, IndOn;
            public bool Dying, Launching, Dragging;
            public long LaunchStart;
        }

        public readonly IconImages Icons = new IconImages();
        public int MaxWidth = 100000;
        public int SearchMode = 1;      // 1 solo icono, 2 cuadro de búsqueda, 3 icono y etiqueta (ajuste de Windows)
        public bool Small;              // botones más pequeños
        // true si con el tamaño normal de botón los iconos no caben (para "botones pequeños cuando la barra esté llena").
        public bool FullAtNormalSize { get; private set; }

        public Action<string> OnToolClick;
        public Action OnStartClick, OnStartContext, OnStartDown;
        public Action<AppItem> OnItemClick, OnItemMiddle;
        public Action<AppItem, int, int> OnItemContext;
        public Action<int, int> OnBlankContext;
        public Action<string[]> OnFilesDropped;
        public Action<AppItem, RECT> OnHover;   // item bajo el ratón (null si ninguno) y su rectángulo en pantalla
        public Action<List<AppItem>> OnReorder; // nuevo orden tras arrastrar un icono

        readonly Cell start = new Cell();
        List<Cell> tools = new List<Cell>();
        List<Cell> cells = new List<Cell>();
        readonly List<AppItem> tipItems = new List<AppItem>();
        Cell hoverCell, pressCell;
        Ease visW;
        bool initialized;
        float startW, cellH, sepX;
        Tip tip;
        bool leftDown, dragging;
        Cell dragCell;
        int dragStartX;
        float dragOffset, slotsX0, slotsX1;
        bool labelsShown;               // las etiquetas caben y se dibujan

        public LeftIsland() : base(false)
        {
            start.Appear = new Ease(1);
            start.Id = "start";
        }

        public void Init()
        {
            Create(true);
            Tip.InitControls();
            tip = new Tip(Hwnd, TipText);
        }

        public void SetTools(string[] ids)
        {
            var list = new List<Cell>();
            foreach (var id in ids)
            {
                Cell c = tools.Find(t => t.Id == id) ?? new Cell { Id = id, Appear = new Ease(1) };
                list.Add(c);
            }
            tools = list;
            if (initialized) LayoutInternal(true);
        }

        static string ToolName(string id)
        {
            return id == "search" ? "Buscar" : id == "taskview" ? "Vista de tareas" : "Widgets";
        }

        string TipText(int idx)
        {
            if (idx == 0) return "Inicio";
            idx--;
            if (idx < tools.Count) return ToolName(tools[idx].Id);
            idx -= tools.Count;
            if (idx < 0 || idx >= tipItems.Count) return "";
            var it = tipItems[idx];
            // Las apps abiertas muestran la vista previa de ventanas en lugar del tooltip.
            return it == null || it.Running ? "" : it.Name;
        }

        // ------------------------------------------------------------------ modelo

        public void SetItems(List<AppItem> list)
        {
            bool first = !initialized;
            initialized = true;

            var oldById = new Dictionary<string, Cell>();
            foreach (var c in cells) oldById[c.Id] = c;

            var result = new List<Cell>();
            var keep = new HashSet<Cell>();
            foreach (var it in list)
            {
                Cell c;
                if (oldById.TryGetValue(it.Id, out c)) { oldById.Remove(it.Id); }
                else
                {
                    c = new Cell { Id = it.Id };
                    if (first) { c.Appear = new Ease(1); }
                    else { c.Appear = new Ease(0); c.Appear.T = 1; c.W = new Ease(0); }
                    c.IndW = new Ease(0);
                    c.X = new Ease(-1000f);
                }
                if (c.Dying) { c.Dying = false; }
                c.Appear.T = 1;
                c.Item = it;
                if (it.Running) c.Launching = false;
                c.Active.T = it.Active ? 1f : 0f;
                c.IndOn.T = it.Running ? 1f : 0f;
                result.Add(c);
                keep.Add(c);
            }

            // Las celdas que desaparecen se mantienen mientras se encogen, en su posición anterior.
            Cell prev = null;
            foreach (var c in cells)
            {
                if (keep.Contains(c)) { prev = c; continue; }
                c.Dying = true;
                c.Appear.T = 0;
                c.IndOn.T = 0;
                int idx = prev == null ? 0 : result.IndexOf(prev) + 1;
                result.Insert(idx, c);
                prev = c;
            }
            cells = result;
            LayoutInternal(first);
        }

        public AppItem[] Items
        {
            get { return tipItems.ToArray(); }
        }

        public override void Layout() { LayoutInternal(true); }

        // Ancho normal de una celda de app y de una celda con etiqueta (px).
        float NormalCellW { get { return S(Config.Height) - S(8); } }
        float SmallCellW { get { return Math.Min(NormalCellW, S(32)); } }

        // Ancho de la celda de Buscar según el modo elegido en Windows (en barras verticales, siempre solo icono).
        float ToolWidth(string id, float unit)
        {
            if (id != "search" || Vert) return unit;
            if (SearchMode == 2) return Math.Max(unit, S(190));
            if (SearchMode == 3) return Math.Max(unit, S(96));
            return unit;
        }

        float HeadWidth(float unit, float gap)
        {
            float head = unit;
            foreach (var t in tools) head += gap + ToolWidth(t.Id, unit);
            return head;
        }

        // ¿Caben "labeled" botones con etiqueta y "plain" sin ella sin llenar la barra?
        // (para "Combinar botones y ocultar etiquetas: cuando la barra esté llena").
        public bool FitsUncombined(int labeled, int plain)
        {
            float pad = S(6), gap = S(2), sep = S(12);
            float cw = Small ? SmallCellW : NormalCellW;
            int n = labeled + plain;
            if (n == 0) return true;
            float lw = Vert ? cw : S(110);
            return pad * 2 + HeadWidth(NormalCellW, gap) + sep + plain * cw + labeled * lw + (n - 1) * gap <= MaxWidth;
        }

        bool HasLabel(Cell c) { return c.Item != null && !string.IsNullOrEmpty(c.Item.Label); }

        void LayoutInternal(bool snap)
        {
            float h = S(Config.Height);
            float pad = S(6), gap = S(2), sep = S(12);
            cellH = h - S(8);
            startW = cellH;
            int n = 0, nLab = 0;
            foreach (var c in cells) if (!c.Dying) { n++; if (HasLabel(c) && !Vert) nLab++; }

            float head = HeadWidth(startW, gap);
            float fixedW = pad + head + pad + (n > 0 ? sep + (n - 1) * gap : 0);
            FullAtNormalSize = fixedW + n * NormalCellW > MaxWidth;

            float cellW = Small ? SmallCellW : cellH;
            // Etiquetas: ancho de Windows 11; se estrechan si no caben y, si ni así, se quitan.
            float labW = S(156);
            labelsShown = nLab > 0;
            if (nLab > 0 && fixedW + (n - nLab) * cellW + nLab * labW > MaxWidth)
            {
                labW = (MaxWidth - fixedW - (n - nLab) * cellW) / nLab;
                if (labW < S(72)) { labelsShown = false; labW = cellW; }
            }
            if (!labelsShown) { nLab = 0; labW = cellW; }
            float total = fixedW + (n - nLab) * cellW + nLab * labW;
            if (total > MaxWidth && n > 0)
            {
                cellW = Math.Max(S(24), (MaxWidth - fixedW) / n);
                labW = cellW;
                total = fixedW + n * cellW;
            }

            start.X = new Ease(pad);
            start.W = new Ease(startW);
            float tx = pad + startW;
            for (int i = 0; i < tools.Count; i++)
            {
                float tw = ToolWidth(tools[i].Id, startW);
                tools[i].X = new Ease(tx + gap);
                tools[i].W = new Ease(tw);
                tx += gap + tw;
            }
            sepX = pad + head + sep / 2f;

            float x = pad + head + sep;
            slotsX0 = x;
            tipItems.Clear();
            foreach (var c in cells)
            {
                float tw = c.Dying ? 0f : (labelsShown && HasLabel(c) ? labW : cellW);
                c.X.T = x;
                c.W.T = tw;
                if (c != dragCell && (snap || c.X.V < -500f))
                {
                    c.X.V = x;
                    if (snap) { c.W.V = tw; if (!c.Dying) c.Appear.V = 1f; c.Active.V = c.Active.T; c.IndOn.V = c.IndOn.T; }
                }
                c.IndW.T = IndicatorWidth(c);
                if (snap) c.IndW.V = c.IndW.T;
                if (!c.Dying) { x += tw + gap; tipItems.Add(c.Item); }
            }
            slotsX1 = x - gap;
            if (snap) cells.RemoveAll(c => c.Dying);

            visW.T = total;
            if (snap || visW.V <= 0f) visW.V = total;
            VisH = (int)h;
            VisW = (int)Math.Round(visW.V);
            Reposition();
            RebuildTips();
            StartAnim();
            Invalidate();
        }

        float IndicatorWidth(Cell c)
        {
            var it = c.Item;
            if (it == null) return 0f;
            if (it.Active || it.Flashing) return S(16);
            return it.Windows.Count > 1 ? S(10) : S(6);
        }

        void RebuildTips()
        {
            if (tip == null) return;
            var rects = new List<RECT>();
            rects.Add(ClientRect(start.X.V, start.W.V));
            foreach (var t in tools) rects.Add(ClientRect(t.X.V, t.W.V));
            foreach (var c in cells)
            {
                if (c.Dying) continue;
                rects.Add(ClientRect(c.X.T, c.W.T));
            }
            tip.Rebuild(rects);
        }

        // Rectángulo de una celda (u, ancho a lo largo de la isla) en coordenadas de cliente de la ventana.
        RECT ClientRect(float u, float du)
        {
            RECT r = MapRectI(u, S(4), du, cellH);
            return new RECT(r.Left + ovL, r.Top + ovT, r.Right + ovL, r.Bottom + ovT);
        }

        // Dibujo en coordenadas lógicas (u a lo largo, v a lo ancho): se transponen en barras verticales.
        void FillRoundL(IntPtr g, float u, float v, float du, float dv, float r, uint argb)
        {
            float x, y, w, h;
            MapRect(u, v, du, dv, out x, out y, out w, out h);
            Gfx.FillRound(g, x, y, w, h, r, argb);
        }

        void FillRectL(IntPtr g, float u, float v, float du, float dv, uint argb)
        {
            float x, y, w, h;
            MapRect(u, v, du, dv, out x, out y, out w, out h);
            Gfx.FillRect(g, x, y, w, h, argb);
        }

        void ImageL(IntPtr g, IconImage img, float u, float v, float size, float alpha)
        {
            float x, y, w, h;
            MapRect(u, v, size, size, out x, out y, out w, out h);
            Gfx.DrawImage(g, img, x, y, size, size, alpha);
        }

        // true si el borde de pantalla queda hacia v grande (abajo o derecha): ahí va el indicador de la app.
        bool EdgeFar { get { return Edge == Edge.Bottom || Edge == Edge.Right; } }

        // ------------------------------------------------------------------ animación

        protected override bool OnAnimTick(float dt)
        {
            bool more = false;
            more |= StepCell(start, dt);
            foreach (var t in tools) more |= StepCell(t, dt);
            for (int i = cells.Count - 1; i >= 0; i--)
            {
                var c = cells[i];
                more |= StepCell(c, dt);
                if (c.Dying && c.W.V <= 0.5f && c.Appear.V <= 0.02f) cells.RemoveAt(i);
                if (c.Launching)
                {
                    if (Stopwatch.GetTimestamp() - c.LaunchStart > 10L * Stopwatch.Frequency) c.Launching = false;
                    else more = true;
                }
            }
            if (visW.Step(dt, 14f, 0.5f))
            {
                int w = (int)Math.Round(visW.V);
                if (w != VisW) { VisW = w; Reposition(); }
                more = true;
            }
            return more;
        }

        static bool StepCell(Cell c, float dt)
        {
            bool m = false;
            if (!c.Dragging) m |= c.X.Step(dt, 16f, 0.25f);
            m |= c.W.Step(dt, 16f, 0.25f);
            m |= c.Appear.Step(dt, 13f, 0.005f);
            m |= c.Hover.Step(dt, c.Hover.T > c.Hover.V ? 28f : 12f, 0.005f);
            m |= c.Press.Step(dt, c.Press.T > c.Press.V ? 40f : 16f, 0.005f);
            m |= c.Active.Step(dt, 14f, 0.005f);
            m |= c.IndW.Step(dt, 16f, 0.25f);
            m |= c.IndOn.Step(dt, 14f, 0.005f);
            return m;
        }

        // ------------------------------------------------------------------ ratón

        Cell HitTest(int px, int py)
        {
            int x, y;
            Unmap(px, py, out x, out y);
            if (y < S(4) || y >= S(4) + cellH) return null;
            if (x >= start.X.V && x < start.X.V + start.W.V) return start;
            foreach (var t in tools)
                if (x >= t.X.V && x < t.X.V + t.W.V) return t;
            foreach (var c in cells)
                if (!c.Dying && x >= c.X.V && x < c.X.V + c.W.V) return c;
            return null;
        }

        // ---- reordenar arrastrando ----

        void DragMove(int x)
        {
            if (!dragging)
            {
                if (Math.Abs(x - dragStartX) < S(6)) return;
                dragging = true;
                dragCell = pressCell;
                dragCell.Dragging = true;
                dragOffset = dragStartX - dragCell.X.V;
                Native.SetCapture(Hwnd);
                tip.Hide();
                if (OnHover != null) OnHover(null, default(RECT));
            }

            var living = new List<Cell>();
            int pinned = 0;
            foreach (var c in cells)
            {
                if (c.Dying) continue;
                living.Add(c);
                if (c.Item != null && c.Item.Pinned) pinned++;
            }
            int n = living.Count;
            float nx = x - dragOffset;
            nx = Math.Max(slotsX0, Math.Min(nx, slotsX1 - dragCell.W.V));
            dragCell.X.V = nx;

            // Posición entre las demás: cuántas quedan con su centro antes del centro del icono arrastrado.
            float centre = nx + dragCell.W.V / 2f;
            int rank = 0;
            foreach (var c in living) if (c != dragCell && c.X.T + c.W.T / 2f < centre) rank++;
            rank = Math.Max(0, Math.Min(n - 1, rank));
            // Las fijadas solo se reordenan entre sí, igual que las no fijadas.
            if (dragCell.Item != null && dragCell.Item.Pinned) rank = Math.Min(rank, Math.Max(0, pinned - 1));
            else rank = Math.Max(rank, Math.Min(pinned, n - 1));

            int current = living.IndexOf(dragCell);
            if (rank != current)
            {
                living.Remove(dragCell);
                cells.Remove(dragCell);
                int at = rank >= living.Count ? cells.IndexOf(living[living.Count - 1]) + 1 : cells.IndexOf(living[rank]);
                cells.Insert(at, dragCell);
                LayoutInternal(false);
            }
            Invalidate();
        }

        void EndDrag()
        {
            dragging = false;
            if (dragCell != null) dragCell.Dragging = false;
            Native.ReleaseCapture();
            if (pressCell != null) { pressCell.Press.T = 0f; pressCell = null; }
            dragCell = null;
            LayoutInternal(false);
            if (OnReorder != null)
            {
                var order = new List<AppItem>();
                foreach (var c in cells) if (!c.Dying && c.Item != null) order.Add(c.Item);
                OnReorder(order);
            }
        }

        protected override void OnMouseMove(int px, int py)
        {
            int x, y;
            Unmap(px, py, out x, out y);
            if (leftDown && pressCell != null && pressCell != start && pressCell.Item != null)
            {
                DragMove(x);
                if (dragging) return;
            }
            var h = HitTest(px, py);
            if (h == hoverCell) return;
            if (hoverCell != null) hoverCell.Hover.T = 0f;
            hoverCell = h;
            if (h != null) h.Hover.T = 1f;
            StartAnim();
            if (OnHover != null)
            {
                if (h != null && h != start && h.Item != null) OnHover(h.Item, CellScreenRect(h));
                else OnHover(null, default(RECT));
            }
        }

        RECT CellScreenRect(Cell c)
        {
            RECT w;
            Native.GetWindowRect(Hwnd, out w);
            RECT r = ClientRect(c.X.V, c.W.V);
            return new RECT(w.Left + r.Left, w.Top + r.Top, w.Left + r.Right, w.Top + r.Bottom);
        }

        protected override void OnMouseLeave()
        {
            if (hoverCell != null) hoverCell.Hover.T = 0f;
            if (pressCell != null) pressCell.Press.T = 0f;
            hoverCell = pressCell = null;
            StartAnim();
            if (OnHover != null) OnHover(null, default(RECT));
        }

        protected override void OnMouseButton(int button, bool down, int px, int py)
        {
            int x, y;
            Unmap(px, py, out x, out y);
            var hit = HitTest(px, py);
            if (button == 0)
            {
                if (down)
                {
                    leftDown = true;
                    dragStartX = x;
                    if (hit != null) { pressCell = hit; hit.Press.T = 1f; tip.Hide(); StartAnim(); if (hit == start && OnStartDown != null) OnStartDown(); }
                    return;
                }
                leftDown = false;
                if (dragging) { EndDrag(); return; }
                var was = pressCell;
                if (was != null) { was.Press.T = 0f; StartAnim(); }
                pressCell = null;
                if (hit == null || hit != was) return;
                if (hit == start) { if (OnStartClick != null) OnStartClick(); return; }
                if (hit.Item == null) { if (OnToolClick != null) OnToolClick(hit.Id); return; }
                BeginLaunchBounce(hit);
                if (OnItemClick != null && hit.Item != null) OnItemClick(hit.Item);
            }
            else if (button == 1)
            {
                if (hit != null && hit != start && hit.Item != null)
                {
                    BeginLaunchBounce(hit, true);
                    if (OnItemMiddle != null) OnItemMiddle(hit.Item);
                }
            }
            else if (button == 2)
            {
                POINT p;
                Native.GetCursorPos(out p);
                if (hit == start) { if (OnStartContext != null) OnStartContext(); }
                else if (hit != null && hit.Item != null)
                {
                    var cr = CellScreenRect(hit);
                    if (OnItemContext != null) OnItemContext(hit.Item, (cr.Left + cr.Right) / 2, (cr.Top + cr.Bottom) / 2);
                }
                else if (OnBlankContext != null) OnBlankContext(p.X, p.Y);
            }
        }

        // Al lanzar una app que aún no tiene ventana, el icono rebota hasta que aparece.
        void BeginLaunchBounce(Cell c, bool force)
        {
            if (c.Item == null || (c.Item.Running && !force)) return;
            c.Launching = true;
            c.LaunchStart = Stopwatch.GetTimestamp();
            StartAnim();
        }

        void BeginLaunchBounce(Cell c) { BeginLaunchBounce(c, false); }

        protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_NOTIFY)
            {
                if (tip != null && tip.HandleNotify(lParam)) return IntPtr.Zero;
            }
            else if (msg == 0x0215) // WM_CAPTURECHANGED: si se pierde la captura a mitad de arrastre, se termina
            {
                if (dragging) { leftDown = false; EndDrag(); }
            }
            else if (msg == Native.WM_DROPFILES)
            {
                uint count = Native.DragQueryFileW(wParam, 0xFFFFFFFF, null, 0);
                var files = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    var sb = new StringBuilder(520);
                    Native.DragQueryFileW(wParam, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
                Native.DragFinish(wParam);
                if (OnFilesDropped != null && files.Count > 0) OnFilesDropped(files.ToArray());
                return IntPtr.Zero;
            }
            return base.WndProc(msg, wParam, lParam);
        }

        // ------------------------------------------------------------------ dibujo

        struct Fallback { public RECT R; public string Name; }

        protected override void OnPaintContent(IntPtr dc)
        {
            float top = S(4);
            float radius = S(6);
            var fallbacks = new List<Fallback>();
            var labels = new List<Fallback>();
            IntPtr g = Gfx.Begin(dc);

            DrawStart(g, top, radius);
            var glyphs = new List<Fallback>();
            foreach (var t in tools) DrawTool(g, t, top, radius, glyphs, labels);

            if (cells.Count > 0)
                FillRectL(g, sepX, S(14), Math.Max(1, S(1) * 0.9f), S(Config.Height) - S(28), Gfx.Argb(0.22f, 255, 255, 255));

            float px = Icons.Px;
            long now = Stopwatch.GetTimestamp();
            var drawOrder = new List<Cell>(cells);
            if (dragCell != null && drawOrder.Remove(dragCell)) drawOrder.Add(dragCell);   // el icono arrastrado va encima
            foreach (var c in drawOrder)
            {
                float cw = c.W.V;
                if (cw < 1f) continue;
                var it = c.Item;
                float appear = Math.Max(0f, Math.Min(1f, c.Appear.V));
                float ease = 1f - (1f - appear) * (1f - appear);
                float box = c.X.V;

                float fill = Math.Max(c.Active.V * 0.11f, c.Hover.V * 0.16f) * (1f - 0.45f * c.Press.V);
                FillRoundL(g, box, top, cw, cellH, radius, Gfx.Argb(fill * appear, 255, 255, 255));

                float scale = (0.55f + 0.45f * ease) * (1f - 0.14f * c.Press.V);
                float size = px * scale;
                // Con etiqueta: icono a la izquierda y título de la ventana a continuación.
                bool labeled = labelsShown && HasLabel(c) && !Vert && cw > cellH + S(24);
                float iconSlot = labeled ? Math.Min(cw, cellH) : cw;
                float ix = box + (iconSlot - size) / 2f;
                if (labeled && appear > 0.6f)
                {
                    RECT lr = new RECT((int)(box + iconSlot - S(2)), (int)top, (int)(box + cw - S(8)), (int)(top + cellH));
                    if (lr.Width > S(12)) labels.Add(new Fallback { R = lr, Name = it.Label });
                }
                // El icono se separa del indicador (que va hacia el borde de pantalla) y rebota alejándose de él.
                float away = EdgeFar ? -1f : 1f;
                float iy = top + (cellH - size) / 2f + away * S(2);
                if (c.Launching)
                {
                    double t = (now - c.LaunchStart) / (double)Stopwatch.Frequency;
                    double p = (t % 0.9) / 0.9;
                    iy += away * (float)(4.0 * p * (1.0 - p)) * S(9);
                }
                if (it != null && it.Image != null) ImageL(g, it.Image, ix, iy, size, ease);
                else if (appear > 0.4f)
                {
                    FillRoundL(g, ix, iy, size, size, S(5), Gfx.Argb(0.27f * ease, 255, 255, 255));
                    fallbacks.Add(new Fallback { R = MapRectI(ix, iy, size, size), Name = it == null ? "" : it.Name });
                }

                if (c.IndOn.V > 0.01f && it != null)
                {
                    float bw = Math.Max(2f, c.IndW.V);
                    float bh = Math.Max(2f, S(3));
                    float bx = box + (iconSlot - bw) / 2f;
                    float by = EdgeFar ? top + cellH - bh - S(2) : top + S(2);
                    float a = c.Active.V;
                    int r = (int)(150 + (76 - 150) * a), gg = (int)(150 + (194 - 150) * a), b = (int)(150 + (255 - 150) * a);
                    if (it.Flashing) { r = 255; gg = 176; b = 0; }
                    FillRoundL(g, bx, by, bw, bh, bh / 2f, Gfx.Argb(c.IndOn.V * appear, r, gg, b));
                }
            }
            Gfx.End(g);

            foreach (var f in glyphs)
                Gdi.Text(dc, f.Name, f.R, Gdi.Font(IsWin11 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", (int)S(16), 400), Native.Rgb(255, 255, 255), Gdi.DT_CENTER | Gdi.DT_VCENTER);

            foreach (var f in labels)
                Gdi.Text(dc, f.Name, f.R, Gdi.Font("Segoe UI", S(12), 400), Native.Rgb(245, 245, 245), Gdi.DT_VCENTER | Gdi.DT_END_ELLIPSIS);

            foreach (var f in fallbacks)
            {
                string letter = string.IsNullOrEmpty(f.Name) ? "?" : f.Name.Substring(0, 1).ToUpperInvariant();
                Gdi.Text(dc, letter, f.R, Gdi.Font("Segoe UI", S(14), 600), Native.Rgb(240, 240, 240), Gdi.DT_CENTER | Gdi.DT_VCENTER);
            }
        }

        void DrawTool(IntPtr g, Cell c, float top, float radius, List<Fallback> glyphs, List<Fallback> labels)
        {
            float x = c.X.V, w = c.W.V;
            float fill = c.Hover.V * 0.16f * (1f - 0.45f * c.Press.V);
            float scale = 1f - 0.12f * c.Press.V;
            RECT pr = MapRectI(x, top, w, cellH);   // celda en coordenadas físicas (el glifo no se transpone)
            if (c.Id == "search" && w > cellH + S(8))
            {
                // Buscar como cuadro (píldora siempre visible) o como icono con etiqueta.
                bool boxed = SearchMode == 2;
                float inset = boxed ? S(4) : 0f;
                float baseFill = boxed ? 0.10f : 0f;
                Gfx.FillRound(g, pr.Left, pr.Top + inset, pr.Width, pr.Height - inset * 2, boxed ? (pr.Height - inset * 2) / 2f : radius,
                    Gfx.Argb(Math.Max(baseFill, fill + baseFill * 0.5f), 255, 255, 255));
                int gw = (int)Math.Min(cellH, S(40));
                glyphs.Add(new Fallback { R = new RECT(pr.Left, pr.Top, pr.Left + gw, pr.Bottom), Name = "\uE721" });
                labels.Add(new Fallback { R = new RECT(pr.Left + gw - S(2), pr.Top, pr.Right - S(8), pr.Bottom), Name = "Buscar" });
                return;
            }
            FillRoundL(g, x, top, w, cellH, radius, Gfx.Argb(fill, 255, 255, 255));
            if (c.Id == "widgets")
            {
                float u = S(7) * scale, gp = Math.Max(1f, S(2)) * scale;
                float x0 = pr.Left + (pr.Width - (u * 2 + gp)) / 2f, y0 = pr.Top + (pr.Height - (u * 2 + gp)) / 2f;
                uint col = Gfx.Argb(1f, 76, 194, 255);
                Gfx.FillRound(g, x0, y0, u, u * 2 + gp, S(2), col);
                Gfx.FillRound(g, x0 + u + gp, y0, u, u, S(2), col);
                Gfx.FillRound(g, x0 + u + gp, y0 + u + gp, u, u, S(2), Gfx.Argb(0.8f, 255, 255, 255));
            }
            else
            {
                glyphs.Add(new Fallback { R = pr, Name = c.Id == "search" ? "" : "" });
            }
        }

        void DrawStart(IntPtr g, float top, float radius)
        {
            float x = start.X.V, w = start.W.V;
            float fill = start.Hover.V * 0.16f * (1f - 0.45f * start.Press.V);
            FillRoundL(g, x, top, w, cellH, radius, Gfx.Argb(fill, 255, 255, 255));

            float scale = 1f - 0.12f * start.Press.V;
            float sq = S(8) * scale, gp = Math.Max(1f, S(2)) * scale;
            float total = sq * 2 + gp;
            RECT pr = MapRectI(x, top, w, cellH);
            float x0 = pr.Left + (pr.Width - total) / 2f, y0 = pr.Top + (pr.Height - total) / 2f;
            uint col = Gfx.Argb(1f, 76, 194, 255);
            for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                    Gfx.FillRound(g, x0 + cx * (sq + gp), y0 + cy * (sq + gp), sq, sq, S(1), col);
        }

        public new void Destroy()
        {
            if (tip != null) tip.Destroy();
            Icons.Clear();
            base.Destroy();
        }
    }
}
