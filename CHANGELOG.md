# Cambios

Los cambios que nota quien usa NeuTaskBar, por versión. El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/).

## Sin publicar

### Añadido

- Idioma de la aplicación y de la barra: español, inglés o francés (por defecto, el de Windows).
- Ocultar la isla de aplicaciones, la de información o las dos hasta pasar el ratón por su sitio.
- Color de cada isla: predefinido, a medida o transparente, con el contenido en tinta clara u oscura según el fondo.
- **Apariencia**: alto de las islas, tamaño de los iconos y del texto, separación con los bordes, opacidad del color, seguir el modo claro u oscuro de Windows, color de énfasis (propio o el de Windows), color del contenido, contorno y reducción de animaciones.
- Relleno de la franja entre las islas con los colores de la ventana maximizada o, si se prefiere, con el color de las islas.
- Página **Posición**: unir las dos islas en una sola barra y moverlas arrastrándolas a cualquier esquina libre de la pantalla, también arriba. La isla de aplicaciones, cuando se mueve sola, también puede dejarse en el centro de arriba o de abajo.
- **Barra completa**: una sola barra de lado a lado, como la de Windows, en vez de dos islas.
- Página **Aplicaciones**: marca de app abierta (línea, punto o ninguna), efecto al pasar el ratón, separación entre iconos, iconos en gris, rebote al abrir, quitar el botón de Inicio, anchura mínima y apps ocultas (desde el menú de su icono).
- La marca de app abierta se repite una vez por ventana, hasta cuatro.
- **Grupos** de hasta cuatro apps en un solo botón: se crean con clic derecho en una app, y un clic en el grupo despliega sus apps.
- Página **Información**: mostrar u ocultar red, volumen y batería, porcentaje de batería, formato de la hora y de la fecha, uso de procesador y memoria, controles multimedia y avisos de volumen y batería en el sitio del reloj.
- Página **Comportamiento**: clic central para cerrar la ventana, rueda sobre una app para cambiar de ventana, quitar las vistas previas y espera antes de esconderse.
- Página **Temas**: cinco aspectos ya hechos, y exportar, importar o restablecer los ajustes.
- Botón **Restablecer** en cada página de personalización: devuelve sus ajustes a como vienen de fábrica, previa confirmación.
- Las páginas de la aplicación se desplazan cuando no caben en la ventana.
- Las islas siguen la posición, el tamaño, la alineación y la ocultación automática de la barra de tareas de Windows.

### Cambiado

- Las islas se pintan con transparencia real, así que los iconos ya no se aclaran sobre fondos claros.
- La barra original de Windows se deja invisible en lugar de forzarle la ocultación automática.
- Los ejecutables y los recursos generados dejan de guardarse en el repositorio.

### Corregido

- Los iconos de los menús de la barra se ven enteros: antes se recortaban a su cuadro y algunos perdían un trozo por arriba o por la izquierda.
- El hueco entre las islas continúa el color con el que la ventana termina de verdad. Antes lo tomaba unos píxeles más adentro, y si ahí había un panel de otro tono dejaba un recuadro que no casaba con el borde de la ventana.
- Bajo una ventana con el fondo dibujado (WhatsApp) o con texto junto al borde, el hueco entre las islas ya no se llena de rayas: cada panel se continúa con su color liso.
- Una app anclada con un acceso directo a su lanzador (Discord, Slack y otras que se actualizan solas) ya no sale dos veces, anclada y abierta.
- Con las islas arriba, la franja ya no estira la pestaña activa del navegador hasta el borde de la pantalla.

## 1.0

- Primera versión: barra de tareas de dos islas, ventana de la aplicación, inicio con Windows y desinstalador.
