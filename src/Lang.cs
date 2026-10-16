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
            "Tus aplicaciones a un lado y el sistema al otro.", "Your apps on one side, the system on the other.",
                "Vos applications d'un côté, le système de l'autre.",
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
            "Barra completa", "Full-width bar", "Barre pleine largeur",
            "Una sola barra de lado a lado, como la de Windows, en vez de dos islas.", "A single edge-to-edge bar, like Windows's, instead of two islands.",
                "Une seule barre d'un bord à l'autre, comme celle de Windows, au lieu de deux îles.",
            "Con la barra completa las islas no se mueven.", "With the full-width bar the islands cannot be moved.",
                "Avec la barre pleine largeur, les îles ne se déplacent pas.",
            "Mover las islas", "Move the islands", "Déplacer les îles",
            "Mover", "Move", "Déplacer",
            "Confirmar", "Confirm", "Confirmer",
            "Arrastra cada isla a un sitio libre y pulsa Confirmar.", "Drag each island to a free spot and press Confirm.",
                "Faites glisser chaque île vers un emplacement libre, puis Confirmer.",
            "Pulsa Mover para poder arrastrarlas a otra esquina de la pantalla.", "Press Move to drag them to another corner of the screen.",
                "Appuyez sur Déplacer pour les faire glisser vers un autre coin.",
            "Activa la barra de tareas para poder moverlas.", "Turn on the taskbar to move them.", "Activez la barre des tâches pour pouvoir les déplacer.",
            "Volver a su sitio", "Back to their place", "Retour à leur place",
            "Las islas vuelven a donde manda la barra de tareas de Windows.", "The islands go back to where the Windows taskbar puts them.",
                "Les îles reviennent là où la barre de Windows les place.",
            "Restablecer", "Reset", "Réinitialiser",

            "Los ajustes de «{0}» volverán a como vienen de fábrica. ¿Continuar?", "The “{0}” settings will go back to their defaults. Continue?",
                "Les réglages de « {0} » reviendront aux valeurs par défaut. Continuer ?",

            // ---- navegación: páginas de personalización
            "Aplicaciones", "Apps", "Applications",
            "Información", "Info", "Informations",
            "Comportamiento", "Behavior", "Comportement",
            "Temas", "Themes", "Thèmes",

            // ---- Apariencia: tamaño, color y detalles
            "Alto de las islas", "Island height", "Hauteur des îles",
            "El de la barra de Windows es 48.", "The Windows taskbar is 48.", "Celle de la barre de Windows est de 48.",
            "Tamaño de los iconos", "Icon size", "Taille des icônes",
            "Los de tus aplicaciones.", "Those of your apps.", "Celles de vos applications.",
            "Separación con los bordes", "Gap from the screen edges", "Écart avec les bords",
            "Con más de 0, las islas flotan.", "Above 0, the islands float.", "Au-dessus de 0, les îles flottent.",
            "Tamaño del texto", "Text size", "Taille du texte",
            "Reloj, fecha y etiquetas.", "Clock, date and labels.", "Horloge, date et étiquettes.",
            "Opacidad del color", "Color opacity", "Opacité de la couleur",
            "Cuánto tapa el color lo que hay detrás.", "How much the color covers what is behind.", "À quel point la couleur couvre l'arrière-plan.",
            "Auto", "Auto", "Auto",
            "Seguir el modo de Windows", "Follow the Windows mode", "Suivre le mode de Windows",
            "Islas claras u oscuras como Windows, en vez de los colores de arriba.", "Light or dark islands like Windows, instead of the colors above.",
                "Îles claires ou sombres comme Windows, au lieu des couleurs ci-dessus.",
            "Relleno entre las islas", "Fill between the islands", "Remplissage entre les îles",
            "Bajo una ventana maximizada.", "Under a maximized window.", "Sous une fenêtre agrandie.",
            "Seguir la ventana", "Follow the window", "Suivre la fenêtre",
            "Color de las islas", "Island color", "Couleur des îles",
            "Color de énfasis", "Accent color", "Couleur d'accentuation",
            "Logo de Inicio y marca de la app activa.", "Start logo and active app mark.", "Logo Démarrer et repère de l'application active.",
            "Usar el color de énfasis de Windows", "Use the Windows accent color", "Utiliser la couleur d'accentuation de Windows",
            "El que elijas en Configuración > Personalización > Colores.", "The one you choose in Settings > Personalization > Colors.",
                "Celle choisie dans Paramètres > Personnalisation > Couleurs.",
            "Color del contenido", "Content color", "Couleur du contenu",
            "Texto e iconos del sistema.", "Text and system icons.", "Texte et icônes système.",
            "Automático", "Automatic", "Automatique",
            "Claro|ink", "Light", "Clair",
            "Oscuro|ink", "Dark", "Sombre",
            "Contorno", "Outline", "Contour",
            "Una línea fina alrededor de cada isla.", "A thin line around each island.", "Une ligne fine autour de chaque île.",
            "Reducir animaciones", "Reduce animations", "Réduire les animations",
            "Los cambios se muestran al instante, sin transiciones.", "Changes show instantly, without transitions.",
                "Les changements s'affichent aussitôt, sans transitions.",
            "Sin animaciones con batería", "No animations on battery", "Pas d'animations sur batterie",
            "Solo hay animaciones con el equipo enchufado.", "Animations only while plugged in.", "Animations uniquement sur secteur.",
            "Ámbar", "Amber", "Ambre",
            "Rosa", "Pink", "Rose",
            "Morado", "Purple", "Violet",

            // ---- Aplicaciones
            "Personaliza la isla de aplicaciones.", "Customize the apps island.", "Personnalisez l'île des applications.",
            "Marca de app abierta", "Open app mark", "Repère d'application ouverte",
            "Bajo el icono: una por ventana abierta, hasta cuatro.", "Under the icon: one per open window, up to four.",
                "Sous l'icône : un par fenêtre ouverte, jusqu'à quatre.",
            "Línea", "Line", "Ligne",
            "Punto", "Dot", "Point",
            "Ninguna", "None", "Aucun",
            "Al pasar el ratón", "On mouse over", "Au survol",
            "Cómo responde el icono.", "How the icon responds.", "Réaction de l'icône.",
            "Resaltar", "Highlight", "Surligner",
            "Ampliar", "Zoom", "Agrandir",
            "Nada", "Nothing", "Rien",
            "Separación entre iconos", "Space between icons", "Espace entre les icônes",
            "Iconos en gris", "Gray icons", "Icônes en gris",
            "Todos los iconos en un solo tono.", "All icons in a single tone.", "Toutes les icônes dans un seul ton.",
            "Rebote al abrir", "Bounce on launch", "Rebond au lancement",
            "El icono rebota mientras la app se abre.", "The icon bounces while the app opens.", "L'icône rebondit pendant l'ouverture.",
            "Botón de Inicio", "Start button", "Bouton Démarrer",
            "Sin él, la tecla Windows sigue abriendo Inicio.", "Without it, the Windows key still opens Start.",
                "Sans lui, la touche Windows ouvre toujours Démarrer.",
            "Anchura mínima", "Minimum width", "Largeur minimale",
            "La isla no encoge por debajo de esta anchura.", "The island never gets narrower than this.", "L'île ne rétrécit pas en dessous.",
            "Aplicaciones ocultas", "Hidden apps", "Applications masquées",
            "Mostrar todas", "Show all", "Tout afficher",
            "Grupos de aplicaciones", "App groups", "Groupes d'applications",
            "Clic derecho en una app ▸ Crear grupo. Hasta 4 por grupo.", "Right-click an app ▸ Create group. Up to 4 per group.",
                "Clic droit sur une application ▸ Créer un groupe. 4 au plus.",
            "Deshacer todos", "Ungroup all", "Tout dissoudre",
            "Ninguna. Se ocultan desde el menú de su icono en la barra.", "None. Hide them from their icon's menu on the bar.",
                "Aucune. Masquez-les depuis le menu de leur icône.",

            // ---- Información
            "Elige qué muestra la isla de información.", "Choose what the info island shows.", "Choisissez ce qu'affiche l'île d'informations.",
            "Red", "Network", "Réseau",
            "Icono de la conexión a Internet.", "Internet connection icon.", "Icône de la connexion Internet.",
            "Volumen", "Volume", "Volume",
            "La rueda del ratón sobre el icono lo sube y lo baja.", "The mouse wheel over the icon turns it up and down.",
                "La molette sur l'icône le monte et le baisse.",
            "Batería", "Battery", "Batterie",
            "Solo aparece en equipos con batería.", "Only shown on devices with a battery.", "Uniquement sur les appareils avec batterie.",
            "Porcentaje de batería", "Battery percentage", "Pourcentage de batterie",
            "La carga en números, junto al icono.", "The charge in numbers, next to the icon.", "La charge en chiffres, près de l'icône.",
            "Formato de la hora", "Time format", "Format de l'heure",
            "El de Windows, o siempre 24 o 12 horas.", "Windows's own, or always 24 or 12 hours.", "Celui de Windows, ou toujours 24 ou 12 heures.",
            "24 h", "24 h", "24 h",
            "12 h", "12 h", "12 h",
            "Fecha", "Date", "Date",
            "Bajo la hora.", "Below the time.", "Sous l'heure.",
            "Corta", "Short", "Courte",
            "Con día", "With day", "Avec jour",
            "Sin fecha", "No date", "Sans date",
            "Uso del procesador", "Processor usage", "Utilisation du processeur",
            "Porcentaje de CPU en uso. Al pulsarlo se abre el Administrador de tareas.", "CPU in use. Clicking it opens Task Manager.",
                "Processeur utilisé. Un clic ouvre le Gestionnaire des tâches.",
            "Uso de la memoria", "Memory usage", "Utilisation de la mémoire",
            "Porcentaje de RAM en uso.", "RAM in use.", "Mémoire utilisée.",
            "Controles multimedia", "Media controls", "Commandes multimédias",
            "Anterior, reproducir o pausar y siguiente.", "Previous, play or pause and next.", "Précédent, lecture ou pause et suivant.",
            "Avisos en la isla", "Notices on the island", "Avis sur l'île",
            "El volumen y la batería se muestran un momento en el sitio del reloj al cambiar.",
                "Volume and battery show briefly in place of the clock when they change.",
                "Le volume et la batterie s'affichent un instant à la place de l'horloge.",

            // ---- Comportamiento
            "Elige cómo responde la barra al ratón.", "Choose how the bar responds to the mouse.", "Choisissez comment la barre réagit à la souris.",
            "Clic central en una app", "Middle click on an app", "Clic central sur une application",
            "Con la rueda del ratón pulsada.", "Pressing the mouse wheel.", "En appuyant sur la molette.",
            "Nueva ventana", "New window", "Nouvelle fenêtre",
            "Rueda sobre una app", "Wheel over an app", "Molette sur une application",
            "Al girarla encima de una app abierta.", "When turned over an open app.", "En la tournant sur une application ouverte.",
            "Cambiar ventana", "Switch window", "Changer de fenêtre",
            "Vistas previas", "Previews", "Aperçus",
            "Miniaturas de las ventanas al pasar el ratón por una app abierta.", "Window thumbnails when hovering an open app.",
                "Miniatures des fenêtres au survol d'une application ouverte.",
            "Espera antes de esconderse", "Delay before hiding", "Délai avant de se masquer",
            "Para las islas ocultas, al apartar el ratón.", "For hidden islands, once the mouse leaves.", "Pour les îles masquées, quand la souris s'éloigne.",

            // ---- Temas
            "Cambia el aspecto de una vez o lleva tus ajustes a otro equipo.", "Change the look in one go or take your settings to another PC.",
                "Changez l'apparence d'un coup ou emportez vos réglages sur un autre PC.",
            "Clásico|theme", "Classic", "Classique",
            "Claro|theme", "Light", "Clair",
            "Cristal|theme", "Glass", "Verre",
            "Compacto|theme", "Compact", "Compact",
            "Flotante|theme", "Floating", "Flottant",
            "Las islas oscuras de siempre.", "The usual dark islands.", "Les îles sombres habituelles.",
            "Islas claras con el contenido en oscuro.", "Light islands with dark content.", "Îles claires avec le contenu en sombre.",
            "Transparentes, con un contorno fino.", "Transparent, with a thin outline.", "Transparentes, avec un contour fin.",
            "Islas más bajas e iconos más pequeños.", "Lower islands and smaller icons.", "Îles plus basses et icônes plus petites.",
            "Separadas de los bordes y con contorno.", "Away from the edges, with an outline.", "Écartées des bords, avec un contour.",
            "Aplicar", "Apply", "Appliquer",
            "Exportar", "Export", "Exporter",
            "Exportar ajustes", "Export settings", "Exporter les réglages",
            "Guarda tus ajustes en un archivo.", "Saves your settings to a file.", "Enregistre vos réglages dans un fichier.",
            "Importar", "Import", "Importer",
            "Importar ajustes", "Import settings", "Importer des réglages",
            "Sustituye tus ajustes por los de un archivo exportado.", "Replaces your settings with those of an exported file.",
                "Remplace vos réglages par ceux d'un fichier exporté.",
            "Restablecer todo", "Reset everything", "Tout réinitialiser",
            "Deja la aplicación como recién instalada. El idioma no cambia.", "Leaves the app as freshly installed. The language stays.",
                "Remet l'application à neuf. La langue ne change pas.",
            "Todos los ajustes volverán a como vienen de fábrica. ¿Continuar?", "All settings will go back to their defaults. Continue?",
                "Tous les réglages reviendront aux valeurs par défaut. Continuer ?",
            "No se ha podido guardar el archivo.", "The file could not be saved.", "Impossible d'enregistrer le fichier.",
            "Ese archivo no contiene ajustes de NeuTaskBar.", "That file has no NeuTaskBar settings.", "Ce fichier ne contient pas de réglages NeuTaskBar.",

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
            "Procesador: ", "Processor: ", "Processeur : ",
            "Memoria: ", "Memory: ", "Mémoire : ",

            // ---- menús de la barra
            "Tareas", "Tasks", "Tâches",
            "Aplicación", "Application", "Application",
            "Desanclar de la barra de tareas", "Unpin from taskbar", "Désépingler de la barre des tâches",
            "Anclar a la barra de tareas", "Pin to taskbar", "Épingler à la barre des tâches",
            "Ocultar de la barra de tareas", "Hide from taskbar", "Masquer de la barre des tâches",
            "Crear grupo", "Create group", "Créer un groupe",
            "Añadir al grupo: ", "Add to group: ", "Ajouter au groupe : ",
            "Grupo", "Group", "Groupe",
            "Sacar del grupo: ", "Remove from group: ", "Retirer du groupe : ",
            "Deshacer el grupo", "Ungroup", "Dissoudre le groupe",
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
