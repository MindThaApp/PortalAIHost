# Portal AI Host: guía del usuario

Portal AI Host ejecuta un modelo de IA en tu PC y lo comparte con tus aplicaciones Portal. Tus preguntas y tus archivos se quedan en tus propios equipos. Esta guía explica cada parte de la aplicación, qué hacen sus ajustes y qué hacer cuando algo sale mal.

Los lugares donde debería ir una imagen están marcados como `[screenshot: ...]`.

## Contenido

1. Inicio rápido
2. Requisitos del sistema, instalación, actualización y desinstalación
3. Moverse por la aplicación
4. Modelos
5. Aplicaciones y permisos
6. Pruébalo
7. Ajustes de rendimiento
8. Notificaciones
9. Permitir que otros dispositivos usen este PC
10. Dispositivos en este PC
11. Control de modelos y dos modelos a la vez
12. Usar tu PC de casa desde un portátil (modo cliente)
13. Fuera de casa: el relé y la clave de acceso al relé
14. Privacidad e informes de errores
15. Dónde se guardan tus datos, copias de seguridad y eliminación
16. Atajos de teclado
17. Solución de problemas y preguntas frecuentes
18. Próximamente
19. Soporte

---

## 1. Inicio rápido

1. Instala Portal AI Host desde Microsoft Store y ábrelo.
2. La ventana de bienvenida revisa tu PC (memoria, tarjeta gráfica, espacio libre en disco) y recomienda un modelo. Elige **Empezar con la configuración recomendada**, o **Descargar** para elegir el modelo tú mismo. **Elegir más tarde** omite este paso.
3. Busca el icono junto al reloj. Portal AI Host espera allí en silencio. Haz clic en él para ver un estado rápido.
4. Abre **Pruébalo** y haz una pregunta corta para comprobar que el modelo funciona.
5. La primera vez que una aplicación Portal quiera usar la IA, un aviso te preguntará si permitirlo o denegarlo.

[screenshot: la ventana de bienvenida con el modelo recomendado]

Si, en cambio, quieres que un portátil use la IA de tu PC de casa, elige **Usar mi PC de casa** en la ventana de bienvenida. Consulta la sección 12.

**Gratis o de pago:** Portal AI Host es gratuito. Ninguna función de esta guía requiere cuenta, inicio de sesión ni compra.

---

## 2. Requisitos del sistema, instalación, actualización y desinstalación

### Requisitos

| | Mínimo | Recomendado |
|---|---|---|
| Windows | Windows 11 (21H2, compilación 22000) o posterior, x64 o Arm64 | Lo mismo |
| Memoria | 8 GB | 16 GB o más (32 GB o más para los modelos grandes) |
| Gráficos | Ninguna (el modelo se ejecuta en el procesador, que es más lento) | Una tarjeta gráfica compatible con Vulkan y 8 GB de memoria de vídeo o más (AMD, NVIDIA o Intel) |
| Espacio libre en disco | 5 GB | 25 GB |

Los modelos ocupan de 2 a 20 GB cada uno. Los PC Arm64 ejecutan el modelo en el procesador. Se necesita conexión a Internet solo para descargar modelos y para el relé; una vez que un modelo está en tu PC, funciona sin conexión.

### Instalación

Instálala desde Microsoft Store. No hace falta nada más: ni Python, ni línea de comandos, ni otro software. El motor de IA forma parte de la aplicación.

### Iniciar con Windows

**Configuración > General > Iniciar con Windows** (predeterminado: desactivado, salvo que elijas «Empezar con la configuración recomendada» en la ventana de bienvenida, que lo activa). Cuando está activado, Portal AI Host espera en la bandeja después de iniciar sesión, para que las aplicaciones puedan usarlo de inmediato.
- Si el interruptor indica que solo está disponible en la aplicación instalada, estás ejecutando una copia de desarrollo.
- Si lo desactivaste en el Administrador de tareas (aplicaciones de inicio), vuelve a activarlo allí.
- Si tu organización lo establece, el interruptor está bloqueado.

### Actualización

La Store actualiza la aplicación. Tras una actualización, **Novedades** se abre una vez. Puedes volver a abrirlo desde el menú de la aplicación. Los modelos permanecen en su sitio entre actualizaciones.

### Desinstalación

Elimina la aplicación en **Configuración de Windows > Aplicaciones**. Los modelos de la carpeta predeterminada se guardan dentro de la carpeta de datos de la aplicación y se eliminan con ella. Una carpeta de modelos personalizada que hayas elegido no se toca. Consulta la sección 15 antes de desinstalar si quieres conservar algo.

---

## 3. Moverse por la aplicación

El lado izquierdo tiene cinco páginas.

| Página | Para qué sirve | Atajo |
|---|---|---|
| Inicio | Estado, modelo activo, uso de memoria, la aplicación que usa la IA en este momento | Ctrl+1 |
| Modelos | Descargar, usar y eliminar modelos | Ctrl+2 |
| Dispositivos y aplicaciones | Permitir o quitar aplicaciones Portal, pausarlas, establecer prioridad | Ctrl+3 |
| Pruébalo | Hacer una pregunta corta al modelo | Ctrl+4 |
| Configuración | Todo lo configurable | Ctrl+, |

Una sexta página, **Dispositivos de este PC**, muestra los portátiles y teléfonos emparejados con este PC (consulta la sección 10). El menú de la aplicación contiene Configuración, Atajos de teclado, Novedades y Acerca de.

**Inicio** indica si la IA está **Lista**, **Respondiendo**, **Preparando el modelo**, **En reposo** (el modelo se activa cuando una aplicación lo pide) o si **aún no hay modelo**. **AHORA** muestra qué aplicación recibe una respuesta y aproximadamente cuántas palabras por segundo. También muestra cuánta memoria y memoria gráfica usa el modelo, y un botón **Cargar ahora** / **Descargar ahora**. **HOY** cuenta las solicitudes de cada aplicación. Una tarjeta de estado muestra «Todo correcto» o enumera lo que requiere atención, cada elemento con una solución de un clic, como Cargar el modelo, Reiniciar el motor, Comprobar archivos o Abrir la configuración de rendimiento.

[screenshot: la página Inicio mientras una aplicación usa el modelo]

El icono de la bandeja abre una pequeña ventana emergente: modelo, memoria, última solicitud y cola, con los botones **Abrir aplicación** y **Descargar modelo**.

[screenshot: la ventana emergente de la bandeja]

Si el programa host no se está ejecutando, Inicio ofrece **Iniciar Portal AI Host**. Si se cerró inesperadamente más de una vez, una barra de **Modo seguro** indica que se inició con ajustes conservadores. Tus aplicaciones siguen funcionando; reinicia el PC si vuelve a pasar.

---

## 4. Modelos

Un modelo es la IA que hace el trabajo. **Modelos** (Ctrl+2) muestra lo que tienes y lo que puedes añadir.

### Modelo recomendado

El primer inicio y la página Inicio recomiendan «el mejor modelo para este PC», con un motivo claro, basado en tu memoria, tu memoria gráfica y tu espacio libre en disco. Cuando hay uno mejor disponible, una barra indica **Hay un modelo mejor disponible**. Tu modelo actual se mantiene hasta que el nuevo esté listo, y tú decides cuándo eliminar el antiguo.

### Descargar un modelo

1. Abre **Modelos**. Elige un modelo en **Modelos que puedes añadir**.
2. Pulsa **Descargar**. Una barra muestra el progreso y el tiempo restante.
3. **Pausar** y **Reanudar** están disponibles en cualquier momento. **Detener** descarta la parte ya descargada (tras una confirmación).
4. El host comprueba los archivos descargados antes de usarlos.
5. Cuando termine, pulsa **Usar este modelo**.

Las descargas se pueden reanudar tras reiniciar y se verifican archivo por archivo. Una descarga necesita espacio libre suficiente; si no lo hay, la página indica cuánto falta y ofrece **Cambiar carpeta en Configuración**. El PC se mantiene despierto mientras se descarga (Configuración > Rendimiento, activado por defecto).

[screenshot: la página Modelos con una descarga en curso]

Un modelo marcado como «Este modelo puede ser demasiado grande para este PC» se puede descargar igualmente, pero puede funcionar lento o no caber.

### Detalles, comprobar archivos, volver a descargar, eliminar

- **Detalles** muestra para qué sirve el modelo, qué tener en cuenta, la memoria que necesita, su licencia, cuándo se usó por última vez y cuánto espacio ocupa.
- **Comprobar archivos** vuelve a verificar un modelo en el disco. Nunca elimina nada. Un modelo dañado se marca como **Dañado**.
- **Volver a descargar** (para un modelo dañado) elimina la copia dañada y la descarga de nuevo. No se elimina nada más.
- **Eliminar** quita un modelo de este PC tras una confirmación que indica cuánto espacio liberará. Si el modelo está en uso, el host primero lo descarga y pregunta qué usar a continuación. Solo se eliminan los archivos que descargó el host.

### Añadir tu propio modelo

**Añadir tu propio modelo...** te permite elegir un archivo `.gguf`. El host crea su propia copia en la carpeta de modelos. Tu original nunca se modifica ni se mueve. La fila se marca como **Añadido por ti**, y el host no puede calcular cuánta memoria necesita, así que pruébalo y comprueba. **Eliminar** en un modelo así solo borra la copia del host.

### Liberar espacio

**Liberar espacio** lista los restos que creó la propia aplicación: descargas incompletas que no se pueden reanudar, copias antiguas que quedaron tras mover tus modelos y modelos que una comprobación detectó como dañados. Confirmas antes de que se elimine nada. Los modelos que usas y los archivos que añadiste tú mismo nunca aparecen en la lista. Las descargas incompletas que aún puedes reanudar se conservan durante una semana.

### Carpeta de modelos y mover tus modelos

**Configuración > Carpeta de modelos** indica dónde van las nuevas descargas. Deja el campo vacío para la carpeta predeterminada, o escribe una ruta completa como `D:\Modelos` y pulsa Intro. Los modelos ya descargados no se mueven ni se eliminan: se quedan donde están y siguen listados. Para moverlos, acepta **Mover mis modelos allí**. Se copian, se comprueban y solo entonces se eliminan las copias antiguas. El modelo en uso se descarga mientras esto se ejecuta, y **Cancelar movimiento** deja todo donde estaba.

### Presupuesto de memoria y barras

Las barras **Presupuesto de memoria** de la página Modelos muestran la RAM y la memoria gráfica: cuánto usan los modelos cargados, cuánto se reserva mientras se carga un modelo y cuánto queda libre según tus límites (consulta la sección 7). Una carga que superaría tu límite de memoria se rechaza con un motivo claro antes de detener nada.

[screenshot: las barras de Presupuesto de memoria con dos modelos cargados]

### Ubicación: tarjeta gráfica, memoria o solo procesador

Dónde se ejecuta un modelo lo decide **Configuración > Rendimiento > Ejecutar en** (Automático, Tarjeta gráfica, Procesador). Un modelo que no cabe en la tarjeta gráfica se ejecuta en parte en el procesador (más lento). Consulta también la ubicación por modelo en la sección 11, que incluye **Solo procesador**.

### Límites y gratis frente a pago

- Todos los modelos de la lista son gratuitos para uso comercial (licencias Apache 2.0 o MIT); cada modelo muestra su propia licencia.
- Los modelos se descargan desde las páginas de sus editores en Hugging Face. El host solo acepta una lista de modelos de confianza y firmada. Si no puede cargar esa lista, una barra indica **Lista de modelos no disponible**; los modelos que ya están en este PC siguen funcionando.

### Solución de problemas

- *No aparecen modelos para descargar:* comprueba tu conexión a Internet y pulsa **Reintentar** en Inicio.
- *La descarga se detiene:* pulsa **Reanudar**; si falta espacio libre, libera espacio o cambia la carpeta.
- *Modelo «puede ser demasiado grande»:* elige uno más pequeño, o reduce la carga en Configuración > Rendimiento.

---

## 5. Aplicaciones y permisos

Abre **Dispositivos y aplicaciones** (Ctrl+3).

- **Esperando tu respuesta:** una aplicación que pidió usar la IA aparece aquí y también como aviso («¿Permitir que Voice Portal use la IA en este PC?»). Elige **Permitir**, **No permitir** o **Decidir más tarde**. El aviso indica si la aplicación es una aplicación Portal verificada, una aplicación instalada que no es de Portal, o una aplicación no verificada (en ese caso indica desde dónde se ejecuta; permítela solo si la iniciaste tú).
- **Aplicaciones que pueden usar tu host de IA:** cada aplicación permitida muestra sus solicitudes de hoy y cuándo se usó por última vez.
  - **Pausar** rechaza nuevas solicitudes hasta que reanudes; la aplicación mantiene su acceso.
  - **Prioridad** (Alta, Normal, Baja) decide quién va primero cuando varias aplicaciones piden a la vez. Las solicitudes interactivas van antes que el trabajo por lotes, y una solicitud que espera mucho sube de posición para que una aplicación de prioridad baja nunca se quede sin atender.
  - **Quitar acceso** detiene la aplicación hasta que vuelvas a permitirla. **Deshacer** aparece un momento.

Las aplicaciones deben pedir permiso antes de poder usar el host. El host solo escucha en este PC, a menos que actives compartirlo con otros dispositivos (sección 9).

[screenshot: la página Dispositivos y aplicaciones con una aplicación en espera y una aplicación permitida]

Para desarrolladores: el host ofrece una API local compatible con OpenAI en `http://127.0.0.1:41870` (chat completions con streaming, embeddings, lista de modelos). Consulta `api-v1.md`.

**Dónde se guarda:** las aplicaciones permitidas y sus tokens se guardan en la carpeta de datos del host, protegida para tu usuario de Windows. Quitar el acceso de una aplicación elimina su token.

---

## 6. Pruébalo

**Pruébalo** (Ctrl+4) comprueba que el modelo funciona. Escribe una pregunta corta y pulsa **Preguntar** (o Ctrl+Intro). **Detener** termina la respuesta, **Borrar** vacía la página. Se ofrecen preguntas sugeridas. Muestra cuánto tardaron las primeras palabras y la velocidad en tokens por segundo. Nada de lo que escribas aquí se guarda.

[screenshot: Pruébalo con una respuesta y la línea de tiempos]

---

## 7. Ajustes de rendimiento

**Configuración > Rendimiento** evita que la IA ralentice tu PC. Los cambios se aplican de inmediato; un modelo que ya no cabe se vuelve a cargar con los nuevos límites o se descarga (sigue en el disco). **Restablecer valores predeterminados** restaura la sección.

| Ajuste | Qué hace | Predeterminado |
|---|---|---|
| Rendimiento | Un preajuste: **Silencioso** (cerca de la mitad de velocidad, el PC sigue respondiendo), **Equilibrado** (buena velocidad, tus programas van primero), **Rápido** (respuestas más veloces, otros programas pueden ralentizarse), **Personalizado** | Equilibrado |
| Memoria máxima para la IA | La IA nunca usa más memoria que esto | 80 % de la memoria del PC |
| Dejar siempre libre | Memoria que se reserva libre para tus otros programas | Lo establece el preajuste |
| Ejecutar en | **Automático** (tarjeta gráfica cuando ayuda), **Tarjeta gráfica** (lo más rápido, si el modelo cabe), **Procesador** (más lento, deja libre la tarjeta gráfica para juegos) | Automático |
| Memoria gráfica para la IA | Un límite de la memoria gráfica que puede usar la IA | Lo establece el preajuste |
| Núcleos del procesador | Menos núcleos mantienen tu PC receptivo | Lo establece el preajuste |
| Prioridad | **Baja** (tu trabajo primero) o **Normal** | Baja |
| Uso máximo del procesador | Un tope para el uso del procesador | Sin límite (100 %) |
| Reducir actividad cuando | La IA se ejecuta con la prioridad más baja, usa como máximo la mitad de tu procesador y vuelve a la normalidad cuando termina. Puedes elegir cualquiera de estas opciones: hay un juego o un programa a pantalla completa en marcha, el PC funciona con batería, una aplicación Portal está ocupada con trabajo en vivo, como el dictado | Programa a pantalla completa, batería y aplicación ocupada activados |

En **Liberar memoria cuando esté inactivo** dentro de **Configuración > General** eliges **Mantener el modelo cargado**, **Tras 5 minutos** o **Tras 10 minutos** (10 minutos por defecto, 5 con batería). El modelo vuelve a cargarse la próxima vez que una aplicación lo pida. Mantenerlo cargado responde más rápido, pero usa memoria todo el tiempo.

Las indicaciones de los deslizadores dicen si la memoria elegida permite que un modelo quepa por completo en la tarjeta gráfica, en parte en el procesador o solo en el procesador, y ofrecen un modelo más pequeño cuando el actual ya no cabe.

[screenshot: Configuración > Rendimiento]

**Solución de problemas:** las respuestas lentas en un PC con tarjeta gráfica suelen significar que el modelo solo cabe en parte en la tarjeta. Elige un modelo más pequeño o sube la Memoria gráfica para la IA. Si tu PC se entrecorta mientras lo usas, elige Silencioso o baja el Uso máximo del procesador.

---

## 8. Notificaciones

**Configuración > Notificaciones** controla los mensajes breves de Windows que aparecen cuando algo necesita tu atención y la ventana está cerrada. Permanecen en silencio mientras hay un programa a pantalla completa delante, y nunca se repiten más de una vez cada pocos minutos.

| Interruptor | Significado |
|---|---|
| Termina o falla una descarga de modelo | Activado por defecto |
| Una aplicación pide aprobación | Activado por defecto |
| El motor de IA se detiene o falla una comprobación | Activado por defecto |
| Un dispositivo se empareja o se conecta desde fuera de casa | Activado por defecto (las alertas de la sección 10) |

Cada uno se puede desactivar por separado. **Restablecer valores predeterminados** de la misma página los restaura.

---

## 9. Permitir que otros dispositivos usen este PC

**Configuración > Otros dispositivos.** Desactivado por defecto. Cuando está activado, tu propio portátil u otro PC de tu red doméstica puede pedir usar la IA de este PC. Un dispositivo debe emparejarse primero con un código, y cada aplicación de él sigue necesitando tu permiso.

### Emparejar un dispositivo

1. Activa **Permitir que otros dispositivos usen la IA de este PC**. Windows puede pedirte que permitas una regla de red.
2. Pulsa **Emparejar un dispositivo**. Aparecen un código y un código QR, y **Copiar enlace** copia un enlace de emparejamiento.
3. En el otro dispositivo, abre su aplicación Portal (o Portal AI Host en modo cliente, sección 12), elige conectarte a un PC y luego escanea el código, escríbelo o pega el enlace.
4. Comprueba que está hablando con este PC antes de enviar nada, y luego muestra el nombre del PC y una huella para confirmar.

El código funciona una sola vez, durante 5 minutos, para un único dispositivo. Los intentos erróneos lo gastan; crea uno nuevo con **Emparejar un dispositivo**. **Cancelar emparejamiento** lo termina.

[screenshot: el código de emparejamiento y el código QR]

### Dispositivos emparejados

Cada dispositivo emparejado aparece con su hora de último uso y un botón **Quitar**. Quitar un dispositivo corta su acceso al instante, y sus solicitudes en curso terminan. Para volver a tener acceso debe emparejarse de nuevo. Se pueden emparejar como máximo **16** dispositivos.

### Otros ajustes de esta tarjeta

- **Llegar también a este PC desde fuera de casa** (el relé), consulta la sección 13. Predeterminado: activado una vez que los otros dispositivos están habilitados.
- **Clave de acceso al relé**, consulta la sección 13. Predeterminado: desactivado.
- **Los dispositivos pueden reemplazar modelos**, consulta la sección 11. Predeterminado: desactivado.

**Qué viaja por la red:** las solicitudes y respuestas viajan cifradas entre tus propios dispositivos. La conexión de la red doméstica usa un certificado que el dispositivo fijó al emparejarse. Nada va a un servidor nuestro, salvo, de forma opcional, el relé ciego (sección 13).

**Solución de problemas:** *Un dispositivo no encuentra este PC:* comprueba que ambos están en la misma red, que este PC está encendido y que el host se está ejecutando. *El código dice «no es correcto» o «ha caducado»:* crea uno nuevo. *«No está en tu red doméstica»:* empareja primero en casa.

---

## 10. Dispositivos en este PC

La página **Dispositivos de este PC** muestra una tarjeta por cada portátil emparejado. Es el lugar para ver qué está haciendo cada portátil y limitarlo.

[screenshot: Dispositivos de este PC con dos tarjetas de dispositivo]

### Qué muestra una tarjeta

- Estado (En línea, Inactivo, Sin conexión), ruta (Red doméstica o Relé (fuera de casa)) y última actividad.
- Qué se está ejecutando ahora y qué está esperando.
- Solicitudes y tokens de hoy, con una barra de 7 días, por aplicación.
- El modelo en uso, quién lo cargó, y la velocidad en tokens por segundo y el tiempo hasta el primer byte.

### Controles

| Control | Qué hace |
|---|---|
| Pausar / Reanudar | Pausa todo el dispositivo, o una de sus aplicaciones. El portátil recibe una respuesta clara de «pausado» y usa su propia alternativa, si has definido una. |
| Cambiar nombre | Cambia el nombre que se muestra del dispositivo |
| Límites | Prioridad, límites diarios, horas de silencio y modelos permitidos (abajo) |
| Control de modelos (interruptor) | Permite que este dispositivo cargue y descargue modelos. Consulta la sección 11 |
| Recuperar | Descarga el modelo que cargó este dispositivo. Vuelve la elección propia de este PC. No se toca ningún archivo de modelo. |
| Olvidar | Elimina el dispositivo. Su acceso se detiene al instante y se borra su historial de uso. Los modelos no se tocan. |

**Límites** (bajo el botón **Límites** de la tarjeta, y luego **Guardar**):

- **Misma prioridad que las aplicaciones de este PC.** Desactivado (predeterminado): las aplicaciones propias de este PC van primero. Activado: el dispositivo espera en la misma cola.
- **Solicitudes por día** y **Tokens por día.** Predeterminado: sin límite. Cuando se agotan, se le indica al dispositivo que vuelva después de medianoche (hora de este PC).
- **Horas de silencio**, con **Desde** y **Hasta** (hora de este PC). El dispositivo no recibe respuestas en esas horas. Predeterminado: desactivado.
- **Modelos que este dispositivo puede usar.** Predeterminado: todos los modelos.

### Modo juego

En la parte superior de la página. **Predeterminado: desactivado.** Cuando está activado, las solicitudes de otros dispositivos reciben la respuesta de que este PC está ocupado mientras juegas, para que los juegos conserven la tarjeta gráfica. Este PC cuenta como «jugando» cuando Windows informa de un juego a pantalla completa, o cuando otro programa mantiene ocupado al 40 % o más el motor 3D de la tarjeta gráfica durante unos 10 segundos; por tanto, los juegos en ventana o en ventana sin bordes también cuentan. Deja de contar unos 30 segundos después de que termine la carga. Si los contadores gráficos no están disponibles en tu PC, solo se usa la comprobación de pantalla completa. El portátil recurre entonces a su propia configuración.

### Alertas

Aparece una notificación de Windows cuando se empareja un nuevo dispositivo y cuando un dispositivo se conecta desde fuera de casa a través del relé. Desactívalas en **Configuración > Notificaciones > Un dispositivo se empareja o se conecta desde fuera de casa**.

### Datos

Las cifras de uso son solo recuentos (solicitudes y tokens por aplicación y por día, se guardan 7 días). No se guarda ningún texto de indicaciones ni de respuestas. **Olvidar** elimina el archivo de uso de ese dispositivo y nada más.

---

## 11. Control de modelos y dos modelos a la vez

Por defecto, un portátil emparejado usa el modelo que este PC tenga cargado. Puedes dar más a un dispositivo.

### Control de modelos

**Configuración > Otros dispositivos**, en la fila del dispositivo: **Control de modelos** (predeterminado: desactivado). Con él activado, el portátil puede:
- cargar y descargar modelos instalados en este PC,
- elegir dónde se ejecuta un modelo (Automático, Tarjeta gráfica, Memoria del sistema o Solo procesador),
- elegir qué modelos de este PC usa cada una de sus propias aplicaciones.

Nunca puede descargar, eliminar, comprobar o importar modelos, cambiar ajustes o tocar tus archivos, con o sin esta concesión. Si lo desactivas, la siguiente llamada del dispositivo se rechaza. Quitar el dispositivo elimina la concesión, y volver a emparejarlo no la restaura.

El PC muestra quién cargó qué: «Cargado por <dispositivo>: <modelo> (tarjeta gráfica)». **Recuperar** (en la tarjeta y en Configuración) descarga el modelo que cargó un dispositivo. Vuelve la elección propia de este PC y se carga en la siguiente solicitud.

### Dos modelos a la vez

Con el Control de modelos, un portátil puede mantener un segundo modelo cargado en este PC, por ejemplo uno en la tarjeta gráfica y otro en la memoria del sistema.
- Tu propio modelo se queda en la primera ranura. El modelo de un dispositivo va a la segunda, así que un dispositivo nunca desplaza tu modelo solo para obtener el suyo.
- Cada ranura tiene su propia cola, de modo que dos modelos pueden responder al mismo tiempo. Una solicitud que nombra el segundo modelo va a él; todo lo demás va al tuyo.
- Antes de cargar, el host cuenta lo que ya usa la otra ranura frente a tus límites. Un segundo modelo que no cabe se rechaza («memoria insuficiente»). Nada se detiene en silencio.
- Tus propias decisiones siempre tienen prioridad. Si eliges otro modelo, descargas uno, o tu propio modelo necesita la memoria, el modelo del dispositivo se detiene y se le avisa.
- El segundo modelo de un dispositivo nunca se mantiene cargado para siempre; se descarga como cualquier otro cuando está inactivo.
- **Solo procesador** ejecuta un modelo sin capas gráficas, usando aproximadamente la mitad del procesador; por ejemplo, un modelo auxiliar en el procesador mientras otro usa la tarjeta gráfica.

[screenshot: la página Modelos con dos modelos cargados por propietarios distintos]

### Los dispositivos pueden reemplazar modelos

**Configuración > Otros dispositivos > Los dispositivos pueden reemplazar modelos** (predeterminado: desactivado). Desactivado: un dispositivo no puede descargar un modelo que usan tus propias aplicaciones. Activado: sí puede, y tus aplicaciones vuelven a cargar su modelo en su siguiente solicitud. Con dos dispositivos, el primero que cargó conserva su modelo.

---

## 12. Usar tu PC de casa desde un portátil (modo cliente)

El modo cliente hace que Portal AI Host en un portátil use la IA de otro de tus PC en lugar de ejecutar un modelo por sí mismo. Las aplicaciones del portátil funcionan como de costumbre.

### Configuración

1. En el PC de casa, activa **Permitir que otros dispositivos usen la IA de este PC** y pulsa **Emparejar un dispositivo** (sección 9). Pulsa **Copiar enlace** y envía el enlace al portátil.
2. En el portátil, abre **Configuración > PC de casa** (o elige **Usar mi PC de casa** en la ventana de bienvenida).
3. En **Dónde se ejecuta la IA**, elige **Usar mi PC de casa**. Se abre una ventana **Usar mi PC de casa**. Pega el enlace con **Pegar enlace**, pulsa **Siguiente**, comprueba el nombre del PC y la huella, y luego **Emparejar**. Pon nombre al portátil.
4. Inicio muestra ahora el nombre del PC y un estado: **Conectado - red doméstica**, **Conectado - mediante relé**, **Conectando...** o **No disponible**.

[screenshot: la ventana de emparejamiento en el portátil]

### Ajustes (Configuración > PC de casa)

| Ajuste | Opciones | Predeterminado |
|---|---|---|
| Dónde se ejecuta la IA | Este PC / Usar mi PC de casa | Este PC |
| Si el PC de casa no está disponible | **Ninguno** (las aplicaciones usan su IA integrada) / **Usar el modelo de este portátil** (se carga cuando hace falta) | Ninguno |
| Conexión fuera de casa | **Automática** / **Manual** (entonces un interruptor **Usar relé** se aplica solo a esta sesión y se restablece al cerrar la aplicación) | Automática |

- La primera vez que no se puede acceder al PC de casa, se te pregunta una vez qué hacer; puedes cambiarlo aquí en cualquier momento.
- **Emparejar PC** vuelve a emparejar; **Desemparejar** olvida el PC (los modelos y las descargas se mantienen como están); **Cambiar PC** (en Inicio) abre estos ajustes; **Ejecutar IA aquí** (en Inicio) sale del modo cliente y conserva el emparejamiento guardado.
- Con el Control de modelos concedido por el PC de casa, aparece **Modelos en <nombre del PC>** con botones de Cargar, Descargar y ubicación, además de **Modelo del PC para cada aplicación**. Sin esa concesión, la página dice: «Pide al propietario de <nombre del PC> que active el Control de modelos para este portátil».
- Un portátil en modo cliente no acepta conexiones de otros dispositivos y no puede servir de intermediario hacia tu PC de casa.

### Qué pasa con los modelos propios de este portátil

Al entrar en modo cliente, el modelo cargado en el portátil se descarga para liberar memoria. Los archivos de modelos, las descargas y la carpeta de modelos no se tocan. Al salir del modo cliente, el modelo se vuelve a cargar cuando se necesita.

### Solución de problemas

- **No disponible:** comprueba que el PC de casa está encendido y en la misma red, o activa el relé.
- **«Este portátil fue eliminado del PC»:** el PC de casa lo quitó. Elige **Emparejar de nuevo**.
- **«La identidad de <PC> ha cambiado»:** el certificado del PC ya no coincide con lo que guardó el portátil. Si no lo esperabas, no vuelvas a emparejar. Si lo esperabas, desemparejar y luego emparejar de nuevo.
- **El emparejamiento desde fuera de casa falla:** empareja primero en casa; el enlace de emparejamiento debe apuntar a una dirección de tu red doméstica.
- **Pausado, horas de silencio, un juego en marcha o un límite diario agotado:** el PC le indica al portátil el motivo; el portátil usa su alternativa.

**Dónde se guarda:** el portátil guarda su emparejamiento en la carpeta de datos del host, dentro de `client\`. La clave del dispositivo y los tokens están protegidos para tu usuario de Windows. **Desemparejar** los elimina. Las copias de seguridad de esa carpeta no se pueden restaurar en otro usuario de Windows.

---

## 13. Fuera de casa: el relé y la clave de acceso al relé

El relé permite que un dispositivo emparejado llegue a tu PC desde cualquier lugar. **Configuración > Otros dispositivos > Llegar también a este PC desde fuera de casa.**

- Tu PC se conecta hacia fuera con un pequeño servicio de relé. El relé solo transmite bytes cifrados que no puede leer, y no guarda contenido. Cada dispositivo debe seguir emparejado y firma cada solicitud.
- La línea de estado muestra **Accesible desde fuera de casa**, **Solo red doméstica**, **Conectando con el relé...** o **No se puede conectar con el relé**.
- Funciona a través del puerto web habitual, así que la mayoría de las redes de oficina lo permiten.
- **Dispositivos de empresa:** el contenido del trabajo irá a tu PC personal. Consulta antes las normas de tu empleador.

### Clave de acceso al relé

**Configuración > Otros dispositivos > Clave de acceso al relé** (predeterminado: desactivado). Cuando está activada, solo los dispositivos emparejados con la clave incluida en el código de emparejamiento pueden llegar a este PC a través del relé, lo que impide que desconocidos gasten las ranuras del relé de tu PC.

- Actívala **antes de emparejar**. Los dispositivos emparejados antes deben volver a emparejarse para usar el relé (la red doméstica sigue funcionando).
- **Nueva clave** crea una clave nueva tras una confirmación. Cada dispositivo emparejado debe volver a emparejarse para usar el relé; en casa no cambia nada. Quitar un dispositivo no cambia la clave, así que usa **Nueva clave** después de quitar un dispositivo en el que no confíes.
- Si el archivo de la clave no se puede usar, el relé se queda desactivado en lugar de abierto, y la red doméstica sigue funcionando. Un archivo de clave dañado se aparta con un nombre `.bad` y nunca se sobrescribe.
- Si el interruptor no se mantiene activado, es posible que el servicio de relé deba actualizarse por su parte. Vuelve a desactivar el interruptor.

**Límites:** el relé limita la velocidad y la cantidad que un dispositivo puede enviar, para que un solo dispositivo no sature el servicio. El uso normal está muy por debajo de estos límites.

---

## 14. Privacidad e informes de errores

- **Sin analíticas ni seguimiento de uso.** Solo un informe de error puede salir de tu PC, y únicamente si lo dejas activado.
- **Tu texto se queda en tus equipos.** El host nunca guarda ni registra las indicaciones ni las respuestas. Los registros solo contienen recuentos, tiempos y estados.
- **Los informes de errores están activados por defecto, y puedes desactivarlos.** Si la ventana o el host en segundo plano se bloquea, un breve informe nos ayuda a corregir el fallo. Desactívalo en cualquier momento en **Configuración > Privacidad > Enviar informes de errores**; se aplica al instante. La ventana de bienvenida ofrece la misma opción, marcada por defecto, antes de enviar nada.
  - **Se envía:** el tipo de error y dónde ocurrió (nombres de funciones y módulos), las versiones de la aplicación y de Windows, si es una compilación de la Store y si el error procede de la ventana o del host.
  - **Nunca se envía:** las indicaciones o las respuestas, lo que escribas, nombres de archivos o rutas de carpetas, tu nombre de equipo o de usuario, qué aplicaciones usan el host, ni tu dirección IP. El texto del error se descarta.
  - **Se conservan 90 días**, y después se eliminan.
  - **Qué se envía** (Configuración > Privacidad) abre la página de privacidad.
- **Todo está cifrado** cuando sale de tu PC hacia otro de tus dispositivos.
- **Política de privacidad:** Acerca de > **Política de privacidad**.
- Preguntas, o una petición para eliminar un informe: mindthaapp@gmail.com.

[screenshot: Configuración > Privacidad con el interruptor de informes de errores]

---

## 15. Dónde se guardan tus datos, copias de seguridad y eliminación

Todo está en la carpeta de datos propia de la aplicación. **Acerca de > Abrir carpeta de datos** la abre.

| Qué | Dónde (dentro de la carpeta de datos) | ¿Hacer copia? |
|---|---|---|
| Modelos descargados | `models\`, o `Portal AI Host Models\` dentro de la carpeta que elegiste en Configuración | Opcional. Se pueden volver a descargar. Son grandes. |
| Configuración | `settings.json` | Sí |
| Aplicaciones permitidas y sus tokens | El almacén de aplicaciones del host | No portable (protegido para tu usuario de Windows) |
| Dispositivos emparejados, límites, estadísticas de uso | El almacén de dispositivos del host y un pequeño archivo de estadísticas por dispositivo | No portable |
| Emparejamiento del portátil (modo cliente) | `client\` | No portable. Empareja de nuevo en un PC nuevo. |
| Registros | `logs\` (rotativos, limitados a 10 MB) | No |
| Archivo de descubrimiento para aplicaciones | `discovery\host.json` | No |

**Copia de seguridad:** cierra Portal AI Host y luego copia la carpeta de datos. La configuración y los modelos se pueden restaurar copiándolos de nuevo. Los permisos de las aplicaciones, los emparejamientos de dispositivos y los tokens están protegidos para tu usuario de Windows y para tu PC, así que en un PC nuevo vuelves a permitir las aplicaciones y a emparejar los dispositivos.

**Eliminar:**
- *Un modelo:* **Modelos > Eliminar**. Solo se borran los archivos que descargó el host.
- *Restos:* **Modelos > Liberar espacio**.
- *Un dispositivo:* **Olvidar** (Dispositivos de este PC) o **Quitar** (Configuración).
- *El acceso de una aplicación:* **Quitar acceso** (Dispositivos y aplicaciones).
- *Todo:* desinstala la aplicación. Una carpeta de modelos personalizada que elegiste se deja en su sitio; borra tú mismo su carpeta `Portal AI Host Models` si quieres que desaparezca.

La aplicación nunca elimina nada que no haya creado, nunca sobrescribe tus archivos, y un modelo que añadiste tú solo se elimina como copia propia del host (tu archivo original no se toca).

---

## 16. Atajos de teclado

| Teclas | Acción |
|---|---|
| Ctrl+1 | Inicio |
| Ctrl+2 | Modelos |
| Ctrl+3 | Dispositivos y aplicaciones |
| Ctrl+4 | Pruébalo |
| Ctrl+, | Configuración |
| F1 | Acerca de |
| Ctrl+Intro | Preguntar, en Pruébalo |
| Esc | Cerrar Novedades o un cuadro de diálogo |

La lista también está en el menú de la aplicación > **Atajos de teclado**.

Apariencia: **Configuración > Apariencia** ofrece Tema (predeterminado: tu ajuste de Windows), Material de la ventana (Mica), Esquinas (Redondeadas), Densidad (Estándar), Tamaño del texto (Normal) y Animaciones (Seguir a Windows). El modo de alto contraste de Windows siempre usa colores sólidos del sistema.

---

## 17. Solución de problemas y preguntas frecuentes

**El host dice que no se está ejecutando.** Pulsa **Iniciar Portal AI Host** en Inicio. Si no puede iniciarse, reinicia el PC; si eso falla, vuelve a instalar desde la Store.

**«Otro usuario de Windows de este PC ya está ejecutando Portal AI Host.»** Pide a ese usuario que lo cierre, o inicia sesión como él.

**Las respuestas son lentas.** Consulta la sección 7. Un modelo demasiado grande para tu memoria gráfica se ejecuta en parte en el procesador.

**Una aplicación dice que no hay IA disponible.** Comprueba que la aplicación está permitida (Dispositivos y aplicaciones), que no está pausada, y que hay un modelo instalado que no es demasiado grande para el PC.

**La primera respuesta es lenta y las siguientes son rápidas.** El modelo estaba en reposo. Se carga con la primera solicitud. Elige **Mantener el modelo cargado** en Configuración > General para evitarlo.

**¿Se envían mis datos a algún sitio?** No. Las indicaciones y las respuestas se quedan en tus dispositivos. Solo los informes de errores pueden salir (sección 14), y puedes desactivarlos.

**¿Puedo usarlo sin Internet?** Sí, una vez descargado un modelo.

**¿Cuesta algo?** No.

**¿Qué modelo debo elegir?** El recomendado. Elige uno más pequeño si tu PC va lento, o uno más grande si quieres mejores respuestas y tienes la memoria.

**La descarga se queda en «Comprobando la descarga».** Espera. Los archivos grandes tardan un minuto en comprobarse.

**El emparejamiento dice «demasiados dispositivos».** El límite es de 16 dispositivos. Quita uno.

**¿Dónde está el icono de la bandeja?** Haz clic en la flecha junto al reloj para mostrar los iconos ocultos.

Si aún no lo consigues, usa **Acerca de > Copiar versión**, escribe a mindthaapp@gmail.com y pega la versión en tu mensaje.

---

## 18. Próximamente

Esto está previsto y todavía no está en la aplicación.

- **Categorías de modelos en la página Modelos:** agrupados por tarea (chat y redacción, programación, razonamiento, visión, embeddings, voz), con filtros y búsqueda.
- **Generación de imágenes y vídeo** en este PC, incluido un «director» que encadena escenas para formar un vídeo. Los modelos de vídeo aparecerán como «Próximamente» hasta que exista el motor.
- **Elegir qué modelo responde**, y modelos que se piden ayuda entre sí, en AI Portal.
- **Versión para macOS.**

---

## 19. Soporte

- **Comentarios e ideas:** Acerca de > **Enviar comentarios o sugerir una idea**.
- **Preguntas de soporte y privacidad:** mindthaapp@gmail.com.
- **Versión:** Acerca de la muestra, y **Copiar versión** la pone en el portapapeles para tu mensaje.
- **Licencias:** Portal AI Host incluye llama.cpp (licencia MIT), que ejecuta los modelos en tu PC. Los modelos se descargan bajo sus propias licencias (Apache 2.0 o MIT). Consulta Acerca de.
