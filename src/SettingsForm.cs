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
    // Si el contenido no cabe en la ventana, la página se desplaza (barra lateral o rueda del ratón).
    abstract class Page : ScrollableControl
    {
        protected readonly string title, subtitle;
        protected int ContentLeft { get { return Ui.S(40); } }
        protected int ContentWidth { get { return Math.Max(Ui.S(300), Math.Min(Width - Ui.S(80), Ui.S(760))); } }
        protected int HeaderHeight { get { return title.Length == 0 ? Ui.S(36) : (subtitle.Length == 0 ? Ui.S(88) : Ui.S(116)); } }

        // Los ajustes se han sustituido de golpe (tema, importación, restablecer): hay que rehacer las páginas.
        public event Action SettingsReplaced;
        protected void OnSettingsReplaced() { if (SettingsReplaced != null) SettingsReplaced(); }

        protected Page(string title, string subtitle)
        {
            this.title = title;
            this.subtitle = subtitle;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Dock = DockStyle.Fill;
            AutoScroll = true;
        }

        // Estado de la barra de tareas (lo recibe de la ventana principal).
        public virtual void UpdateEngine(EngineState state, bool busy, string busyText) { }

        PillButton resetButton;
        string[] resetKeys = new string[0];

        // Pone en el encabezado un botón que devuelve a su valor de fábrica los ajustes de esta página (las claves de
        // config.ini que se indican), después de preguntar.
        protected void EnableReset(params string[] keys)
        {
            resetKeys = keys;
            resetButton = Opt.Button(L.T("Restablecer"));
            resetButton.BackColor = Ui.Bg;
            resetButton.Click += (o, e) =>
            {
                string question = string.Format(L.T("Los ajustes de «{0}» volverán a como vienen de fábrica. ¿Continuar?"), title);
                if (MessageBox.Show(FindForm(), question, "NeuTaskBar", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes) ResetNow();
            };
            Controls.Add(resetButton);
        }

        // Quita de config.ini las claves de la página: sin ellas vale lo de fábrica. La barra lo aplica al momento y
        // las páginas se rehacen para enseñar los valores nuevos.
        void ResetNow()
        {
            var values = new Dictionary<string, string>();
            foreach (string k in resetKeys) values[k] = null;
            Config.SaveMany(values);
            Config.Load();
            Engine.ReloadConfig();
            OnSettingsReplaced();
        }

        // Coloca las tarjetas una bajo otra a partir del encabezado y fija cuánto puede desplazarse la página.
        protected void Stack(params Control[] cards)
        {
            int scroll = AutoScrollPosition.Y, y = HeaderHeight;
            // El botón de restablecer va a la derecha del título.
            if (resetButton != null) resetButton.Location = new Point(ContentLeft + ContentWidth - resetButton.Width, Ui.S(32) + scroll);
            foreach (var c in cards)
            {
                var hero = c as HeroCard;
                int h = hero != null ? hero.PreferredHeight : ((Card)c).PreferredHeight;
                c.SetBounds(ContentLeft, y + scroll, ContentWidth, h);
                y += h + Ui.S(16);
            }
            AutoScrollMinSize = new Size(0, y + Ui.S(8));
        }

        // Rueda del ratón (la reparte la ventana principal, tenga el foco quien lo tenga).
        public void Wheel(int delta)
        {
            if (!VScroll) return;
            AutoScrollPosition = new Point(0, -AutoScrollPosition.Y - delta * Ui.S(72) / 120);
            Invalidate();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Ui.Smooth(g);
            if (title.Length == 0) return;
            int scroll = AutoScrollPosition.Y;
            using (var f = Ui.Font(30, true))
                Ui.Text(g, title, f, Ui.Ink, new Rectangle(ContentLeft, Ui.S(30) + scroll, ContentWidth, Ui.S(40)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (subtitle.Length > 0)
                using (var f = Ui.Font(14, false))
                    Ui.Text(g, subtitle, f, Ui.Muted, new Rectangle(ContentLeft, Ui.S(74) + scroll, ContentWidth, Ui.S(22)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    // Controles de una opción que se guarda en config.ini y se aplica al momento a la barra en marcha.
    static class Opt
    {
        public static void Save(string key, string value)
        {
            Config.Save(key, value);
            Config.Load();
            Engine.ReloadConfig();
        }

        public static ToggleSwitch Toggle(string key, bool value)
        {
            var sw = new ToggleSwitch();
            sw.Size = new Size(Ui.S(46), Ui.S(24));
            sw.SetCheckedSilently(value);
            sw.CheckedChanged += (o, e) => Save(key, sw.Checked ? "1" : "0");
            return sw;
        }

        // Una opción entre varias; se guarda su posición (0, 1, 2...).
        public static SegmentedPicker Choice(string key, int value, int segment, params string[] options)
        {
            var picker = new SegmentedPicker(options, segment);
            picker.Selected = Math.Max(0, Math.Min(options.Length - 1, value));
            picker.SelectedChanged += i => Save(key, i.ToString());
            return picker;
        }

        public static Slider Range(string key, int value, int min, int max, int step, Func<int, string> format)
        {
            var slider = new Slider(min, max, step);
            slider.Format = format;
            slider.Value = value;
            slider.ValueChanged += v => Save(key, v.ToString());
            return slider;
        }

        public static PillButton Button(string text)
        {
            var b = new PillButton();
            b.Text = text;
            b.Mode = PillMode.Outline;
            b.FontPx = 14;
            b.Size = new Size(Ui.S(120), Ui.S(36));
            return b;
        }

        public static string Px(int v) { return v + " px"; }
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
            Stack(hero, quick);
        }
    }

    // Apariencia: opciones visuales de las islas.
    sealed class AppearancePage : Page
    {
        readonly Card hide = new Card();
        readonly Card size = new Card();
        readonly Card colors = new Card();
        readonly Card detail = new Card();
        readonly Card card = new Card();

        static readonly uint[] AccentPresets = { 0, 0xFF6CCB5F, 0xFFFFB900, 0xFFFF6B9D, 0xFFB4A0FF };
        static readonly string[] AccentNames = { "Predeterminado", "Verde", "Ámbar", "Rosa", "Morado", "Otro color…" };

        public AppearancePage() : base(L.T("Apariencia"), L.T("Personaliza cómo se ven las islas de la barra de tareas."))
        {
            EnableReset("hideleft", "hideright", "height", "iconsize", "margin", "textsize", "tintleft", "tintright", "opacity", "autotheme",
                "fill", "accent", "accentwin", "ink", "border", "reducemotion", "calmonbattery");
            size.AddRow(new SettingRow(L.T("Alto de las islas"), L.T("El de la barra de Windows es 48."), Opt.Range("height", Config.Height, 32, 72, 2, Opt.Px)));
            size.AddRow(new SettingRow(L.T("Tamaño de los iconos"), L.T("Los de tus aplicaciones."), Opt.Range("iconsize", Config.IconSize, 16, 40, 2, Opt.Px)));
            size.AddRow(new SettingRow(L.T("Separación con los bordes"), L.T("Con más de 0, las islas flotan."), Opt.Range("margin", Config.Margin, 0, 24, 1, Opt.Px)));
            size.AddRow(new SettingRow(L.T("Tamaño del texto"), L.T("Reloj, fecha y etiquetas."), Opt.Range("textsize", Config.TextSize, 10, 16, 1, Opt.Px)));
            Controls.Add(size);

            detail.AddRow(new SettingRow(L.T("Color de énfasis"), L.T("Logo de Inicio y marca de la app activa."), AccentPicker()));
            detail.AddRow(new SettingRow(L.T("Usar el color de énfasis de Windows"), L.T("El que elijas en Configuración > Personalización > Colores."),
                Opt.Toggle("accentwin", Config.AccentWindows)));
            detail.AddRow(new SettingRow(L.T("Color del contenido"), L.T("Texto e iconos del sistema."),
                Opt.Choice("ink", Config.InkMode, 96, L.T("Automático"), L.T("Claro|ink"), L.T("Oscuro|ink"))));
            detail.AddRow(new SettingRow(L.T("Contorno"), L.T("Una línea fina alrededor de cada isla."), Opt.Toggle("border", Config.Border)));
            detail.AddRow(new SettingRow(L.T("Reducir animaciones"), L.T("Los cambios se muestran al instante, sin transiciones."),
                Opt.Toggle("reducemotion", Config.ReduceMotion)));
            detail.AddRow(new SettingRow(L.T("Sin animaciones con batería"), L.T("Solo hay animaciones con el equipo enchufado."),
                Opt.Toggle("calmonbattery", Config.CalmOnBattery)));
            Controls.Add(detail);

            hide.AddRow(new SettingRow(L.T("Ocultar la isla de aplicaciones"), L.T("Inicio y tus apps. Vuelve al pasar el ratón por su sitio."),
                HideSwitch("hideleft", Config.HideLeft, on => Config.HideLeft = on)));
            hide.AddRow(new SettingRow(L.T("Ocultar la isla de información"), L.T("Red, volumen, batería y reloj. Vuelve al pasar el ratón por su sitio."),
                HideSwitch("hideright", Config.HideRight, on => Config.HideRight = on)));
            Controls.Add(hide);

            colors.AddRow(new SettingRow(L.T("Color de la isla de aplicaciones"), L.T("Un color, transparente o el que tú elijas."),
                ColorPicker("tintleft", Config.TintLeft, c => Config.TintLeft = c)));
            colors.AddRow(new SettingRow(L.T("Color de la isla de información"), L.T("Un color, transparente o el que tú elijas."),
                ColorPicker("tintright", Config.TintRight, c => Config.TintRight = c)));
            colors.AddRow(new SettingRow(L.T("Opacidad del color"), L.T("Cuánto tapa el color lo que hay detrás."), OpacitySlider()));
            colors.AddRow(new SettingRow(L.T("Seguir el modo de Windows"), L.T("Islas claras u oscuras como Windows, en vez de los colores de arriba."),
                Opt.Toggle("autotheme", Config.AutoTheme)));
            colors.AddRow(new SettingRow(L.T("Relleno entre las islas"), L.T("Bajo una ventana maximizada."),
                Opt.Choice("fill", Config.FillMode, 140, L.T("Seguir la ventana"), L.T("Color de las islas"))));
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

        // Opacidad común de las dos islas; en el extremo izquierdo ("Auto") cada color conserva la suya.
        static Slider OpacitySlider()
        {
            var slider = new Slider(-5, 100, 5);
            slider.Format = v => v < 0 ? L.T("Auto") : v + " %";
            slider.Value = Config.Opacity < 0 ? -5 : Config.Opacity;
            slider.ValueChanged += v => Opt.Save("opacity", v < 0 ? "" : v.ToString());
            return slider;
        }

        // Color de énfasis: el predeterminado (un azul que cambia con la tinta), varios hechos o uno a medida.
        static SwatchPicker AccentPicker()
        {
            var picker = new SwatchPicker(AccentPresets, AccentNames, 0xFF000000, 0xFF4CC2FF);
            picker.Value = Config.AccentColor == 0 ? 0 : 0xFF000000 | Config.AccentColor;
            picker.ValueChanged += c => Opt.Save("accent", (c & 0xFFFFFF) == 0 ? "" : (c & 0xFFFFFF).ToString("X6"));
            return picker;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Stack(hide, size, colors, detail, card);
        }
    }

    // Aplicaciones: aspecto y contenido de la isla de aplicaciones.
    sealed class AppsPage : Page
    {
        readonly Card look = new Card();
        readonly Card shape = new Card();
        readonly Card hidden = new Card();
        readonly SettingRow hiddenRow, groupsRow;
        readonly PillButton showAll, ungroupAll;
        string hiddenShown;
        int groupsShown = -1;

        public AppsPage() : base(L.T("Aplicaciones"), L.T("Personaliza la isla de aplicaciones."))
        {
            EnableReset("indicator", "hover", "icongap", "mono", "bounce", "hidestart", "minwidth", "hideapps");
            look.AddRow(new SettingRow(L.T("Marca de app abierta"), L.T("Bajo el icono: una por ventana abierta, hasta cuatro."),
                Opt.Choice("indicator", Config.Indicator, 88, L.T("Línea"), L.T("Punto"), L.T("Ninguna"))));
            look.AddRow(new SettingRow(L.T("Al pasar el ratón"), L.T("Cómo responde el icono."),
                Opt.Choice("hover", Config.HoverEffect, 88, L.T("Resaltar"), L.T("Ampliar"), L.T("Nada"))));
            look.AddRow(new SettingRow(L.T("Separación entre iconos"), "", Opt.Range("icongap", Config.IconGap, 0, 12, 1, Opt.Px)));
            look.AddRow(new SettingRow(L.T("Iconos en gris"), L.T("Todos los iconos en un solo tono."), Opt.Toggle("mono", Config.MonoIcons)));
            look.AddRow(new SettingRow(L.T("Rebote al abrir"), L.T("El icono rebota mientras la app se abre."), Opt.Toggle("bounce", Config.LaunchBounce)));
            Controls.Add(look);

            var start = new ToggleSwitch();
            start.Size = new Size(Ui.S(46), Ui.S(24));
            start.SetCheckedSilently(!Config.HideStart);
            start.CheckedChanged += (o, e) => Opt.Save("hidestart", start.Checked ? "0" : "1");
            shape.AddRow(new SettingRow(L.T("Botón de Inicio"), L.T("Sin él, la tecla Windows sigue abriendo Inicio."), start));
            shape.AddRow(new SettingRow(L.T("Anchura mínima"), L.T("La isla no encoge por debajo de esta anchura."),
                Opt.Range("minwidth", Config.MinWidth, 0, 1200, 40, v => v == 0 ? L.T("Auto") : Opt.Px(v))));
            Controls.Add(shape);

            showAll = Opt.Button(L.T("Mostrar todas"));
            showAll.Size = new Size(Ui.S(150), Ui.S(36));
            showAll.Click += (o, e) => { Opt.Save("hideapps", ""); ShowHidden(); };
            hiddenRow = new SettingRow(L.T("Aplicaciones ocultas"), "", showAll);
            hidden.AddRow(hiddenRow);

            // Los grupos se hacen en la propia barra; aquí se explica cómo y se pueden deshacer todos.
            ungroupAll = Opt.Button(L.T("Deshacer todos"));
            ungroupAll.Size = new Size(Ui.S(150), Ui.S(36));
            ungroupAll.Click += (o, e) => { PinStore.ClearGroups(); Engine.ReloadConfig(); ShowGroups(); };
            groupsRow = new SettingRow(L.T("Grupos de aplicaciones"), "", ungroupAll);
            hidden.AddRow(groupsRow);
            Controls.Add(hidden);
            ShowHidden();
            ShowGroups();
        }

        void ShowGroups()
        {
            int n = PinStore.CountGroups();
            if (n == groupsShown) return;
            groupsShown = n;
            groupsRow.Title = L.T("Grupos de aplicaciones") + (n > 0 ? " (" + n + ")" : "");
            groupsRow.Description = L.T("Clic derecho en una app ▸ Crear grupo. Hasta 4 por grupo.");
            ungroupAll.Mode = n == 0 ? PillMode.Busy : PillMode.Outline;
            groupsRow.Invalidate();
        }

        void ShowHidden()
        {
            string text = Config.HiddenApps.Length == 0 ? L.T("Ninguna. Se ocultan desde el menú de su icono en la barra.") : string.Join(", ", Config.HiddenApps);
            if (text == hiddenShown) return;
            hiddenShown = text;
            hiddenRow.Description = text;
            showAll.Mode = Config.HiddenApps.Length == 0 ? PillMode.Busy : PillMode.Outline;
            hiddenRow.Invalidate();
        }

        // Las apps se ocultan desde la barra, con la ventana quizá abierta: la lista se repasa mientras se ve la página.
        public override void UpdateEngine(EngineState state, bool busy, string busyText)
        {
            if (!Visible) return;
            Config.Load();
            ShowHidden();
            ShowGroups();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Stack(look, shape, hidden);
        }
    }

    // Información: lo que muestra la isla de información.
    sealed class InfoPage : Page
    {
        readonly Card icons = new Card();
        readonly Card clock = new Card();
        readonly Card extras = new Card();

        public InfoPage() : base(L.T("Información"), L.T("Elige qué muestra la isla de información."))
        {
            EnableReset("shownet", "showvol", "showbat", "batpercent", "clocktime", "clockdate", "showcpu", "showram", "showmedia", "notices");
            icons.AddRow(new SettingRow(L.T("Red"), L.T("Icono de la conexión a Internet."), Opt.Toggle("shownet", Config.ShowNet)));
            icons.AddRow(new SettingRow(L.T("Volumen"), L.T("La rueda del ratón sobre el icono lo sube y lo baja."), Opt.Toggle("showvol", Config.ShowVolume)));
            icons.AddRow(new SettingRow(L.T("Batería"), L.T("Solo aparece en equipos con batería."), Opt.Toggle("showbat", Config.ShowBattery)));
            icons.AddRow(new SettingRow(L.T("Porcentaje de batería"), L.T("La carga en números, junto al icono."), Opt.Toggle("batpercent", Config.BatteryPercent)));
            Controls.Add(icons);

            clock.AddRow(new SettingRow(L.T("Formato de la hora"), L.T("El de Windows, o siempre 24 o 12 horas."),
                Opt.Choice("clocktime", Config.ClockTime, 88, "Windows", L.T("24 h"), L.T("12 h"))));
            clock.AddRow(new SettingRow(L.T("Fecha"), L.T("Bajo la hora."),
                Opt.Choice("clockdate", Config.ClockDate, 100, L.T("Corta"), L.T("Con día"), L.T("Sin fecha"))));
            Controls.Add(clock);

            extras.AddRow(new SettingRow(L.T("Uso del procesador"), L.T("Porcentaje de CPU en uso. Al pulsarlo se abre el Administrador de tareas."),
                Opt.Toggle("showcpu", Config.ShowCpu)));
            extras.AddRow(new SettingRow(L.T("Uso de la memoria"), L.T("Porcentaje de RAM en uso."), Opt.Toggle("showram", Config.ShowRam)));
            extras.AddRow(new SettingRow(L.T("Controles multimedia"), L.T("Anterior, reproducir o pausar y siguiente."), Opt.Toggle("showmedia", Config.ShowMedia)));
            extras.AddRow(new SettingRow(L.T("Avisos en la isla"), L.T("El volumen y la batería se muestran un momento en el sitio del reloj al cambiar."),
                Opt.Toggle("notices", Config.Notices)));
            Controls.Add(extras);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Stack(icons, clock, extras);
        }
    }

    // Comportamiento: cómo responde la barra al ratón.
    sealed class BehaviorPage : Page
    {
        readonly Card card = new Card();

        public BehaviorPage() : base(L.T("Comportamiento"), L.T("Elige cómo responde la barra al ratón."))
        {
            EnableReset("middleclick", "wheel", "previews", "hidedelay");
            card.AddRow(new SettingRow(L.T("Clic central en una app"), L.T("Con la rueda del ratón pulsada."),
                Opt.Choice("middleclick", Config.MiddleClick, 130, L.T("Nueva ventana"), L.T("Cerrar ventana"))));
            card.AddRow(new SettingRow(L.T("Rueda sobre una app"), L.T("Al girarla encima de una app abierta."),
                Opt.Choice("wheel", Config.WheelAction, 130, L.T("Nada"), L.T("Cambiar ventana"))));
            card.AddRow(new SettingRow(L.T("Vistas previas"), L.T("Miniaturas de las ventanas al pasar el ratón por una app abierta."),
                Opt.Toggle("previews", Config.Previews)));
            card.AddRow(new SettingRow(L.T("Espera antes de esconderse"), L.T("Para las islas ocultas, al apartar el ratón."),
                Opt.Range("hidedelay", Config.HideDelay, 0, 2000, 50, v => v + " ms")));
            Controls.Add(card);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Stack(card);
        }
    }

    // Temas: aspectos ya hechos, y copia de los ajustes para llevarlos a otro equipo.
    sealed class ThemesPage : Page
    {
        static readonly string[] Descriptions =
        {
            "Las islas oscuras de siempre.", "Islas claras con el contenido en oscuro.", "Transparentes, con un contorno fino.",
            "Islas más bajas e iconos más pequeños.", "Separadas de los bordes y con contorno."
        };
        readonly Card themes = new Card();
        readonly Card backup = new Card();

        public ThemesPage() : base(L.T("Temas"), L.T("Cambia el aspecto de una vez o lleva tus ajustes a otro equipo."))
        {
            for (int i = 0; i < Config.ThemeNames.Length; i++)
            {
                int index = i;
                var apply = Opt.Button(L.T("Aplicar"));
                apply.Click += (o, e) => { Config.ApplyTheme(index); Replaced(); };
                themes.AddRow(new SettingRow(L.T(Config.ThemeNames[i] + "|theme"), L.T(Descriptions[i]), apply));
            }
            Controls.Add(themes);

            var export = Opt.Button(L.T("Exportar"));
            export.Click += (o, e) =>
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "NeuTaskBar (*.ini)|*.ini";
                    dlg.FileName = "NeuTaskBar.ini";
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK && !Config.ExportSettings(dlg.FileName))
                        MessageBox.Show(FindForm(), L.T("No se ha podido guardar el archivo."), "NeuTaskBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            backup.AddRow(new SettingRow(L.T("Exportar ajustes"), L.T("Guarda tus ajustes en un archivo."), export));

            var import = Opt.Button(L.T("Importar"));
            import.Click += (o, e) =>
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Filter = "NeuTaskBar (*.ini)|*.ini";
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    if (Config.ImportSettings(dlg.FileName)) Replaced();
                    else MessageBox.Show(FindForm(), L.T("Ese archivo no contiene ajustes de NeuTaskBar."), "NeuTaskBar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            backup.AddRow(new SettingRow(L.T("Importar ajustes"), L.T("Sustituye tus ajustes por los de un archivo exportado."), import));

            var reset = Opt.Button(L.T("Restablecer"));
            reset.Click += (o, e) =>
            {
                if (MessageBox.Show(FindForm(), L.T("Todos los ajustes volverán a como vienen de fábrica. ¿Continuar?"), "NeuTaskBar",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Config.ResetSettings();
                Replaced();
            };
            backup.AddRow(new SettingRow(L.T("Restablecer todo"), L.T("Deja la aplicación como recién instalada. El idioma no cambia."), reset));
            Controls.Add(backup);
        }

        // Los ajustes han cambiado de golpe: la barra los vuelve a leer y las páginas se rehacen con ellos.
        void Replaced()
        {
            Engine.ReloadConfig();
            OnSettingsReplaced();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Stack(themes, backup);
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
            EnableReset("fullbar", "join", "cornerleft", "cornerright");
            // Barra completa: manda sobre lo demás de la página (con ella no hay islas que unir ni que mover).
            var fullBar = new ToggleSwitch();
            fullBar.Size = new Size(Ui.S(46), Ui.S(24));
            fullBar.SetCheckedSilently(Config.FullBar);
            fullBar.CheckedChanged += (o, e) =>
            {
                if (fullBar.Checked && moving) SetMoving(false);
                Opt.Save("fullbar", fullBar.Checked ? "1" : "0");
                ShowMoveState();
            };
            card.AddRow(new SettingRow(L.T("Barra completa"), L.T("Una sola barra de lado a lado, como la de Windows, en vez de dos islas."), fullBar));

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
            if (on && (!engineOn || Config.FullBar)) return;
            moving = on;
            Engine.SetMoveMode(on);
            ShowMoveState();
        }

        void ShowMoveState()
        {
            move.Text = moving ? L.T("Confirmar") : L.T("Mover");
            move.Mode = moving ? PillMode.Primary : engineOn && !Config.FullBar ? PillMode.Outline : PillMode.Busy;
            moveRow.Description = moving ? L.T("Arrastra cada isla a un sitio libre y pulsa Confirmar.")
                : Config.FullBar ? L.T("Con la barra completa las islas no se mueven.")
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
            Stack(card);
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
            Stack(card);
        }
    }

    // Ventana principal: barra lateral de navegación + página activa.
    sealed class MainForm : Form, IMessageFilter
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
            Application.AddMessageFilter(this);
        }

        // La rueda del ratón desplaza la página que se ve, tenga el foco el control que lo tenga.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A /* WM_MOUSEWHEEL */ || ActiveForm != this) return false;
            int i = nav.Selected;
            if (i < 0 || i >= pages.Count) return false;
            pages[i].Wheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
            return true;
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
            AddPage(L.T("Aplicaciones"), "\uE71D", new AppsPage());
            AddPage(L.T("Información"), "\uE823", new InfoPage());
            AddPage(L.T("Comportamiento"), "\uE7C9", new BehaviorPage());
            AddPage(L.T("Posición"), "\uE7C2", new PositionPage());
            AddPage(L.T("Temas"), "\uE771", new ThemesPage());
            AddPage(L.T("Acerca de"), "", new AboutPage());
            home.LanguageChanged += () => BeginInvoke((Action)BuildPages);
            foreach (var p in pages) p.SettingsReplaced += () => BeginInvoke((Action)BuildPages);
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
            if (disposing) { Application.RemoveMessageFilter(this); poll.Dispose(); if (logo != null) logo.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
