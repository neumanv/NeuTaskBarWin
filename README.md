# NeuTaskBarWin

Personalizar la barra de tareas de Windows: reemplaza visualmente la barra por **dos islas** acrílicas pegadas a las esquinas inferiores de la pantalla.

- **Isla izquierda**: botón Inicio, apps fijadas y apps abiertas. Se ensancha según el número de apps.
- **Isla derecha**: flecha de iconos ocultos, red, volumen, batería, hora y fecha.
- El centro queda libre y transparente; el área de trabajo de Windows se ajusta (las ventanas maximizadas no quedan tapadas).

Iconos al tamaño exacto que pide el shell (los mismos que usa la barra de Windows, también para apps de la Tienda) y animaciones: deslizamiento al abrir/cerrar apps, aparición de iconos, ensanche de la isla, hover, pulsación, rebote al lanzar una app e indicador de ventana activa.

Windows 10 (1803+) y Windows 11. Sin dependencias: C# sobre .NET Framework 4.x (incluido en Windows) y Win32 puro.

## Archivos

En la carpeta principal:

| Archivo | Para qué sirve |
| --- | --- |
| `NeuTaskBar.exe` | La aplicación (ventana **NeuTaskBar**). Se abre con la bienvenida y el botón **Activar barra de tareas**. |
| `Uninstall.exe` | Desinstalador: detiene la barra, restaura la barra original de Windows y borra los ejecutables. |

El logo está en `assets\logo-source.png`; el icono (`assets\app.ico`) y la imagen de la ventana se generan a partir de él al compilar.

## Encontrarla desde Windows

Al compilar (`build.ps1`) o al abrir `NeuTaskBar.exe` se crea un acceso directo en el menú Inicio (`%AppData%\Microsoft\Windows\Start Menu\Programs\NeuTaskBar.lnk`) y una entrada en Configuración > Aplicaciones. Así basta con pulsar Windows y escribir "NeuTaskBar" para abrirla, sin ir a su carpeta. `NeuTaskBar.exe --register` repite ese registro y `Uninstall.exe` lo elimina.

## Compilar

```powershell
.\build.ps1          # genera NeuTaskBar.exe y Uninstall.exe en esta carpeta
.\build.ps1 -Run     # compila y abre la aplicación
```

## Uso

1. Abre `NeuTaskBar.exe` y pulsa **Activar barra de tareas**. El mismo botón pasa a **Desactivar barra de tareas**.
2. Cerrar la ventana no detiene la barra: sigue funcionando en segundo plano.

La ventana tiene navegación lateral: **Inicio** (activar/desactivar la barra e iniciar con Windows), **Apariencia** (reservada para las opciones visuales) y **Acerca de** (versión, carpeta de instalación y desinstalar).

### Añadir opciones nuevas

Cada sección es una clase `Page` (`src/SettingsForm.cs`) que se registra en `MainForm` con `AddPage(título, glifo, página)`. Los componentes reutilizables están en `src/Ui.cs`: `Card` + `SettingRow` (título, descripción y un control a la derecha), `ToggleSwitch`, `PillButton` y la barra lateral `NavBar`. Para que una opción afecte a la barra en marcha, el motor lee `%APPDATA%\NeuTaskBar\config.ini` (`src/Config.cs`).

| Acción | Resultado |
| --- | --- |
| Clic en Inicio | Abre el menú Inicio (clic derecho: menú Win+X) |
| Clic en un icono | Lanza la app, o la activa / minimiza (con varias ventanas alterna entre ellas) |
| Clic central en un icono | Abre una nueva instancia |
| Clic derecho en un icono | Abrir, cerrar ventanas, fijar/quitar de la barra |
| Arrastrar un icono de la isla | Cambia su orden (las fijadas se reordenan entre sí y se guarda; las abiertas no fijadas también entre sí) |
| Arrastrar un `.exe`/`.lnk` a la isla izquierda | Lo fija |
| Clic en la flecha `^` | Abre el panel real de Windows con los iconos ocultos de la bandeja (mismos iconos y menús) |
| Clic en red/volumen/batería | Panel de configuración rápida (Win+A); la rueda ajusta el volumen |
| Clic en el reloj | Calendario y notificaciones |
| Clic derecho en un icono | Menú nativo de Windows: nombre de la app, anclar/desanclar y cerrar ventana |
| Clic derecho en zona vacía | Administrador de tareas y configuración de la barra de tareas |
| Clic derecho en reloj / red / volumen / batería | Menú del elemento (fecha y hora, red, mezclador y sonido, energía) |
| Pasar el ratón por una app abierta | Vista previa de sus ventanas con el aspecto de Windows 11 (claro u oscuro según el sistema) |

Los menús de la barra son los nativos de Windows (sin entradas propias). Los controles de NeuTaskBar (**Abrir NeuTaskBar**, **Pausar** que devuelve la barra original, **Iniciar con Windows** y **Salir**) están en el menú de su icono en la bandeja de Windows (panel de iconos ocultos `^`) y en la ventana de la aplicación.

Línea de comandos: `NeuTaskBar.exe --engine` (inicia la barra sin abrir la ventana), `--quit`, `--restore` (recupera la barra de Windows si algo fallara), `--autostart on|off`.

## Desinstalar

Ejecuta `Uninstall.exe` (también aparece en Configuración > Aplicaciones tras abrir `NeuTaskBar.exe` por primera vez). Pregunta si quieres conservar tus ajustes y apps fijadas; `Uninstall.exe --silent` lo hace sin preguntar.

## Seguridad

La barra original se oculta (autohide + oculta) y se restaura al salir, pausar, cerrar sesión o si el proceso muere: una segunda instancia mínima (`--guard`) vigila el proceso principal y repone la barra aunque se mate a la fuerza.

## Configuración opcional

`%APPDATA%\NeuTaskBar\config.ini` (`clave=valor`):

```ini
height=48        # alto de las islas a 96 dpi
margin=0         # separación respecto a los bordes de la pantalla
iconsize=24      # tamaño de icono a 96 dpi
tint=8C1A1A1A    # tinte del acrílico, AARRGGBB (más alfa = más opaco)
```

Las apps fijadas se guardan en `%APPDATA%\NeuTaskBar\pins.txt` (la primera vez se importan de las fijadas en la barra de Windows).

## Limitaciones conocidas

- Solo se sustituye la barra del monitor principal; las de otros monitores quedan como están (con ocultación automática mientras la app corre).
- Los iconos de bandeja de terceros no se dibujan dentro de la isla (Windows 11 no expone una API para ello): la flecha `^` abre el panel real de Windows, que muestra los iconos que tengas ocultos. Los que tengas "siempre visibles" en la esquina de la barra original no aparecen en ese panel; en Configuración > Personalización > Barra de tareas > Otros iconos de la bandeja puedes ocultarlos para verlos ahí.
- Las listas de salto del menú del botón incluyen las tareas y categorías propias de cada app (por ejemplo "Más visitado" en Brave), pero no los archivos recientes ni anclados de las apps de Office/Explorador.
- La red muestra tipo de conexión (Wi-Fi/Ethernet/sin conexión) pero no la intensidad de señal.
