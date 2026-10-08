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
                Ui.Text(g, "Bienvenido a NeuTaskBar", f, Ui.Ink, new Rectangle(0, Ui.S(146), W, Ui.S(40)),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var f = Ui.Font(15, false))
            {
                Ui.Text(g, "Personaliza tu barra de tareas de Windows con un diseño de dos islas:", f, Ui.Muted,
                    new Rectangle(0, Ui.S(192), W, Ui.S(24)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Ui.Text(g, "tus aplicaciones a la izquierda y el sistema a la derecha.", f, Ui.Muted,
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
        public event Action ToggleRequested;

        public HomePage(Image logo) : base("", "")
        {
            hero.Logo = logo;
            hero.Button.Click += (o, e) => { if (ToggleRequested != null) ToggleRequested(); };
            Controls.Add(hero);

            autostart.Size = new Size(Ui.S(46), Ui.S(24));
            autostart.SetCheckedSilently(Config.GetAutoStart());
            autostart.CheckedChanged += (o, e) => Config.SetAutoStart(autostart.Checked);
            quick.AddRow(new SettingRow("Iniciar con Windows", "Abre la barra de tareas automáticamente al iniciar sesión", autostart));
            Controls.Add(quick);
        }

        public override void UpdateEngine(EngineState s, bool b, string busyText)
        {
            state = s;
            busy = b;
            var btn = hero.Button;
            if (b) { btn.Text = busyText; btn.Mode = PillMode.Busy; hero.Status = busyText.TrimEnd('.') + "…"; hero.StatusColor = Color.FromArgb(230, 160, 30); }
            else if (s == EngineState.On) { btn.Text = "Desactivar barra de tareas"; btn.Mode = PillMode.Outline; hero.Status = "Barra de tareas activa"; hero.StatusColor = Color.FromArgb(46, 158, 91); }
            else if (s == EngineState.Paused) { btn.Text = "Activar barra de tareas"; btn.Mode = PillMode.Primary; hero.Status = "En pausa: se muestra la barra de Windows"; hero.StatusColor = Color.FromArgb(230, 160, 30); }
            else { btn.Text = "Activar barra de tareas"; btn.Mode = PillMode.Primary; hero.Status = "Barra de tareas desactivada"; hero.StatusColor = Color.FromArgb(140, 140, 146); }
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

    // Apariencia: aquí irán las opciones visuales de las islas.
    sealed class AppearancePage : Page
    {
        readonly Card card = new Card();

        public AppearancePage() : base("Apariencia", "Personaliza cómo se ven las islas de la barra de tareas.")
        {
            card.AddRow(new SettingRow("Botones de la barra", "Buscar, Vista de tareas y Widgets siguen lo que actives en Configuración > Personalización > Barra de tareas.", null));
            Controls.Add(card);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            card.SetBounds(ContentLeft, HeaderHeight, ContentWidth, card.PreferredHeight);
        }
    }

    // Acerca de: versión, carpeta de instalación y desinstalación.
    sealed class AboutPage : Page
    {
        readonly Card card = new Card();

        public AboutPage() : base("Acerca de", "Información sobre NeuTaskBar.")
        {
            card.AddRow(new SettingRow("NeuTaskBar", "Versión 1.0", null));

            var open = SmallButton("Abrir", PillMode.Outline);
            open.Click += (o, e) => { try { Process.Start("explorer.exe", "\"" + Config.InstallDir + "\""); } catch { } };
            card.AddRow(new SettingRow("Carpeta de instalación", Config.InstallDir, open));

            var uninstall = SmallButton("Desinstalar", PillMode.Outline);
            uninstall.Click += (o, e) =>
            {
                string un = Path.Combine(Config.InstallDir, "Uninstall.exe");
                if (File.Exists(un)) { try { Process.Start(un); } catch { } }
            };
            card.AddRow(new SettingRow("Desinstalar NeuTaskBar", "Quita la aplicación y devuelve la barra de tareas original de Windows", uninstall));
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
        readonly HomePage home;
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

            home = new HomePage(logo);
            home.ToggleRequested += OnToggle;
            AddPage("Inicio", "", home);
            AddPage("Apariencia", "", new AppearancePage());
            AddPage("Acerca de", "", new AboutPage());
            ShowPage(0);

            poll.Interval = 700;
            poll.Tick += (o, e) => RefreshState();
            poll.Start();

            ApplyScale();
            RefreshState();
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
            Native.RemovePropW(Handle, Native.MainWindowProp);
            base.OnFormClosed(e);
        }

        void ApplyScale()
        {
            MinimumSize = SizeFromClientSize(new Size(Ui.S(820), Ui.S(560)));
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
                case EngineState.Off: busyText = "Activando..."; Engine.Start(); break;
                case EngineState.Paused: busyText = "Activando..."; Engine.Resume(); break;
                default: busyText = "Desactivando..."; Engine.Stop(); break;
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
            if (busy) { nav.StatusText = busyText.TrimEnd('.') + "…"; nav.StatusColor = Color.FromArgb(230, 160, 30); }
            else if (state == EngineState.On) { nav.StatusText = "Barra de tareas activa"; nav.StatusColor = Color.FromArgb(46, 158, 91); }
            else if (state == EngineState.Paused) { nav.StatusText = "En pausa"; nav.StatusColor = Color.FromArgb(230, 160, 30); }
            else { nav.StatusText = "Barra de tareas desactivada"; nav.StatusColor = Color.FromArgb(140, 140, 146); }
            nav.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { poll.Dispose(); if (logo != null) logo.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
