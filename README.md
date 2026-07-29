# PhotoMusicViewer / ФотоМузыка

A lightweight, privacy-friendly photo viewer and music player for Windows — in one window, with one click to switch modes.

**Read this in:** [English](#english) · [Русский](#русский) · [Español](#español)

---

<a name="english"></a>

## English

**PhotoMusicViewer** is a single-window WPF application for Windows that combines a fast image viewer with a simple audio player. No installer, no settings files, no registry writes, no telemetry — it just runs.

### Features

#### Photo mode
- **Formats:** JPG, JPEG, JFIF, PNG, BMP, WEBP, TIFF, TIF, GIF, HEIC, and RAW (CR2, NEF, ARW).
- **Animated GIF** playback with correct frame composition (transparency, frame offsets and disposal methods are honoured), plus a play/pause button.
- **Navigation:** arrows on screen, keyboard, mouse wheel, drag-and-drop of a file or folder onto the window.
- **Zoom** with the mouse wheel (0.2×–10×, zooms toward the cursor); **pan** by dragging with the left button while zoomed in.
- **Middle click resets the view** — zoom and position return to the original fit.
- **Right click copies the image to the clipboard.** The image is flagged so Windows keeps it out of clipboard history (`Win`+`V`) and out of cloud clipboard sync.
- **Drag the image out** with the left button to drop the file into another application.
- **Grid view** — thumbnail browser with folder navigation.
- **Fullscreen** mode.
- **Editing:** rotate, crop (with an interactive selection overlay), convert WEBP → JPG.
- **Scan text (OCR)** — recognises text on the image using the built-in Windows OCR engine, running every installed language pack and showing each result separately.
- **Strip EXIF** — removes metadata (including GPS coordinates) from the picture.
- **Rename** — inline rename of the current file, plus **Rename all** for batch renaming a folder.
- **Delete** the current file.
- **Sorting** by name, date modified, size or type, ascending or descending.
- **EXIF orientation** is applied automatically, so photos from phones are not shown sideways.

#### Music mode
- **Formats:** MP3, FLAC, WAV, OGG, OPUS, AAC, M4A.
- **Playlist** with drag-and-drop reordering and inline track renaming.
- **Playback:** play/pause, previous/next, seek bar, shuffle, repeat (off / one / all).
- **Speed control** from 0.1× to 3.0× with pitch preserved by the resampler; click the value to reset to 1.0×.
- **Volume** slider with a percentage readout.
- **Add Folder** — loads all supported audio files from a folder.

#### Common
- **Three languages:** English, Russian, Spanish — switched instantly with the `EN / RU / ES` button, no restart needed.
- **Dark theme** only, easy on the eyes.
- **Privacy:** the chosen language lives in memory only; the app writes no configuration files and removes its own entry from the Windows *Recent items* list after opening a file or folder.

### Keyboard shortcuts

#### Photo mode
| Key | Action |
| --- | --- |
| `←` / `→` | Previous / next image |
| `Home` / `End` | First / last image |
| `Space` | Play/pause the animated GIF |
| `R` | Rotate |
| `Ctrl` + `C` | Copy the image to the clipboard |
| `Delete` | Delete the current file |
| `F11` | Fullscreen |
| `Esc` | Leave fullscreen / grid / crop mode |

#### Music mode
| Key | Action |
| --- | --- |
| `Space` | Play / pause |
| `←` / `→` | Seek 5 seconds back / forward |
| `Ctrl` + `←` / `→` | Previous / next track |

### Mouse

#### Photo mode
| Action | Result |
| --- | --- |
| **Right click** | Copy the image to the clipboard (kept out of clipboard history and cloud sync) |
| **Middle click** | Reset zoom and position to the original view |
| **Wheel** | Zoom in / out (0.2×–10×, toward the cursor) |
| **Wheel** in crop mode | Resize the crop selection |
| **Left drag** while zoomed in | Pan the image |
| **Left drag** at normal zoom | Drag the file out into another application |
| **Left click** on the file name | Rename the current file inline |
| **Left click** on a thumbnail | Open the image or enter the folder |
| **Drop** a file or folder on the window | Open it |

#### Music mode
| Action | Result |
| --- | --- |
| **Double click** a track | Play it |
| **Left drag** a track | Reorder the playlist |
| **Left click** a track title | Rename it inline |
| **Wheel** over the volume slider | Volume ±5% |
| **Wheel** over the speed slider | Speed ±0.1× |
| **Left click** the speed value | Reset the speed to 1.0× |
| **Drop** files or a folder on the playlist | Add the tracks |

### Requirements
- Windows 10 version 2004 (build 19041) or newer
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (not needed for a self-contained build)
- For **Scan text (OCR)**: at least one Windows OCR language pack — *Settings → Time & Language → Language & region*
- HEIC and RAW files rely on the matching Windows codecs (e.g. the *HEIF Image Extensions* / *Raw Image Extension* from the Microsoft Store)

### Build

```bash
git clone https://github.com/re-quies/PhotoMusicViewer
cd PhotoMusicViewer
dotnet build -c Release
```

Run it:

```bash
dotnet run -c Release
```

Single-file build that needs no installed runtime:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Tech stack
- C# / .NET 8 / WPF
- [NAudio](https://github.com/naudio/NAudio) and NAudio.Vorbis — audio playback
- [Concentus](https://github.com/lostromb/concentus) and Concentus.Oggfile — OPUS decoding
- `Windows.Media.Ocr` — text recognition
---

<a name="русский"></a>

## Русский

**ФотоМузыка (PhotoMusicViewer)** — приложение для Windows в одном окне: быстрый просмотрщик изображений и простой музыкальный плеер. Без установщика, без файлов настроек, без записи в реестр и без телеметрии — просто запускается и работает.

### Возможности

#### Режим «Фото»
- **Форматы:** JPG, JPEG, JFIF, PNG, BMP, WEBP, TIFF, TIF, GIF, HEIC и RAW (CR2, NEF, ARW).
- **Анимированные GIF** с корректной сборкой кадров (учитываются прозрачность, смещения кадров и способ очистки), есть кнопка пуск/пауза.
- **Навигация:** стрелки на экране, клавиатура, колесо мыши, перетаскивание файла или папки в окно.
- **Масштаб** колесом мыши (0.2×–10×, приближение к курсору); **перемещение** перетаскиванием ЛКМ в увеличенном виде.
- **Средняя кнопка (клик колесом) сбрасывает вид** — масштаб и смещение возвращаются в исходное положение.
- **ПКМ копирует изображение в буфер обмена.** Картинка помечается так, что Windows не сохраняет её в историю буфера (`Win`+`V`) и не отправляет в облачную синхронизацию.
- **Перетаскивание картинки** ЛКМ отдаёт файл в другое приложение (обычный drag-and-drop).
- **Сетка** — просмотр миниатюр с переходом по папкам.
- **Полноэкранный** режим.
- **Редактирование:** поворот, обрезка (с интерактивной рамкой выделения), конвертация WEBP → JPG.
- **Распознать текст (OCR)** — читает текст с изображения встроенным движком Windows, прогоняя картинку через все установленные языки и показывая результат каждого отдельно.
- **Удалить EXIF** — вырезает метаданные, включая GPS-координаты.
- **Переименование** текущего файла прямо в панели и **«Переименовать все»** для пакетного переименования папки.
- **Удаление** текущего файла.
- **Сортировка** по имени, дате изменения, размеру или типу, по возрастанию и убыванию.
- **Ориентация EXIF** применяется автоматически — фото с телефона не будут лежать на боку.

#### Режим «Музыка»
- **Форматы:** MP3, FLAC, WAV, OGG, OPUS, AAC, M4A.
- **Плейлист** с перетаскиванием треков и переименованием названия по клику.
- **Воспроизведение:** пуск/пауза, предыдущий/следующий, полоса перемотки, перемешивание, повтор (выкл / один / все).
- **Скорость** от 0.1× до 3.0× (ресемплер сохраняет тон); клик по значению сбрасывает на 1.0×.
- **Громкость** с показом процентов.
- **«Добавить папку»** — загружает все поддерживаемые аудиофайлы из папки.

#### Общее
- **Три языка:** английский, русский, испанский — переключаются мгновенно кнопкой `EN / RU / ES`, перезапуск не нужен.
- **Только тёмная тема**, комфортная для глаз.
- **Приватность:** выбранный язык хранится только в памяти процесса, приложение не создаёт файлов настроек и само убирает свою запись из системного списка Windows «Недавние файлы» после открытия файла или папки.

### Горячие клавиши

#### Режим «Фото»
| Клавиши | Действие |
| --- | --- |
| `←` / `→` | Предыдущее / следующее изображение |
| `Home` / `End` | Первое / последнее изображение |
| `Space` | Пуск/пауза анимированного GIF |
| `R` | Поворот |
| `Ctrl` + `C` | Копировать изображение в буфер обмена |
| `Delete` | Удалить текущий файл |
| `F11` | Полный экран |
| `Esc` | Выйти из полного экрана / сетки / режима обрезки |

#### Режим «Музыка»
| Клавиши | Действие |
| --- | --- |
| `Space` | Воспроизведение / пауза |
| `←` / `→` | Перемотка на 5 секунд назад / вперёд |
| `Ctrl` + `←` / `→` | Предыдущий / следующий трек |

### Мышь

#### Режим «Фото»
| Действие | Результат |
| --- | --- |
| **ПКМ** | Копировать изображение в буфер обмена (без истории буфера и облачной синхронизации) |
| **Средняя кнопка (клик колесом)** | Сбросить масштаб и смещение в исходное положение |
| **Колесо** | Приближение / отдаление (0.2×–10×, к курсору) |
| **Колесо** в режиме обрезки | Изменить размер рамки выделения |
| **Перетаскивание ЛКМ** в увеличенном виде | Перемещение по изображению |
| **Перетаскивание ЛКМ** в обычном масштабе | Отдать файл в другое приложение |
| **Клик ЛКМ** по имени файла | Переименовать текущий файл на месте |
| **Клик ЛКМ** по миниатюре | Открыть изображение или войти в папку |
| **Перетаскивание** файла или папки в окно | Открыть |

#### Режим «Музыка»
| Действие | Результат |
| --- | --- |
| **Двойной клик** по треку | Запустить его |
| **Перетаскивание ЛКМ** трека | Изменить порядок в плейлисте |
| **Клик ЛКМ** по названию трека | Переименовать на месте |
| **Колесо** над ползунком громкости | Громкость ±5% |
| **Колесо** над ползунком скорости | Скорость ±0.1× |
| **Клик ЛКМ** по значению скорости | Сбросить скорость на 1.0× |
| **Перетаскивание** файлов или папки в плейлист | Добавить треки |

### Требования
- Windows 10 версии 2004 (сборка 19041) или новее
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (не нужен для самодостаточной сборки)
- Для **распознавания текста**: хотя бы один языковой пакет OCR Windows — *Параметры → Время и язык → Язык и регион*
- Для HEIC и RAW нужны соответствующие кодеки Windows (например, *HEIF Image Extensions* и *Raw Image Extension* из Microsoft Store)

### Сборка

```bash
git clone https://github.com/re-quies/PhotoMusicViewer
cd PhotoMusicViewer
dotnet build -c Release
```

Запуск:

```bash
dotnet run -c Release
```

Сборка одним файлом, без установленного runtime:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Технологии
- C# / .NET 8 / WPF
- [NAudio](https://github.com/naudio/NAudio) и NAudio.Vorbis — воспроизведение звука
- [Concentus](https://github.com/lostromb/concentus) и Concentus.Oggfile — декодирование OPUS
- `Windows.Media.Ocr` — распознавание текста
---

<a name="español"></a>

## Español

**PhotoMusicViewer** es una aplicación WPF para Windows que reúne en una sola ventana un visor de imágenes rápido y un reproductor de música sencillo. Sin instalador, sin archivos de configuración, sin escrituras en el registro y sin telemetría: simplemente se ejecuta.

### Características

#### Modo Foto
- **Formatos:** JPG, JPEG, JFIF, PNG, BMP, WEBP, TIFF, TIF, GIF, HEIC y RAW (CR2, NEF, ARW).
- **GIF animados** con composición correcta de fotogramas (se respetan la transparencia, los desplazamientos y el método de eliminación), con botón de reproducir/pausar.
- **Navegación:** flechas en pantalla, teclado, rueda del ratón y arrastrar un archivo o una carpeta a la ventana.
- **Zoom** con la rueda del ratón (0.2×–10×, hacia el cursor); **desplazamiento** arrastrando con el botón izquierdo cuando la imagen está ampliada.
- **El botón central (clic en la rueda) restablece la vista**: el zoom y la posición vuelven al estado original.
- **El botón derecho copia la imagen al portapapeles.** La imagen se marca para que Windows no la guarde en el historial del portapapeles (`Win`+`V`) ni la sincronice en la nube.
- **Arrastra la imagen** con el botón izquierdo para soltar el archivo en otra aplicación.
- **Vista de cuadrícula** — explorador de miniaturas con navegación por carpetas.
- Modo de **pantalla completa**.
- **Edición:** rotar, recortar (con una selección interactiva), convertir WEBP → JPG.
- **Escanear texto (OCR)** — reconoce el texto de la imagen con el motor OCR integrado de Windows, usando todos los paquetes de idioma instalados y mostrando cada resultado por separado.
- **Quitar EXIF** — elimina los metadatos, incluidas las coordenadas GPS.
- **Renombrar** el archivo actual desde la propia barra y **Renombrar todo** para renombrar una carpeta por lotes.
- **Eliminar** el archivo actual.
- **Ordenación** por nombre, fecha de modificación, tamaño o tipo, ascendente o descendente.
- La **orientación EXIF** se aplica automáticamente, así que las fotos del móvil no aparecen giradas.

#### Modo Música
- **Formatos:** MP3, FLAC, WAV, OGG, OPUS, AAC, M4A.
- **Lista de reproducción** con reordenación por arrastre y cambio de nombre en línea.
- **Reproducción:** reproducir/pausar, anterior/siguiente, barra de posición, aleatorio y repetición (no / uno / todo).
- **Velocidad** de 0.1× a 3.0× conservando el tono; haz clic en el valor para volver a 1.0×.
- **Volumen** con indicador de porcentaje.
- **Añadir carpeta** — carga todos los archivos de audio compatibles de una carpeta.

#### General
- **Tres idiomas:** inglés, ruso y español, con cambio instantáneo mediante el botón `EN / RU / ES`, sin reiniciar.
- **Solo tema oscuro**, cómodo para la vista.
- **Privacidad:** el idioma elegido solo vive en memoria, la aplicación no crea archivos de configuración y elimina su propia entrada de la lista de *Elementos recientes* de Windows tras abrir un archivo o una carpeta.

### Atajos de teclado

#### Modo Foto
| Teclas | Acción |
| --- | --- |
| `←` / `→` | Imagen anterior / siguiente |
| `Home` / `End` | Primera / última imagen |
| `Space` | Reproducir o pausar el GIF animado |
| `R` | Rotar |
| `Ctrl` + `C` | Copiar la imagen al portapapeles |
| `Delete` | Eliminar el archivo actual |
| `F11` | Pantalla completa |
| `Esc` | Salir de pantalla completa / cuadrícula / recorte |

#### Modo Música
| Teclas | Acción |
| --- | --- |
| `Space` | Reproducir / pausar |
| `←` / `→` | Retroceder / avanzar 5 segundos |
| `Ctrl` + `←` / `→` | Pista anterior / siguiente |

### Ratón

#### Modo Foto
| Acción | Resultado |
| --- | --- |
| **Clic derecho** | Copiar la imagen al portapapeles (fuera del historial y de la sincronización en la nube) |
| **Clic central (rueda)** | Restablecer el zoom y la posición a la vista original |
| **Rueda** | Acercar / alejar (0.2×–10×, hacia el cursor) |
| **Rueda** en modo recorte | Cambiar el tamaño de la selección |
| **Arrastrar con el izquierdo** con zoom | Desplazar la imagen |
| **Arrastrar con el izquierdo** sin zoom | Soltar el archivo en otra aplicación |
| **Clic izquierdo** en el nombre del archivo | Renombrar el archivo actual |
| **Clic izquierdo** en una miniatura | Abrir la imagen o entrar en la carpeta |
| **Soltar** un archivo o carpeta en la ventana | Abrirlo |

#### Modo Música
| Acción | Resultado |
| --- | --- |
| **Doble clic** en una pista | Reproducirla |
| **Arrastrar** una pista | Reordenar la lista |
| **Clic izquierdo** en el título | Renombrarla |
| **Rueda** sobre el control de volumen | Volumen ±5% |
| **Rueda** sobre el control de velocidad | Velocidad ±0.1× |
| **Clic izquierdo** en el valor de velocidad | Restablecer a 1.0× |
| **Soltar** archivos o una carpeta en la lista | Añadir las pistas |

### Requisitos
- Windows 10 versión 2004 (compilación 19041) o posterior
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (no es necesario en una compilación autocontenida)
- Para **escanear texto**: al menos un paquete de idioma OCR de Windows — *Configuración → Hora e idioma → Idioma y región*
- HEIC y RAW requieren los códecs correspondientes de Windows (por ejemplo *HEIF Image Extensions* y *Raw Image Extension* de Microsoft Store)

### Compilación

```bash
git clone https://github.com/re-quies/PhotoMusicViewer
cd PhotoMusicViewer
dotnet build -c Release
```

Ejecutar:

```bash
dotnet run -c Release
```

Compilación en un solo archivo, sin runtime instalado:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Tecnologías
- C# / .NET 8 / WPF
- [NAudio](https://github.com/naudio/NAudio) y NAudio.Vorbis — reproducción de audio
- [Concentus](https://github.com/lostromb/concentus) y Concentus.Oggfile — decodificación de OPUS
- `Windows.Media.Ocr` — reconocimiento de texto
