# Ficha de la tienda (español, es-ES)

Traducción de la ficha de la tienda en inglés (listing-en.md). Solo las secciones visibles para el usuario. Los nombres de productos y los enlaces no cambian.

## Nombre de la aplicación

Portal AI Host

## Descripción breve

Ejecuta un modelo de IA local en tu propio PC y compártelo con tus aplicaciones Portal. Con un clic se elige el mejor modelo para tu equipo, se descarga y queda listo. Tu texto se queda en este PC: sin cuenta, sin nube, sin suscripción.

## Descripción

Portal AI Host da a tu PC su propio modelo de IA y permite que tus otras aplicaciones lo usen.

En lugar de que cada aplicación descargue su propio modelo o envíe tu texto a un servicio en la nube, Portal AI Host mantiene un único modelo en tu PC y lo comparte con las aplicaciones que permites. Así, Voice Portal y las demás aplicaciones Portal pueden escribir resúmenes, actas y respuestas con un modelo más grande y mejor del que podrían incluir por sí solas.

Fácil de configurar
- En el primer inicio, la aplicación revisa tu PC (memoria, tarjeta gráfica, espacio libre en disco) y recomienda el mejor modelo que funcione bien en él, con una explicación clara.
- Un clic lo descarga. Las descargas se pueden pausar y reanudar, y cada archivo se comprueba antes de usarse.
- No hay nada más que instalar: el motor viene incluido. Sin Python, sin línea de comandos, sin herramientas adicionales.

Privado por diseño
- El modelo se ejecuta en este PC. Las indicaciones y las respuestas nunca salen de él.
- Sin cuenta y sin inicio de sesión.
- Sin analíticas. Los informes de errores opcionales nunca incluyen tus archivos ni lo que escribes, y puedes desactivarlos. Tus indicaciones nunca se guardan ni se registran.
- El host solo escucha en este PC (bucle local). Otros equipos de tu red no pueden acceder a él.

Tú tienes el control
- Una aplicación debe pedir permiso antes de usar el host, y decides: permitir, denegar o decidir más tarde. Puedes retirar el acceso de una aplicación en cualquier momento.
- El host indica si una aplicación está verificada (su editor), para que sepas quién lo pide.
- Elige dónde se guardan los modelos y elimina los que ya no necesites. El host solo borra los archivos que descargó él mismo.
- Iniciar con Windows es opcional y está desactivado hasta que lo actives.

Funciona con tu hardware
- Usa tu tarjeta gráfica mediante Vulkan (AMD, NVIDIA e Intel) y recurre al procesador cuando no hay una GPU adecuada.
- Libera memoria cuando el modelo está inactivo, o lo mantiene cargado si lo prefieres.

Para desarrolladores
- El host ofrece una API estándar compatible con OpenAI en 127.0.0.1 (chat completions con streaming, embeddings y una lista de modelos), así que las bibliotecas cliente existentes funcionan. Cada aplicación necesita primero la aprobación del usuario.

Los modelos se descargan desde las páginas de sus editores en Hugging Face. Cada modelo tiene su propia licencia; la aplicación solo ofrece modelos cuyas licencias permiten el uso comercial (Apache 2.0 o MIT).

## Características (Partner Center, «Características del producto», máximo 20, 200 caracteres cada una)

1. Ejecuta un modelo de IA en tu PC, sin necesidad de nube
2. Un solo modelo compartido por todas tus aplicaciones Portal
3. Recomienda el mejor modelo para tu hardware
4. Descarga con un clic, con pausa, reanudación y comprobación de archivos
5. Usa tu tarjeta gráfica (Vulkan), con recurso automático al procesador
6. Sin cuenta, sin inicio de sesión, sin suscripción
7. Sin analíticas; los informes de errores son opcionales y están depurados; las indicaciones nunca se guardan ni se registran
8. Escucha solo en este PC (bucle local)
9. Las aplicaciones deben pedir permiso; tú permites, deniegas o retiras el acceso
10. Indica si una aplicación que pide acceso está verificada
11. Libera memoria cuando está inactivo, o mantiene el modelo cargado
12. Elige la carpeta de tus modelos
13. Inicio opcional con Windows, con icono en la bandeja
14. Recuadro «Pruébalo» para comprobar que el modelo funciona
15. API local compatible con OpenAI para desarrolladores
16. Reparto justo cuando varias aplicaciones piden a la vez
17. Se recupera solo si el motor se detiene

## Términos de búsqueda (palabras clave, 7)

1. IA local
2. IA sin conexión
3. IA privada
4. LLM
5. modelo de IA
6. modelo de lenguaje
7. servidor de IA

Sin nombres de productos de terceros en las palabras clave (política de la tienda 10.1.3).

## Novedades de esta versión (1.0)

Primera versión. Descarga un modelo de IA local con un clic y compártelo de forma privada con tus aplicaciones Portal, en tu propio PC.

## Requisitos del sistema (texto de la ficha)

> Funciona mejor con 16 GB de memoria y una tarjeta gráfica con 8 GB de memoria de vídeo. Sin una tarjeta gráfica adecuada, el modelo se ejecuta en el procesador, que es más lento. Los PC Arm64 ejecutan el modelo en el procesador. Los modelos necesitan entre 2 y 20 GB de espacio libre en disco.

Mínimo: Windows 11 (versión 21H2, compilación 22000) o posterior, x64 o Arm64. Memoria: 8 GB. Espacio libre en disco: 5 GB (la aplicación en sí es pequeña; los modelos ocupan de 2 a 20 GB, y los modelos opcionales más grandes, hasta unos 140 GB).

Recomendado: 16 GB de memoria o más (32 GB o más para los modelos grandes). Una GPU compatible con Vulkan y 8 GB de memoria de vídeo o más (AMD, NVIDIA o Intel). 25 GB de espacio libre en disco.
