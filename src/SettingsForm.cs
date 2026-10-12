using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace NeuTaskBar
{
    // Punto de entrada de la aplicación (ventana principal). Vive en su propio proceso: el motor de la barra no carga WinForms.
    static class SettingsApp
    {
        public static int Run()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\NeuTaskBar.Settings", out created))
            {
                if (!created)
                {
                    IntPtr h = Native.FindMainWindow();
                    if (h != IntPtr.Zero)
                    {
                        Native.ShowWindow(h, Native.SW_RESTORE);
                        Native.SetForegroundWindow(h);
                    }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Config.Load();
                Config.RegisterUninstall();
                StartMenu.Create();
                Application.Run(new MainForm());
            }
            return 0;
        }
    }

    enum EngineState { Off, Paused, On }

    static class Engine
    {
        public static EngineState Query()
        {
            if (Native.FindWindowW(App.HostClass, null) == IntPtr.Zero) return EngineState.Off;
            return ShellTaskbar.IsHiddenByUs() ? EngineState.On : EngineState.Paused;
        }

        public static void Start()
        {
            try
            {
                var psi = new ProcessStartInfo(Config.ExePath, "--engine");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { }
        }

        public static void Resume()
        {
            IntPtr h = Native.FindWindowW(App.HostClass, null);
            if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_RESUME, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Stop()
        {
            IntPtr h = Native.FindWindowW(App.HostClass, null);
            if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // Abre o cierra en la barra el modo en el que las islas se arrastran con el ratón.
        public static void SetMoveMode(bool on)
        {
            IntPtr h = Native.FindWindowW(App.HostClass, null);
            if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_MOVE, new IntPtr(on ? 1 : 0), IntPtr.Zero);
        }

        // Avisa a la barra en marcha de que config.ini ha cambiado, para que lo aplique sin reiniciar.
        public static void ReloadConfig()
        {
            IntPtr h = Native.FindWindowW(App.HostClass, null);
            if (h != IntPtr.Zero) Native.PostMessageW(h, App.WM_APP_CONFIG, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // Página de la ventana principal. Para añadir opciones nuevas: crear una clase derivada y registrarla con MainForm.AddPage.
    abstract class Page : Control
    {
        protected readonly string title, subtitle;
        protected int ContentLeft { get { return Ui.S(40); } }
        protected int ContentWidth { get { return Math.Max(Ui.S(300), Math.Min(Width - Ui.S(80), Ui.S(760))); } }
        protected int HeaderHeight { get { return title.Length == 0 ? Ui.S(36) : (subtitle.Length == 0 ? Ui.S(88) : Ui.S(116)); } }

        protected Page(string title, string subtitle)
        {
            this.title = title;
            this.subtitle = subtitle;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Dock = DockStyle.Fill;
        }

        // Estado de la barra de tareas (lo recibe de la ventana principal).
        public virtual void UpdateEngine(EngineState state, bool busy, string busyText) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            if (title.Length == 0) return;
            using (var f = Ui.Font(30, true))
                Ui.Text(g, title, f, Ui.Ink, new Rectangle(ContentLeft, Ui.S(30), ContentWidth, Ui.S(40)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (subtitle.Length > 0)
                using (var f = Ui.Font(14, false))
                    Ui.Text(g, subtitle, f, Ui.Muted, new Rectangle(ContentLeft, Ui.S(74), ContentWidth, Ui.S(22)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    // Tarjeta de bienvenida con el botón principal.
    sealed class HeroCard : Control
    {
        public readonly PillButton Button = new PillButton();
        public Image Logo;
        public string Status = "";
        public Color StatusColor = Ui.Faint;

        public HeroCard()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Button.BackColor = Ui.CardFill;
            Controls.Add(Button);
        }

        public int PreferredHeight { get { return Ui.S(372); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int bw = Ui.S(320), bh = Ui.S(54);
            Button.SetBounds((Width - bw) / 2, Ui.S(290), bw, bh);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            Ui.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f), Ui.S(12), Ui.CardFill, Ui.Border);
            int W = Width;

            if (Logo != null)
            {
                float lh = Ui.S(100), lw = lh * Logo.Width / Logo.Height;
                g.DrawImage(Logo, (W - lw) / 2f, Ui.S(30), lw, lh);
            }
            using (var f = Ui.Font(30, true))
                Ui.Text(g, L.T("Bienvenido a NeuTaskBar"), f, Ui.Ink, new Rectangle(0, Ui.S(146), W, Ui.S(40)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var f = Ui.Font(15, false))
            {
                Ui.Text(g, L.T("Personaliza tu barra de tareas de Windows con un diseño de dos islas:"), f, Ui.Muted,
                    new Rectangle(0, Ui.S(192), W, Ui.S(24)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Ui.Text(g, L.T("tus aplicaciones a la izquierda y el sistema a la derecha."), f, Ui.Muted,
                    new Rectangle(0, Ui.S(216), W, Ui.S(24)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (var f = Ui.Font(14, false))
            {
                var size = TextRenderer.MeasureText(g, Status, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                float d = Ui.S(10), total = d + Ui.S(10) + size.Width;
                float x0 = (W - total) / 2f, y = Ui.S(254);
                using (var b = new SolidBrush(StatusColor)) g.FillEllipse(b, x0, y + (size.Height - d) / 2f, d, d);
                Ui.Text(g, Status, f, Ui.Ink, new Rectangle((int)(x0 + d + Ui.S(10)), (int)y, size.Width + 4, size.Height + 2), TextFormatFlags.Left);
            }
        }
    }

    // Inicio: bienvenida, activar/desactivar la barra y ajustes rápidos.
    sealed class HomePage : Page
    {
        readonly HeroCard hero = new HeroCard();
        readonly Card quick = new Card();
        readonly ToggleSwitch autostart = new ToggleSwitch();
        EngineState state = EngineState.Off;
        bool busy;
        readonly SegmentedPicker language = new SegmentedPicker(L.Names);
        public event Action ToggleRequested;
        public event Action LanguageChanged;   // hay que rehacer las páginas: cada una fija sus textos al construirse

        public HomePage(Image logo) : base("", "")
        {
            hero.Logo = logo;
            hero.Button.Click += (o, e) => { if (ToggleRequested != null) ToggleRequested(); };
            Controls.Add(hero);

            autostart.Size = new Size(Ui.S(46), Ui.S(24));
            autostart.SetCheckedSilently(Config.GetAutoStart());
            autostart.CheckedChanged += (o, e) => Config.SetAutoStart(autostart.Checked);
            quick.AddRow(new SettingRow(L.T("Iniciar con Windows"), L.T("Abre la barra de tareas automáticamente al iniciar sesión"), autostart));
            language.Selected = L.Index;
            language.SelectedChanged += i =>
            {
                L.Set(L.Codes[i]);
                Config.Save("lang", L.Codes[i]);
                Engine.ReloadConfig();
                if (LanguageChanged != null) LanguageChanged();
            };
            quick.AddRow(new SettingRow(L.T("Idioma"), L.T("Idioma de la aplicación y de la barra de tareas."), language));
            Controls.Add(quick);
        }

        public override void UpdateEngine(EngineState s, bool b, string busyText)
        {
            state = s;
            busy = b;
            var btn = hero.Button;
            if (b) { btn.Text = busyText; btn.Mode = PillMode.Busy; hero.Status = busyText.TrimEnd('.') + "…"; hero.StatusColor = Color.FromArgb(230, 160, 30); }
            else if (s == EngineState.On) { btn.Text = L.T("Desactivar barra de tareas"); btn.Mode = PillMode.Outline; hero.Status = L.T("Barra de tareas activa"); hero.StatusColor = Color.FromArgb(46, 158, 91); }
            else if (s == EngineState.Paused) { btn.Text = L.T("Activar barra de tareas"); btn.Mode = PillMode.Primary; hero.Status = L.T("En pausa: se muestra la barra de Windows"); hero.StatusColor = Color.FromArgb(230, 160, 30); }
            else { btn.Text = L.T("Activar barra de tareas"); btn.Mode = PillMode.Primary; hero.Status = L.T("Barra de tareas desactivada"); hero.StatusColor = Color.FromArgb(140, 140, 146); }
            hero.Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = ContentLeft, w = ContentWidth, y = HeaderHeight;
            hero.SetBounds(x, y, w, hero.PreferredHeight);
            y += hero.PreferredHeight + Ui.S(16);
            quick.SetBounds(x, y, w, quick.PreferredHeight);
        }
    }

    // Apariencia: opciones visuales de las islas.
    sealed class AppearancePage : Page
    {
        readonly Card hide = new Card();
        readonly Card colors = new Card();
        readonly Card card = new Card();

        public AppearancePage() : base(L.T("Apariencia"), L.T("Personaliza cómo se ven las islas de la barra de tareas."))
        {
            hide.AddRow(new SettingRow(L.T("Ocultar la isla de aplicaciones"), L.T("Inicio y tus apps. Vuelve al pasar el ratón por su sitio."),
                HideSwitch("hideleft", Config.HideLeft, on => Config.HideLeft = on)));
            hide.AddRow(new SettingRow(L.T("Ocultar la isla de información"), L.T("Red, volumen, batería y reloj. Vuelve al pasar el ratón por su sitio."),
                HideSwitch("hideright", Config.HideRight, on => Config.HideRight = on)));
            Controls.Add(hide);

            colors.AddRow(new SettingRow(L.T("Color de la isla de aplicaciones"), L.T("Un color, transparente o el que tú elijas."),
                ColorPicker("tintleft", Config.TintLeft, c => Config.TintLeft = c)));
            colors.AddRow(new SettingRow(L.T("Color de la isla de información"), L.T("Un color, transparente o el que tú elijas."),
                ColorPicker("tintright", Config.TintRight, c => Config.TintRight = c)));
            Controls.Add(colors);

            card.AddRow(new SettingRow(L.T("Ajustes de la barra"), L.T("La posición, la alineación, los botones, las etiquetas, el reloj y la ocultación automática siguen lo que elijas en Configuración > Personalización > Barra de tareas."), null));
            Controls.Add(card);
        }

        // Interruptor que guarda su valor en config.ini y lo aplica a la barra en marcha.
        static ToggleSwitch HideSwitch(string key, bool value, Action<bool> set)
        {
            var sw = new ToggleSwitch();
            sw.Size = new Size(Ui.S(46), Ui.S(24));
            sw.SetCheckedSilently(value);
            sw.CheckedChanged += (o, e) =>
            {
                set(sw.Checked);
                Config.Save(key, sw.Checked ? "1" : "0");
                Engine.ReloadConfig();
            };
            return sw;
        }

        // Selector de color que guarda su valor (AARRGGBB) en config.ini y lo aplica a la barra en marcha.
        static SwatchPicker ColorPicker(string key, uint value, Action<uint> set)
        {
            var picker = new SwatchPicker();
            picker.Value = value;
            picker.ValueChanged += c =>
            {
                set(c);
                Config.Save(key, c.ToString("X8"));
                Engine.ReloadConfig();
            };
            return picker;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = HeaderHeight;
            hide.SetBounds(ContentLeft, y, ContentWidth, hide.PreferredHeight);
            y += hide.PreferredHeight + Ui.S(16);
            colors.SetBounds(ContentLeft, y, ContentWidth, colors.PreferredHeight);
            y += colors.PreferredHeight + Ui.S(16);
            card.SetBounds(ContentLeft, y, ContentWidth, card.PreferredHeight);
        }
    }

    // Posición: unir las islas en una sola barra y llevarlas a otra esquina de la pantalla.
    sealed class PositionPage : Page
    {
        readonly Card card = new Card();
        readonly ToggleSwitch join = new ToggleSwitch();
        readonly PillButton move = new PillButton(), reset = new PillButton();
        readonly SettingRow moveRow;
        bool moving, engineOn;

        public PositionPage() : base(L.T("Posición"), L.T("Elige dónde van las islas de la barra de tareas."))
        {
            join.Size = new Size(Ui.S(46), Ui.S(24));
            join.SetCheckedSilently(Config.Join);
            join.CheckedChanged += (o, e) =>
            {
                Config.Join = join.Checked;
                Config.Save("join", join.Checked ? "1" : "0");
                Engine.ReloadConfig();
            };
            card.AddRow(new SettingRow(L.T("Unir las islas"), L.T("Forman una sola barra, con el color de la isla de aplicaciones."), join));

            move.FontPx = reset.FontPx = 14;
            move.Size = reset.Size = new Size(Ui.S(120), Ui.S(36));
            move.Click += (o, e) => SetMoving(!moving);
            moveRow = new SettingRow(L.T("Mover las islas"), "", move);
            card.AddRow(moveRow);

            reset.Text = L.T("Restablecer");
            reset.Mode = PillMode.Outline;
            reset.Click += (o, e) =>
            {
                Config.CornerLeft = Config.CornerRight = -1;
                Config.Save("cornerleft", "");
                Config.Save("cornerright", "");
                Engine.ReloadConfig();
            };
            card.AddRow(new SettingRow(L.T("Volver a su sitio"), L.T("Las islas vuelven a donde manda la barra de tareas de Windows."), reset));
            Controls.Add(card);
            ShowMoveState();
        }

        // Mover es temporal: las islas solo se dejan arrastrar desde que se pulsa "Mover" hasta que se confirma.
        void SetMoving(bool on)
        {
            if (on && !engineOn) return;
            moving = on;
            Engine.SetMoveMode(on);
            ShowMoveState();
        }

        void ShowMoveState()
        {
            move.Text = moving ? L.T("Confirmar") : L.T("Mover");
            move.Mode = moving ? PillMode.Primary : engineOn ? PillMode.Outline : PillMode.Busy;
            moveRow.Description = moving ? L.T("Arrastra cada isla a una esquina libre y pulsa Confirmar.")
                : engineOn ? L.T("Pulsa Mover para poder arrastrarlas a otra esquina de la pantalla.")
                : L.T("Activa la barra de tareas para poder moverlas.");
            moveRow.Invalidate();
        }

        public override void UpdateEngine(EngineState state, bool busy, string busyText)
        {
            bool on = state == EngineState.On && !busy;
            if (on == engineOn) return;
            engineOn = on;
            if (!on) moving = false;   // la barra sale sola del modo mover al pausarse o cerrarse
            ShowMoveState();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            card.SetBounds(ContentLeft, HeaderHeight, ContentWidth, card.PreferredHeight);
        }
    }

    // Acerca de: enlace al repositorio, carpeta de instalación y desinstalación.
    sealed class AboutPage : Page
    {
        const string RepoUrl = "https://github.com/neumanv/NeuTaskBarWin";
        readonly Card card = new Card();

        public AboutPage() : base(L.T("Acerca de"), L.T("Información sobre NeuTaskBar."))
        {
            card.AddRow(new SettingRow(L.T("Acceder al repositorio"), RepoUrl.Substring(8), null)
                .Link((g, r) => Ui.GitHubMark(g, r, Ui.Ink), () => { try { Process.Start(RepoUrl); } catch { } }));

            var open = SmallButton(L.T("Abrir"), PillMode.Outline);
            open.Click += (o, e) => { try { Process.Start("explorer.exe", "\"" + Config.InstallDir + "\""); } catch { } };
            card.AddRow(new SettingRow(L.T("Carpeta de instalación"), Config.InstallDir, open));

            var uninstall = SmallButton(L.T("Desinstalar"), PillMode.Outline);
            uninstall.Click += (o, e) =>
            {
                string un = Path.Combine(Config.InstallDir, "Uninstall.exe");
                if (File.Exists(un)) { try { Process.Start(un); } catch { } }
            };
            card.AddRow(new SettingRow(L.T("Desinstalar NeuTaskBar"), L.T("Quita la aplicación y devuelve la barra de tareas original de Windows"), uninstall));
            Controls.Add(card);
        }

        static PillButton SmallButton(string text, PillMode mode)
        {
            var b = new PillButton();
            b.Text = text;
            b.Mode = mode;
            b.FontPx = 14;
            b.Size = new Size(Ui.S(120), Ui.S(36));
            return b;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            card.SetBounds(ContentLeft, HeaderHeight, ContentWidth, card.PreferredHeight);
        }
    }

    // Ventana principal: barra lateral de navegación + página activa.
    sealed class MainForm : Form
    {
        readonly NavBar nav = new NavBar();
        readonly Panel host = new Panel();
        readonly List<Page> pages = new List<Page>();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        HomePage home;
        Image logo;
        EngineState state = EngineState.Off;
        bool busy;
        string busyText = "";
        EngineState busyFrom;
        DateTime busyUntil;

        public MainForm()
        {
            Text = "NeuTaskBar";
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Ui.Scale = PrimaryDpi() / 96f;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var st = asm.GetManifestResourceStream("app.ico")) if (st != null) Icon = new Icon(st, new Size(32, 32));
                using (var st = asm.GetManifestResourceStream("logo.png"))
                {
                    if (st != null)
                    {
                        var ms = new MemoryStream();
                        st.CopyTo(ms);
                        ms.Position = 0;
                        logo = Image.FromStream(ms);
                    }
                }
            }
            catch { }

            nav.Logo = logo;
            nav.Dock = DockStyle.Left;
            nav.SelectedChanged += i => ShowPage(i);

            host.Dock = DockStyle.Fill;
            host.BackColor = Ui.Bg;

            Controls.Add(host);
            Controls.Add(nav);

            BuildPages();

            poll.Interval = 700;
            poll.Tick += (o, e) => RefreshState();
            poll.Start();

            ApplyScale();
            RefreshState();
        }

        // Crea las páginas; se rehacen al cambiar de idioma, porque cada una fija sus textos al construirse.
        void BuildPages()
        {
            int selected = pages.Count > 0 ? nav.Selected : 0;
            if (pages.Count > 0) Engine.SetMoveMode(false);
            foreach (var p in pages) { host.Controls.Remove(p); p.Dispose(); }
            pages.Clear();
            nav.Titles.Clear();
            nav.Glyphs.Clear();
            home = new HomePage(logo);
            home.ToggleRequested += OnToggle;
            AddPage(L.T("Inicio|home"), "", home);
            AddPage(L.T("Apariencia"), "", new AppearancePage());
            AddPage(L.T("Posición"), "\uE7C2", new PositionPage());
            AddPage(L.T("Acerca de"), "", new AboutPage());
            home.LanguageChanged += () => BeginInvoke((Action)BuildPages);
            ShowPage(selected);
            foreach (var p in pages) p.PerformLayout();
            Publish();
        }

        // Añade una página a la navegación lateral.
        void AddPage(string title, string glyph, Page page)
        {
            pages.Add(page);
            nav.Titles.Add(title);
            nav.Glyphs.Add(glyph);
            page.Visible = false;
            host.Controls.Add(page);
        }

        void ShowPage(int i)
        {
            for (int k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
            nav.Selected = i;
            pages[i].Focus();
        }

        // DPI del monitor principal (la ventana arranca centrada en él).
        static int PrimaryDpi()
        {
            return Native.GetMonitorDpi(Native.MonitorFromPoint(new POINT(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */));
        }

        // DeviceDpi de WinForms no refleja el DPI real aquí: se lee de la ventana.
        void RefreshDpi()
        {
            try
            {
                uint dpi = Native.GetDpiForWindow(Handle);
                if (dpi > 0) Ui.Scale = dpi / 96f;
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetPropW(Handle, Native.MainWindowProp, new IntPtr(1));
            RefreshDpi();
            ApplyScale();
        }

        // Cambio de DPI al mover la ventana a otro monitor (WM_DPICHANGED).
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0)
            {
                Ui.Scale = Native.LoWord(m.WParam) / 96f;
                var r = (RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(RECT));
                SetBounds(r.Left, r.Top, r.Width, r.Height);
                ApplyScale();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Engine.SetMoveMode(false);   // cerrar la ventana confirma: las islas no se quedan sueltas
            Native.RemovePropW(Handle, Native.MainWindowProp);
            base.OnFormClosed(e);
        }

        void ApplyScale()
        {
            MinimumSize = SizeFromClientSize(new Size(Ui.S(820), Ui.S(600)));
            if (ClientSize.Width < Ui.S(980) && !Visible) ClientSize = new Size(Ui.S(980), Ui.S(660));
            nav.Width = Ui.S(240);
            foreach (var p in pages) p.PerformLayout();
            Invalidate(true);
        }

        void OnToggle()
        {
            if (busy) return;
            busyFrom = state;
            busy = true;
            busyUntil = DateTime.UtcNow.AddSeconds(8);
            switch (state)
            {
                case EngineState.Off: busyText = L.T("Activando..."); Engine.Start(); break;
                case EngineState.Paused: busyText = L.T("Activando..."); Engine.Resume(); break;
                default: busyText = L.T("Desactivando..."); Engine.Stop(); break;
            }
            Publish();
        }

        void RefreshState()
        {
            var now = Engine.Query();
            if (busy && (now != busyFrom || DateTime.UtcNow > busyUntil)) busy = false;
            if (now != state || !busy) { state = now; Publish(); }
        }

        void Publish()
        {
            foreach (var p in pages) p.UpdateEngine(state, busy, busyText);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { poll.Dispose(); if (logo != null) logo.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
