using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeuTaskBar
{
    // Idioma de la aplicación y de la barra: español (el de los textos del código), inglés o francés.
    // Cada texto visible se pide con L.T("texto en español"): devuelve su traducción al idioma elegido, o el propio
    // texto si no la hay. Si dos textos iguales necesitan traducciones distintas, la clave lleva detrás de "|" una
    // pista que no se muestra ("Inicio|home", "Inicio|start").
    // Para añadir un idioma: una entrada más en Codes y Names y una columna más en cada fila de la tabla.
    static class L
    {
        public static readonly string[] Codes = { "es", "en", "fr" };
        public static readonly string[] Names = { "Español", "English", "Français" };

        static int current = Detect();
        static Dictionary<string, string[]> table;

        // Posición en Codes del idioma en uso.
        public static int Index { get { return current; } }
        public static string Code { get { return Codes[current]; } }

        // Idioma de Windows si es uno de los disponibles; si no, inglés.
        static int Detect()
        {
            int i = Array.IndexOf(Codes, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            return i >= 0 ? i : 1;
        }

        // Fija el idioma por su código ("es", "en", "fr"); vacío o desconocido = el de Windows.
        public static void Set(string code)
        {
            int i = Array.IndexOf(Codes, (code ?? "").Trim().ToLowerInvariant());
            current = i >= 0 ? i : Detect();
        }

        // Formato de fechas del idioma en uso (el regional de Windows si es ese mismo idioma).
        public static CultureInfo Culture
        {
            get
            {
                var c = CultureInfo.CurrentCulture;
                if (c.TwoLetterISOLanguageName == Code) return c;
                try { return CultureInfo.GetCultureInfo(current == 0 ? "es-ES" : current == 2 ? "fr-FR" : "en-US"); }
                catch { return c; }
            }
        }

        public static string T(string key)
        {
            if (current != 0)
            {
                if (table == null) Build();
                string[] row;
                if (table.TryGetValue(key, out row) && row[current - 1] != null) return row[current - 1];
            }
            int bar = key.IndexOf('|');
            return bar < 0 ? key : key.Substring(0, bar);
        }

        static void Build()
        {
            table = new Dictionary<string, string[]>();
            for (int i = 0; i + 2 < Rows.Length; i += 3) table[Rows[i]] = new[] { Rows[i + 1], Rows[i + 2] };
        }

        // español (clave), inglés, francés
        static readonly string[] Rows =
        {
            // ---- ventana de la aplicación: navegación y estado
            "Inicio|home", "Home", "Accueil",
            "Apariencia", "Appearance", "Apparence",
            "Posición", "Position", "Position",
            "Acerca de", "About", "À propos",
            "Barra de tareas activa", "Taskbar on", "Barre des tâches active",
            "Barra de tareas desactivada", "Taskbar off", "Barre des tâches désactivée",
            "En pausa: se muestra la barra de Windows", "Paused: the Windows taskbar is shown", "En pause : la barre de Windows est affichée",
            "Activando...", "Turning on...", "Activation...",
            "Desactivando...", "Turning off...", "Désactivation...",

            // ---- Inicio
            "Bienvenido a NeuTaskBar", "Welcome to NeuTaskBar", "Bienvenue dans NeuTaskBar",
            "Personaliza tu barra de tareas de Windows con un diseño de dos islas:", "Customize your Windows taskbar with a two-island design:",
                "Personnalisez votre barre des tâches Windows avec deux îles :",
            "tus aplicaciones a la izquierda y el sistema a la derecha.", "your apps on the left and the system on the right.",
                "vos applications à gauche et le système à droite.",
            "Activar barra de tareas", "Turn on taskbar", "Activer la barre des tâches",
            "Desactivar barra de tareas", "Turn off taskbar", "Désactiver la barre des tâches",
            "Iniciar con Windows", "Start with Windows", "Lancer au démarrage de Windows",
            "Abre la barra de tareas automáticamente al iniciar sesión", "Starts the taskbar automatically when you sign in",
                "Lance la barre des tâches automatiquement à l'ouverture de session",
            "Idioma", "Language", "Langue",
            "Idioma de la aplicación y de la barra de tareas.", "Language of the app and the taskbar.", "Langue de l'application et de la barre des tâches.",

            // ---- Apariencia
            "Personaliza cómo se ven las islas de la barra de tareas.", "Customize how the taskbar islands look.",
                "Personnalisez l'apparence des îles de la barre des tâches.",
            "Ocultar la isla de aplicaciones", "Hide the apps island", "Masquer l'île des applications",
            "Inicio y tus apps. Vuelve al pasar el ratón por su sitio.", "Start and your apps. Comes back when the mouse passes over its spot.",
                "Démarrer et vos applications. Revient au passage de la souris.",
            "Ocultar la isla de información", "Hide the info island", "Masquer l'île d'informations",
            "Red, volumen, batería y reloj. Vuelve al pasar el ratón por su sitio.", "Network, volume, battery and clock. Comes back when the mouse passes over its spot.",
                "Réseau, volume, batterie et horloge. Revient au passage de la souris.",
            "Color de la isla de aplicaciones", "Apps island color", "Couleur de l'île des applications",
            "Color de la isla de información", "Info island color", "Couleur de l'île d'informations",
            "Un color, transparente o el que tú elijas.", "A color, transparent or one of your choice.", "Une couleur, transparent ou celle de votre choix.",
            "Predeterminado", "Default", "Par défaut",
            "Azul", "Blue", "Bleu",
            "Verde", "Green", "Vert",
            "Rojo", "Red", "Rouge",
            "Claro", "Light", "Clair",
            "Transparente", "Transparent", "Transparent",
            "Otro color…", "Another color…", "Autre couleur…",
            "Ajustes de la barra", "Taskbar settings", "Réglages de la barre",
            "La posición, la alineación, los botones, las etiquetas, el reloj y la ocultación automática siguen lo que elijas en Configuración > Personalización > Barra de tareas.",
                "Position, alignment, buttons, labels, clock and auto-hide follow what you choose in Settings > Personalization > Taskbar.",
                "La position, l'alignement, les boutons, les étiquettes, l'horloge et le masquage automatique suivent vos choix dans Paramètres > Personnalisation > Barre des tâches.",

            // ---- Posición
            "Elige dónde van las islas de la barra de tareas.", "Choose where the taskbar islands go.", "Choisissez où placer les îles de la barre des tâches.",
            "Unir las islas", "Join the islands", "Réunir les îles",
            "Forman una sola barra, con el color de la isla de aplicaciones.", "They form a single bar, with the apps island color.",
                "Elles forment une seule barre, de la couleur de l'île des applications.",
            "Mover las islas", "Move the islands", "Déplacer les îles",
            "Mover", "Move", "Déplacer",
            "Confirmar", "Confirm", "Confirmer",
            "Arrastra cada isla a una esquina libre y pulsa Confirmar.", "Drag each island to a free corner and press Confirm.",
                "Faites glisser chaque île vers un coin libre, puis Confirmer.",
            "Pulsa Mover para poder arrastrarlas a otra esquina de la pantalla.", "Press Move to drag them to another corner of the screen.",
                "Appuyez sur Déplacer pour les faire glisser vers un autre coin.",
            "Activa la barra de tareas para poder moverlas.", "Turn on the taskbar to move them.", "Activez la barre des tâches pour pouvoir les déplacer.",
            "Volver a su sitio", "Back to their place", "Retour à leur place",
            "Las islas vuelven a donde manda la barra de tareas de Windows.", "The islands go back to where the Windows taskbar puts them.",
                "Les îles reviennent là où la barre de Windows les place.",
            "Restablecer", "Reset", "Réinitialiser",

            // ---- Acerca de
            "Información sobre NeuTaskBar.", "Information about NeuTaskBar.", "Informations sur NeuTaskBar.",
            "Acceder al repositorio", "Go to the repository", "Accéder au dépôt",
            "Carpeta de instalación", "Installation folder", "Dossier d'installation",
            "Abrir", "Open", "Ouvrir",
            "Desinstalar NeuTaskBar", "Uninstall NeuTaskBar", "Désinstaller NeuTaskBar",
            "Quita la aplicación y devuelve la barra de tareas original de Windows", "Removes the app and brings back the original Windows taskbar",
                "Supprime l'application et rétablit la barre des tâches d'origine",
            "Desinstalar", "Uninstall", "Désinstaller",

            // ---- isla de aplicaciones
            "Inicio|start", "Start", "Démarrer",
            "Buscar", "Search", "Rechercher",
            "Vista de tareas", "Task view", "Vue des tâches",

            // ---- isla de información
            "Mostrar iconos ocultos", "Show hidden icons", "Afficher les icônes cachées",
            "Teclado táctil", "Touch keyboard", "Clavier tactile",
            "Mostrar escritorio", "Show desktop", "Afficher le bureau",
            "Wi-Fi conectado", "Wi-Fi connected", "Wi-Fi connecté",
            "Ethernet conectado", "Ethernet connected", "Ethernet connecté",
            "Sin conexión a Internet", "No Internet connection", "Pas de connexion Internet",
            "Volumen: silenciado", "Volume: muted", "Volume : muet",
            "Volumen: ", "Volume: ", "Volume : ",
            "Batería: ", "Battery: ", "Batterie : ",
            " (cargando)", " (charging)", " (en charge)",
            " (conectada)", " (plugged in)", " (branchée)",

            // ---- menús de la barra
            "Tareas", "Tasks", "Tâches",
            "Aplicación", "Application", "Application",
            "Desanclar de la barra de tareas", "Unpin from taskbar", "Désépingler de la barre des tâches",
            "Anclar a la barra de tareas", "Pin to taskbar", "Épingler à la barre des tâches",
            "Finalizar tarea", "End task", "Fin de tâche",
            "Cerrar todas las ventanas", "Close all windows", "Fermer toutes les fenêtres",
            "Cerrar ventana", "Close window", "Fermer la fenêtre",
            "Administrador de tareas", "Task Manager", "Gestionnaire des tâches",
            "Configuración de la barra de tareas", "Taskbar settings", "Paramètres de la barre des tâches",
            "Ajustar fecha y hora", "Adjust date and time", "Ajuster la date et l'heure",
            "Configuración de red e Internet", "Network and Internet settings", "Paramètres réseau et Internet",
            "Abrir el mezclador de volumen", "Open volume mixer", "Ouvrir le mélangeur de volume",
            "Configuración de sonido", "Sound settings", "Paramètres de son",
            "Configuración de energía y batería", "Power and battery settings", "Paramètres d'alimentation et de batterie",

            // ---- icono de la bandeja
            "Abrir NeuTaskBar", "Open NeuTaskBar", "Ouvrir NeuTaskBar",
            "Reanudar", "Resume", "Reprendre",
            "Pausar (mostrar barra de Windows)", "Pause (show the Windows taskbar)", "Mettre en pause (afficher la barre de Windows)",
            "Salir", "Exit", "Quitter",
            "NeuTaskBar (en pausa)", "NeuTaskBar (paused)", "NeuTaskBar (en pause)",

            // ---- acceso directo y línea de comandos
            "Barra de tareas de dos islas", "Two-island taskbar", "Barre des tâches à deux îles",
            "NeuTaskBar - barra de tareas de dos islas", "NeuTaskBar - two-island taskbar", "NeuTaskBar - barre des tâches à deux îles",
            "abre la aplicación", "opens the app", "ouvre l'application",
            "inicia directamente la barra de tareas (sin ventana)", "starts the taskbar directly (no window)", "lance directement la barre des tâches (sans fenêtre)",
            "cierra la barra de tareas en ejecución", "closes the running taskbar", "ferme la barre des tâches en cours",
            "restaura la barra de tareas original (por si quedó oculta)", "restores the original taskbar (in case it stayed hidden)",
                "rétablit la barre des tâches d'origine (si elle est restée masquée)",
            "activa/desactiva el inicio con Windows", "turns starting with Windows on/off", "active/désactive le lancement au démarrage de Windows",

            // ---- desinstalador
            "¿Quieres desinstalar NeuTaskBar?\r\n\r\nSe detendrá la barra de tareas personalizada, volverá la barra original de Windows y se eliminarán los programas de:\r\n",
                "Do you want to uninstall NeuTaskBar?\r\n\r\nThe custom taskbar will stop, the original Windows taskbar will come back and the programs will be removed from:\r\n",
                "Voulez-vous désinstaller NeuTaskBar ?\r\n\r\nLa barre des tâches personnalisée s'arrêtera, la barre d'origine de Windows reviendra et les programmes seront supprimés de :\r\n",
            "¿Eliminar también tus ajustes y las aplicaciones fijadas?\r\n\r\n(Si eliges No se conservan por si vuelves a instalarla.)",
                "Also delete your settings and pinned apps?\r\n\r\n(If you choose No they are kept in case you install it again.)",
                "Supprimer aussi vos réglages et les applications épinglées ?\r\n\r\n(Si vous choisissez Non, ils sont conservés pour une réinstallation.)",
            "NeuTaskBar se ha desinstalado correctamente.", "NeuTaskBar has been uninstalled.", "NeuTaskBar a été désinstallé.",
            "NeuTaskBar se ha detenido, pero no se pudo borrar NeuTaskBar.exe (está en uso). Bórralo manualmente:\r\n",
                "NeuTaskBar has stopped, but NeuTaskBar.exe could not be deleted (it is in use). Delete it manually:\r\n",
                "NeuTaskBar s'est arrêté, mais NeuTaskBar.exe n'a pas pu être supprimé (il est utilisé). Supprimez-le manuellement :\r\n",
        };
    }
}
