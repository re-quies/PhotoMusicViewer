# PhotoMusicViewer / ФотоМузыка

A photo viewer and music player for Windows, with local OCR and optional online translation — in one window.

**Version: 7.13.8** · C# / .NET 8 / WPF

**Read this in:** [English](#english) · [Русский](#русский) · [Español](#español)

---

<a name="english"></a>

<p align="center">
  <img src="https://github.com/user-attachments/assets/2e39f8d9-3fed-421f-95c9-9381f08fed69" alt="PhotoMusicViewer1">
</p>
<p align="center">
  <img src="https://github.com/user-attachments/assets/33bba2db-41e0-4dc3-928c-96930fcb1ac4" alt="PhotoMusicViewer2">
</p>

## English

**PhotoMusicViewer** combines an image viewer, basic photo editing, text recognition and a music player. No installer is required. Viewing photos, playing local music and Windows OCR work offline; translation and cloud image recognition are optional and require your own API keys.

### Features

#### Photo mode
- **Compact speed/volume:** each control is a single row: icon → slider → number. Upper panel height is 28 (previously 42); count text is unchanged. Speed/volume icons have localized tooltips. At very narrow widths the upper panel expands left to keep sliders usable, without scaling text.
- **Music panel layout:** the seek bar, current title and playback time occupy the center of the bottom row, between mode selectors and actions. Track count, speed and volume occupy a 28-unit block immediately above the actions, starting at Add Folder and reaching the right edge; the upper block uses the playlist background and only reserves its bottom-right corner. The playlist reaches the footer on the left; there is no full-width empty strip.
- **Music toolbar:** original vector icons with localized tooltips replace button captions. Play/pause, repeat off/all/one and sort direction have distinct states. Remaining music text (track names, time, speed, volume, count and sorting) and tooltips use size 13. Remove/Clear affect the playlist only, not files. The compact footer is still 42 units high, with mode selectors aligned to the photo footer.
- **Mirror view:** click reflects left/right; double-click reflects top/bottom. The same 150 ms delay as Open is used. Preview does not change the file. Save changes writes the current rotation/reflection together: copy by default, or explicit replacement with a recovery backup. As with rotation, saving is supported for JPEG, PNG, BMP and single-page TIFF; animated GIF reflection is unavailable. Toolbar filename, metadata/count, zoom and sorting text use size 13.
- **Icon toolbar:** Open → Grid → Crop → Rotate → Mirror → OCR → Fullscreen → Strip EXIF → JPG (WebP only) → Rename all → Settings → Delete. Original vector drawings replace text; hover shows the localized name and existing explanation. Mode selectors occupy the same position in both modes. Only the filename stays on the left; photo size, count and zoom join the right-aligned controls with subdued text. Both mode panels are 42 units high; photo actions are smaller and right-aligned in one row. Photo/Music selectors use the same icons in both modes. Change the interface language in Settings; the separate language buttons are removed from both modes.
- **Open:** single-click opens the image picker; double-click opens the folder picker. The user-configured single-click delay is **150 ms**; the second click must arrive before it expires. Choosing a folder shows a photo thumbnail grid, with subfolders and a parent-folder button. Click a thumbnail to enter the viewer. Empty folders stay in grid mode. Drop a folder in photo mode to open the grid; in music mode it is added to the playlist.
- **Supported file extensions:** JPG, JPEG, JFIF, PNG, BMP, WEBP, TIFF, TIF, GIF, HEIC, HEIF, HIF and RAW (CR2, NEF, ARW). Actual decoding depends on the Windows codecs installed; support for viewing does not imply support for saving in the same format.
- **Animated GIFs:** composed frames with transparency, offsets and disposal handling; play/pause control. Large images and animations may be refused by resource limits.
- **Navigation:** previous/next and first/last image, drag-and-drop, thumbnail grid with folder navigation, fullscreen.
- **Zoom:** percentages are relative to original image pixels, including monitor scaling: 100% means one original pixel per physical screen pixel. Images still open fitted to the window (small images are enlarged); middle click restores that fitted view, not necessarily 100%. Wheel steps and panning are unchanged; minimum remains 0.2× of the fitted view, maximum is 10,000% of original pixels. The fitted view is retained even if an exceptionally tiny image already exceeds that maximum. Percentages update on resizing/fullscreen and are independent of decoded preview resolution and file DPI metadata.
- **100% shortcut:** click the zoom percentage to show original pixels 1:1 and center the image. Middle click still returns to the fitted view. A tooltip explains both actions.
- **Clipboard:** right click or `Ctrl+C` copies the image. Dragging at normal zoom exports the file to another application.
- **Sorting controls:** dark background matches the viewing area and text matches the photo filename in both modes, including the expanded list. Grid sort key/direction changes immediately reorder the current folder without re-opening it; parent/subfolder entries stay before photos.
- **Sorting:** natural filename order, modification date, size or type; ascending/descending. Photo defaults: modification date, newest first.
- **EXIF orientation** is applied when displaying images.
- **Rotation:** 90° viewing steps do not change the file until you choose **Save rotation**.
- **Crop:** interactive selection, saving a copy by default. JPEG offers lossless MCU-grid cropping or exact cropping with re-encoding at quality 95. The lossless frame may expand to the JPEG block grid; the save dialog shows the resulting area.
- **Save rotation / Strip EXIF:** copies by default, with an explicit option to replace the original. JPEG uses `jpegtran` for lossless operations; incompatible lossless rotation is refused rather than silently cropped or re-encoded. Lossy JPEG re-encoding is an explicit alternative.
- **Strip EXIF** removes more than EXIF: GPS, camera metadata and color profiles too. Removing a color profile can change the appearance. Supported here: JPEG/JFIF, PNG, BMP and TIFF.
- **WEBP → JPG:** creates a new JPEG; removal of the source is a separate confirmed action. The source is checked against the version used for conversion before removal.
- **Rename:** inline rename of the current photo; batch rename for selected photo/RAW, video and music categories, with folder selection and optional subfolders. See the batch rename section below.
- **Delete:** uses the Windows Recycle Bin. If permanent deletion is required, an additional warning is shown.
- **Restore original / Delete backup:** manage the retained backup after replacing an image.

#### OCR and translation
- **Local OCR:** built-in `Windows.Media.Ocr`, using the available recognition languages. No image upload is needed.
- **Text translation:** DeepL, Google Gemini, Google Cloud Translation v2, or Qwen (Alibaba Model Studio / DashScope).
- **Image → text / text + translation:** Google Gemini and Qwen vision models can process the image itself. Cloud Translation v2 is text-only.
- **Settings:** source/target language, models, prompt presets and provider-specific options. Qwen supports region selection and a custom API endpoint; the key must match the endpoint/region.
- **Network confirmation is on by default.** Permissions distinguish text, images and service probes and are scoped to the destination origin for the session. Disabling confirmation permits supported HTTPS requests without asking each time.
- Images are prepared with metadata stripping enabled by default; this does **not** remove sensitive information visible in the pixels. API usage may be billed by the provider. Check its data retention and privacy policy before sending private content.

#### Music mode
- **File extensions:** MP3, FLAC, WAV, OGG, OPUS, AAC, M4A. Some formats depend on Windows media support.
- **Container-aware decoding:** Ogg Opus/Vorbis is recognized by content even under a wrong extension. Ordinary MP4/M4A Opus mono/stereo uses the built-in Concentus decoder, without a Windows Opus codec or external FFmpeg. AAC/ALAC and other system-backed formats still depend on Windows. Fragmented MP4, encrypted/multichannel Opus, multiple audio tracks and complex edit lists are not supported. The playlist extension filter is unchanged; arbitrary `.mp4` files are not automatically added.
- Playlist, drag-and-drop reordering, inline file rename, adding files or a folder.
- Play/pause, previous/next, seek, shuffle, repeat off/one/all.
- **Speed:** 0.1×–3.0×. Changing speed also changes pitch; pitch-preserving time stretching is not implemented. Click the speed value to reset to 1.0×.
- Volume control; sorting by name, date, size or type.

#### Batch rename: choosing media types
- **12 output name schemes:** numeric widths 1–6 (`1`, `01`, `001`, `0001`, `00001`, `000001`); letters starting at `a`, `aa` or `aaa`; mixed `0a`, `00a` or `000a`.
- Letters use lowercase Latin: `a … z, aa … zz, aaa …`; longer variants start at `aa`/`aaa` and grow when exhausted. Mixed names cycle the final letter before increasing the number: `00a … 00z, 01a … 99z, 100a …`.
- The start value is the actual number for numeric schemes (0 allowed), or a sequence position for letters/mixed (1 = first name). The source-name filter remains independent: to rename existing numeric files into letters, select **All selected files**.
- Occupied names, case variants and Windows device names (e.g. `con`, `aux`) are skipped. Existing source names stay reserved; no overwrite and no temporary cyclic rename. Gaps are possible.
- Open **Photo → Rename all**. An opened image is no longer required: choose any folder with **Browse…** in the dialog.
- Independent checkboxes: **Photos / RAW**, **Videos**, **Music / audio**. Select one, several or all; at least one is required. Defaults: photos on, video/music off.
- **Include sidecars and backups** is a separate checkbox, on by default. Known metadata and backups follow their selected owner. SRT/ASS/SSA/VTT/SUB/IDX subtitles follow selected video only. Unchecked media never moves just because it shares a photo name; check Videos/Music too if you want a Live Photo movie/voice memo renamed.
- Ambiguous unqualified metadata such as `same.xmp` is left unchanged if `same.jpg`/`same.mp3`/`same.mov` belongs to an unchecked category. Explicit owner names such as `same.mp3.xmp` follow only that checked owner.
- Numeric-name mode remains separate: **Only non-numeric names** (default) or **All selected files, including numeric names**. Occupied numbers are always skipped, including those of participating sources; numbering can have gaps rather than compactly reusing 001…N.
- Selected primary media sharing a basename get the same generated name; extensions and folders are preserved. With sidecars off, selected RAW/JPG files can still move together because both belong to the checked photo category.
- The dialog shows selected categories, member counts and an example before confirmation. Numbers follow the photo toolbar sort order, including when selecting a media-only folder.
- If the current app music track is in the confirmed plan, it is unloaded before moving. The internal playlist receives only completed mappings, including partial/cancelled results. Playback does not restart automatically; press Play to reload the track. External M3U/PLS playlists, embedded tags and references in other applications are not rewritten.
- Category selection is **by extension**, not decoded content. These are rename-only lists: they do not add image codecs, audio codecs or a video player. `.ts` is intentionally excluded because it may be TypeScript source code; `.mts`/`.m2ts` are included.

| Category | Rename extensions |
|---|---|
| Photos / RAW | `.jpg`, `.jpeg`, `.jfif`, `.png`, `.bmp`, `.webp`, `.tiff`, `.tif`, `.gif`, `.heic`, `.heif`, `.hif`, `.avif`, `.apng`, `.jxl`, `.jxr`, `.wdp`, `.hdp`, `.tga`, `.psd`, `.psb`, `.svg`, `.ico`, `.cr2`, `.cr3`, `.crw`, `.nef`, `.nrw`, `.arw`, `.srf`, `.sr2`, `.raf`, `.orf`, `.rw2`, `.rwl`, `.pef`, `.dng`, `.srw`, `.x3f`, `.3fr`, `.fff`, `.iiq`, `.mos`, `.mrw`, `.erf`, `.kdc`, `.dcr`, `.raw` |
| Videos | `.mp4`, `.m4v`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.webm`, `.mpeg`, `.mpg`, `.mpe`, `.m2v`, `.mts`, `.m2ts`, `.vob`, `.ogv`, `.3gp`, `.3g2`, `.flv`, `.f4v`, `.asf`, `.divx` |
| Music / audio | `.mp3`, `.flac`, `.wav`, `.ogg`, `.opus`, `.aac`, `.m4a`, `.m4b`, `.m4r`, `.wma`, `.aif`, `.aiff`, `.aifc`, `.alac`, `.ape`, `.wv`, `.mka`, `.mp2`, `.mpa`, `.oga`, `.spx`, `.ac3`, `.eac3`, `.dts`, `.amr`, `.au`, `.snd` |

#### Interface and local data
- English, Russian and Spanish; instant switching, dark interface.
- Startup language, sorting, volume and photo deletion confirmation are configurable. Remembering language, sorting and volume can be controlled separately.
- **The application is not “disk-write-free”.** Local data lives under `%LOCALAPPDATA%\PhotoMusicViewer`: `preferences.json`, optional `translation-settings.json`, recovery records in `pending`, option markers, and temporary unencrypted streaming Opus PCM cache in `OpusCache` (up to 64 MiB per reader, deleted on close).
- Saving translation settings is **off by default**. If enabled, API keys are encrypted with Windows DPAPI for the current Windows account; other settings are stored as JSON. DPAPI is not protection against software already running as that user.
- Diagnostic logging is **off by default**. Optional logs live in `logs`; the logger limits/rotates files and sanitizes sensitive fields. `PMV_LOG=on`, `debug` or `off` overrides normal logging selection.
- The optional disk thumbnail cache is **off by default**. It lives in `thumbs`, targets a 256 MiB limit and can be cleared in settings. Thumbnail images are not encrypted; hashed filenames are not image encryption.
- **Windows history cleanup is opt-in and off by default.** Recent-item cleanup skips shortcuts whose target cannot be resolved. Explorer registry cleanup requires an unambiguous full-path match and uses a registry transaction for values and MRU order; if transactional access fails, it does not fall back to unsafe writes. This is selective cleanup, not a guarantee of anonymity or removal of every system trace.
- Text/image clipboard data is marked to exclude it from Windows clipboard history and cloud sync. On exit the app tries to clear only its own clipboard content, without clearing a newer copy from another application. These flags do not prevent other software from reading the clipboard.

### Saving and integrity precautions
- Replacing an image retains an **unencrypted** `*.pmv-original-….bak` next to it, including its original private metadata. Backups are not automatically deleted. Restore works after restart only if the saved file still passes the expected checks; restoration or explicit backup removal normally sends the backup to the Recycle Bin.
- Temporary replacement artifacts have recovery records. A failed rollback keeps the surviving backup and pending record for a later recovery attempt; blocked access or unsafe paths may still require manual recovery. Do not blindly delete `.bak` files or the `pending` directory after a failure.
- Batch rename and recovery cleanup check parent components, reparse points and actual Windows paths. Unsupported or ambiguous paths are refused; this is not a sandbox for all file operations.
- Keep independent backups. Batch rename is **not a crash-atomic transaction across the entire group**; an interrupted rename can split related files. Publishing a new copy is not a universal guarantee against power loss. Inline single-file rename does not provide the companion/backup grouping of batch rename.
- Resource limits, file-version checks and recovery are defensive measures, not a guarantee against every failure, hostile same-user process or filesystem behavior. Multi-page TIFF editing and some formats/metadata operations are restricted; non-JPEG re-encoding does not guarantee metadata preservation.

### Keyboard shortcuts

| Mode | Keys | Action |
| --- | --- | --- |
| Photo | `←` / `→` | Previous / next image |
| Photo | `Home` / `End` | First / last image |
| Photo | `Space` | Play/pause GIF |
| Photo | `R` | Rotate the view, not the file |
| Photo | `Ctrl+C` | Copy image |
| Photo | `Delete` | Delete current file |
| Photo | `F11` | Toggle fullscreen |
| Photo | `Esc` | Leave fullscreen/grid/crop, or request cancellation of a running file operation |
| Music | `Space` | Play/pause |
| Music | `←` / `→` | Seek 5 seconds |
| Music | `Ctrl+←` / `Ctrl+→` | Previous / next track |

Shortcuts depend on focus; text fields and active dialogs may consume them. File-operation cancellation is cooperative, not an undo of completed changes.

### Mouse
- **Photo:** right click copies; middle click resets; wheel zooms or resizes the crop frame; left drag pans when zoomed or drags the file out at normal zoom. Click the filename to rename; click a thumbnail to open an image/folder; drop a file/folder to open it.
- **Music:** double click plays a track; drag reorders; click the title to rename. Wheel over volume changes it by 5%, over speed by 0.1×; click the speed value to reset. Drop files/folders to add tracks.

### Requirements
- Windows 10 version 2004 (build 19041) or newer / Windows 11.
- Recommended build target: **Windows x64**; the bundled `jpegtran` is x64 only.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) for framework-dependent builds; not needed for a self-contained publication.
- Windows OCR language packs for local text recognition.
- Matching Windows codecs for HEIC/HEIF/HIF, WEBP and RAW as needed. Recognized extension alone does not guarantee a working decoder.
- Internet access, your own API keys and provider permissions/balance only for online features. Build requires .NET 8 SDK and package restore access.

### Build and run

Run these commands from the repository root containing `app/`:

```powershell
git clone https://github.com/re-quies/PhotoMusicViewer
cd PhotoMusicViewer
dotnet build app/PhotoMusicViewer.csproj -c Release
dotnet run --project app/PhotoMusicViewer.csproj -c Release
```

Self-contained Windows x64 publication:

```powershell
dotnet publish app/PhotoMusicViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `app/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.

**Distribute the whole publish folder**, not just `PhotoMusicViewer.exe`: `tools/jpegtran/jpegtran.exe` and `libjpeg-62.dll` must remain together beside the app under `tools/jpegtran`, with their license files. “Single-file” does not embed this native helper. The `.csproj` copies it automatically. If the helper is missing or unsupported, affected lossless JPEG operations are refused; there is no silent lossy fallback.

To build an installer, open the root `installer.iss` in Inno Setup 6 after publishing. It packages the whole publish tree, reads the version from the EXE and writes the installer to `installer-output/`.

The GitHub/build archive contains application sources and setup documentation, not the regression suite. Compilation is not a substitute for Windows runtime testing of WPF, Shell, registry transactions, junctions and native JPEG operations.

### Technology and bundled components
- C# / .NET 8 / WPF; Windows MediaPlayer and [NAudio](https://github.com/naudio/NAudio) 2.3.0 / NAudio.Vorbis 1.5.0.
- [Concentus](https://github.com/lostromb/concentus) 2.2.2 / Concentus.Oggfile 1.0.7 for OPUS.
- `Windows.Media.Ocr`; Windows DPAPI (`System.Security.Cryptography.ProtectedData` 8.0.0).
- [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo) 3.2.0, bundled Windows x64 `jpegtran`. See `app/tools/jpegtran/LICENSE.md`, `README.ijg` and `PROVENANCE.txt` for notices and binary provenance. Third-party licenses remain applicable; they do not license the application's own code.

---

<a name="русский"></a>

## Русский

**ФотоМузыка (PhotoMusicViewer)** объединяет просмотр изображений, базовое редактирование, распознавание текста и музыкальный плеер. Установщик не требуется. Просмотр фото, локальная музыка и OCR Windows работают без интернета; перевод и облачное распознавание включаются по желанию и требуют ваших API-ключей.

### Возможности

#### Режим «Фото»
- **Компактные регуляторы:** значок → ползунок → число в одной строке для скорости и громкости. Высота верхнего блока 28 вместо 42; текст счётчика без изменений. Для значков есть локализованные подсказки. В очень узком окне блок расширяется влево, чтобы сохранить доступные ползунки, без уменьшения текста.
- **Компоновка музыкального режима:** полоса проигрывания, название и время — в центре нижнего ряда, между режимами и кнопками. Счётчик, скорость и громкость — в блоке высотой 28 прямо над кнопками, от «Добавить папку» до правого края. Фон верхнего блока совпадает со списком треков; для него зарезервирован только правый нижний угол списка. Слева список доходит до нижней панели, без пустой полосы на всю ширину.
- **Музыкальная панель:** вместо подписей кнопок — собственные векторные значки с локализованными подсказками. Воспроизведение/пауза, повтор выкл/все/один и направление сортировки имеют разные состояния. Оставшийся текст (названия, время, скорость, громкость, счётчик, сортировка) и подсказки — размер 13. «Убрать»/«Очистить» меняют только плейлист, файлы не удаляют. Нижняя панель остаётся высотой 42; переключатели режимов совпадают с фоторежимом.
- **Зеркальное отражение:** одинарный щелчок — слева направо, двойной — сверху вниз; задержка 150 мс, как у Open. До сохранения файл не меняется. Кнопка сохранения записывает текущий поворот и отражение вместе: по умолчанию в копию, при явной замене — с резервной копией оригинала. Как у поворота, запись доступна для JPEG, PNG, BMP и одностраничного TIFF; отражение анимированных GIF недоступно. Название, размер/счётчик, масштаб и сортировка используют шрифт 13.
- **Панель значков:** открыть → сетка → обрезать → повернуть → отражение → OCR → полный экран → EXIF → JPG (только WebP) → переименовать → настройки → удалить. Вместо текста — собственные векторные рисунки; при наведении показываются название и прежнее пояснение. Переключатели режимов расположены одинаково в обеих панелях. Слева — только имя файла; размер, счётчик фото и масштаб перенесены вправо, текст приглушённый. Обе панели высотой 42 единицы; уменьшенные фото-кнопки выровнены вправо в один ряд. Переключатели «Фото»/«Музыка» используют одинаковые значки в обоих режимах. Язык интерфейса меняется в настройках; отдельные языковые кнопки убраны из обоих режимов.
- **Открыть:** одинарный щелчок вызывает выбор фотографии, двойной — выбор папки. Пользовательская задержка одиночного щелчка — **150 мс**; второй щелчок должен прийти до её срабатывания. Выбранная папка показывается как сетку миниатюр фотографий, подпапки и кнопку перехода наверх. Нажмите на миниатюру для просмотра снимка. Пустая папка остаётся в режиме сетки. Перетаскивание папки в фоторежиме открывает сетку, в музыкальном — добавляет её в плейлист.
- **Расширения файлов:** JPG, JPEG, JFIF, PNG, BMP, WEBP, TIFF, TIF, GIF, HEIC, HEIF, HIF и RAW (CR2, NEF, ARW). Реальное декодирование зависит от кодеков Windows. Возможность просмотра не означает возможность сохранения в том же формате.
- **Анимированные GIF:** сборка кадров с учётом прозрачности, смещения и очистки; пуск/пауза. Крупные изображения и анимации могут быть отклонены ограничениями ресурсов.
- **Навигация:** предыдущий/следующий и первый/последний снимок, drag-and-drop, сетка миниатюр с переходами по папкам, полный экран.
- **Масштаб:** проценты относительно исходных пикселей с учётом масштабирования монитора: 100% — один пиксель фото на один физический пиксель экрана. Фото по-прежнему вписывается в окно, маленькое растягивается; средняя кнопка возвращает к этому виду, не обязательно к 100%. Шаги колеса и перемещение прежние; минимум — 0.2× от вписанного вида, максимум — 10 000% от исходных пикселей. Если исключительно маленькое фото уже при вписывании превышает максимум, вписанный вид сохраняется. Проценты обновляются при изменении окна/полном экране и не зависят от разрешения временной копии или DPI в файле.
- **Быстрый масштаб 100%:** нажатие на проценты показывает фото пиксель в пиксель и центрирует его. Нажатие колёсика по-прежнему возвращает к вписанному виду. Для процентов добавлена подсказка.
- **Буфер обмена:** ПКМ или `Ctrl+C` копирует изображение. Перетаскивание при обычном масштабе передаёт файл другому приложению.
- **Списки сортировки:** в обоих режимах фон как у области просмотра, текст как у названия фото, включая раскрытый список. В сетке выбор критерия или направления сразу перестраивает текущую папку без повторного открытия; переход наверх и подпапки остаются перед фотографиями.
- **Сортировка:** естественный порядок имён, дата изменения, размер или тип; оба направления. Фото по умолчанию: дата изменения, новые первыми.
- **Ориентация EXIF** учитывается при показе.
- **Поворот:** шаги по 90° меняют только вид, пока не нажато **«Сохранить поворот»**.
- **Обрезка:** интерактивная рамка, по умолчанию сохранение копии. Для JPEG доступны обрезка без потерь по сетке MCU и точно по рамке с перекодированием, качество 95. Рамка без потерь может расшириться до границ блоков JPEG; итоговая область показана в окне сохранения.
- **Сохранение поворота / удаление EXIF:** по умолчанию в копию, замена оригинала выбирается явно. JPEG обрабатывается без потерь через `jpegtran`; несовместимый поворот отклоняется, а не приводит к скрытой подрезке или перекодированию. JPEG с потерями — отдельный явный вариант.
- **«Удалить EXIF»** удаляет не только EXIF, но и GPS, данные камеры и цветовые профили. Удаление профиля может изменить цвета. Здесь поддерживаются JPEG/JFIF, PNG, BMP и TIFF.
- **WEBP → JPG:** создаётся новый JPEG; удаление источника — отдельное подтверждаемое действие. Перед удалением проверяется соответствие исходника версии, из которой выполнена конвертация.
- **Переименование:** текущий снимок — прямо в панели; массовое — для выбранных категорий фото/RAW, видео и музыки, с выбором папки и опцией подпапок. Подробнее — в разделе массового переименования ниже.
- **Удаление:** через корзину Windows. Если требуется безвозвратное удаление, показывается дополнительное предупреждение.
- **«Вернуть оригинал» / «Удалить резервную копию»:** управление копией, оставшейся после замены изображения.

#### OCR и перевод
- **Локальное распознавание:** `Windows.Media.Ocr`, доступные языки распознавания. Загружать изображение в интернет не нужно.
- **Перевод текста:** DeepL, Google Gemini, Google Cloud Translation v2 или Qwen (Alibaba Model Studio / DashScope).
- **Фото → текст / текст + перевод:** Google Gemini и модели зрения Qwen могут читать само изображение. Cloud Translation v2 работает только с текстом.
- **Настройки:** исходный/целевой язык, модели, шаблоны промтов, параметры сервисов. Для Qwen доступны регионы и собственный адрес API; ключ должен соответствовать адресу/региону.
- **Подтверждение сетевой отправки включено по умолчанию.** Разрешения разделены для текста, изображений и проверки сервиса и привязаны к получателю (origin) на время сеанса. При отключении подтверждения поддерживаемые HTTPS-запросы выполняются без повторных вопросов.
- Удаление метаданных при подготовке изображения к отправке включено по умолчанию, но **не скрывает личные данные, видимые на самом снимке**. API может быть платным. Перед отправкой приватного содержимого изучите политику хранения данных выбранного сервиса.

#### Режим «Музыка»
- **Расширения:** MP3, FLAC, WAV, OGG, OPUS, AAC, M4A. Часть форматов зависит от медиаподдержки Windows.
- **Декодер выбирается по содержимому:** Ogg Opus/Vorbis распознаётся даже с неверным расширением. Обычный MP4/M4A Opus mono/stereo воспроизводится встроенным Concentus, без системного Opus-кодека и внешнего FFmpeg. AAC/ALAC и другие системные форматы по-прежнему зависят от Windows. Fragmented MP4, шифрованный/многоканальный Opus, несколько аудиотреков и сложные edit lists не поддерживаются. Фильтр расширений плейлиста не менялся; произвольные `.mp4` автоматически не добавляются.
- Плейлист, изменение порядка перетаскиванием, переименование файла в панели, добавление файлов или папки.
- Пуск/пауза, предыдущий/следующий, перемотка, случайный порядок, повтор выкл./один/все.
- **Скорость:** 0.1×–3.0×. Вместе со скоростью меняется высота тона; сохранение тона не реализовано. Клик по значению сбрасывает скорость на 1.0×.
- Громкость; сортировка по имени, дате, размеру или типу.

#### Массовое переименование: выбор типов медиа
- **12 схем выходных имён:** числовые с шириной 1–6 (`1`, `01`, `001`, `0001`, `00001`, `000001`); буквенные с началом `a`, `aa` или `aaa`; смешанные `0a`, `00a` или `000a`.
- Буквы — строчная латиница: `a … z, aa … zz, aaa …`. Варианты `aa`/`aaa` начинают с двух/трёх букв и увеличивают длину по исчерпании. Смешанная схема: `00a … 00z, 01a … 99z, 100a …`.
- Начальное значение для числовой схемы — сам номер (можно 0), для буквенной/смешанной — позиция (1 = первое имя). Фильтр исходных имён независим: чтобы превратить числовые имена в буквы, выберите **«Все выбранные»**.
- Занятые имена, варианты регистра и имена устройств Windows (`con`, `aux` и т. п.) пропускаются. Имена исходников остаются в резерве; перезаписи и циклического переименования через временные имена нет. Возможны пропуски.
- Откройте **Фото → Переименовать все**. Открытый снимок больше не обязателен: в диалоге есть **«Выбрать…»** для любой папки, включая папки только с видео или музыкой.
- Независимые галочки: **«Фото / RAW»**, **«Видео»**, **«Музыка / аудио»**. Можно выбрать одну, несколько или все; нужен хотя бы один тип. По умолчанию включены фото, видео/музыка выключены.
- **«Связанные файлы и резервные копии»** — отдельная галочка, включена по умолчанию. Метаданные и копии следуют за выбранным владельцем. Субтитры SRT/ASS/SSA/VTT/SUB/IDX следуют только за выбранным видео. Неотмеченные медиа не переименовываются даже при совпадении имени со снимком: для MOV от Live Photo/голосовой заметки отметьте также видео/музыку.
- Неоднозначный `same.xmp` оставляется на месте, если `same.jpg`/`same.mp3`/`same.mov` относится к неотмеченной категории. Явный `same.mp3.xmp` следует только за своим выбранным владельцем.
- Режим числовых имён выбирается отдельно: **«Только нечисловые имена»** (по умолчанию) или **«Все выбранные, включая числовые имена»**. Занятые номера всегда пропускаются, даже для участвующих источников: возможны пропуски, это не сплошная перенумерация 001…N.
- Выбранные медиа с общей основой имени получают одно новое имя; расширения и папки сохраняются. При выключенных спутниках выбранные RAW/JPG всё равно могут идти вместе: оба относятся к отмеченной категории фото.
- Перед подтверждением показываются категории, количество файлов и пример. Порядок нумерации берётся из сортировки на панели фоторежима, в том числе для папки только с видео/музыкой.
- Если текущий трек приложения входит в подтверждённый план, он освобождается перед перемещением. Внутренний плейлист обновляется только по выполненным изменениям, включая частичную отмену. Автозапуска нет; Play загрузит трек заново. Внешние M3U/PLS-плейлисты, встроенные теги и ссылки других программ не переписываются.
- Категории выбираются **по расширению**, не по декодированному содержимому. Список относится только к переименованию: он не добавляет кодеки просмотра/аудио или видеоплеер. `.ts` намеренно исключён, поскольку это может быть исходный код TypeScript; `.mts`/`.m2ts` включены.

| Категория | Расширения для переименования |
|---|---|
| Фото / RAW | `.jpg`, `.jpeg`, `.jfif`, `.png`, `.bmp`, `.webp`, `.tiff`, `.tif`, `.gif`, `.heic`, `.heif`, `.hif`, `.avif`, `.apng`, `.jxl`, `.jxr`, `.wdp`, `.hdp`, `.tga`, `.psd`, `.psb`, `.svg`, `.ico`, `.cr2`, `.cr3`, `.crw`, `.nef`, `.nrw`, `.arw`, `.srf`, `.sr2`, `.raf`, `.orf`, `.rw2`, `.rwl`, `.pef`, `.dng`, `.srw`, `.x3f`, `.3fr`, `.fff`, `.iiq`, `.mos`, `.mrw`, `.erf`, `.kdc`, `.dcr`, `.raw` |
| Видео | `.mp4`, `.m4v`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.webm`, `.mpeg`, `.mpg`, `.mpe`, `.m2v`, `.mts`, `.m2ts`, `.vob`, `.ogv`, `.3gp`, `.3g2`, `.flv`, `.f4v`, `.asf`, `.divx` |
| Музыка / аудио | `.mp3`, `.flac`, `.wav`, `.ogg`, `.opus`, `.aac`, `.m4a`, `.m4b`, `.m4r`, `.wma`, `.aif`, `.aiff`, `.aifc`, `.alac`, `.ape`, `.wv`, `.mka`, `.mp2`, `.mpa`, `.oga`, `.spx`, `.ac3`, `.eac3`, `.dts`, `.amr`, `.au`, `.snd` |

#### Интерфейс и локальные данные
- Английский, русский и испанский языки; мгновенное переключение, тёмный интерфейс.
- Настраиваются язык при запуске, сортировка, громкость и подтверждение удаления фото. Запоминание языка, сортировки и громкости управляется отдельно.
- **Приложение записывает данные на диск.** Папка `%LOCALAPPDATA%\PhotoMusicViewer`: `preferences.json`, необязательный `translation-settings.json`, записи восстановления в `pending` файлы-метки опций и временный незашифрованный PCM-кэш потокового Opus в `OpusCache` (до 64 МиБ на ридер, удаляется при закрытии).
- Сохранение настроек перевода **выключено по умолчанию**. При включении API-ключи шифруются DPAPI для текущей учётной записи Windows; остальные параметры записываются в JSON. DPAPI не защищает от программ, уже работающих от имени этого пользователя.
- Диагностический журнал **выключен по умолчанию**. При включении хранится в `logs`; размер ограничен, файлы ротируются, чувствительные поля очищаются. Переменная `PMV_LOG=on`, `debug` или `off` переопределяет обычный выбор журнала.
- Дисковый кэш миниатюр **выключен по умолчанию**. Папка `thumbs`, целевой предел 256 МиБ, очистка в настройках. Изображения миниатюр не зашифрованы: хеширование имён файлов не является шифрованием картинок.
- **Очистка истории Windows — только по выбору пользователя, по умолчанию выключена.** Неразобранные ярлыки «Недавних» пропускаются. Реестр Проводника очищается только при достоверном совпадении полного пути; значения и порядок MRU обновляются транзакционно. Если транзакционный доступ недоступен, небезопасного запасного способа записи нет. Это выборочная уборка, а не анонимность и не удаление всех системных следов.
- Текст и изображения в буфере помечаются для исключения из истории Windows и облачной синхронизации. При выходе приложение пытается убрать только своё содержимое, не трогая более новое копирование другой программы. Эти флаги не запрещают другим программам читать буфер.

### Сохранение и целостность файлов
- При замене рядом остаётся **незашифрованный** `*.pmv-original-….bak`, включая исходные личные метаданные. Копии не удаляются автоматически. Восстановление работает после перезапуска, если сохранённый файл проходит проверки; восстановление или явное удаление копии обычно отправляет её в корзину.
- Для временных артефактов замены ведутся записи восстановления. При неудачном возврате оригинала сохранившаяся копия и запись остаются для повторной попытки. Блокировки, отказ доступа или небезопасный путь могут потребовать ручного восстановления. После ошибки не удаляйте `.bak` и папку `pending` вслепую.
- Массовое переименование и аварийная уборка проверяют родительские компоненты, reparse points и фактические пути Windows. Неподдерживаемые/неоднозначные пути отклоняются; это не общая песочница для всех операций приложения.
- Храните независимые резервные копии. Массовое переименование **не является аварийно-атомарной транзакцией всей группы**: прерывание процесса может разделить связанные файлы. Публикация новой копии не даёт универсальной защиты от отключения питания. Одиночное переименование не обеспечивает группировку спутников и копий, как массовое.
- Ограничения ресурсов, проверка версий и восстановление — защитные меры, а не гарантия от любого сбоя, вмешательства другого процесса или особенностей файловой системы. Редактирование многостраничных TIFF и часть операций с форматами/метаданными ограничены; при перекодировании не-JPEG сохранение метаданных не гарантируется.

### Горячие клавиши

| Режим | Клавиши | Действие |
| --- | --- | --- |
| Фото | `←` / `→` | Предыдущее / следующее изображение |
| Фото | `Home` / `End` | Первое / последнее изображение |
| Фото | `Space` | Пуск/пауза GIF |
| Фото | `R` | Повернуть вид, не файл |
| Фото | `Ctrl+C` | Копировать изображение |
| Фото | `Delete` | Удалить текущий файл |
| Фото | `F11` | Переключить полный экран |
| Фото | `Esc` | Выйти из полного экрана/сетки/обрезки либо запросить отмену файловой операции |
| Музыка | `Space` | Пуск/пауза |
| Музыка | `←` / `→` | Перемотка на 5 секунд |
| Музыка | `Ctrl+←` / `Ctrl+→` | Предыдущий / следующий трек |

Работа клавиш зависит от фокуса: поля ввода и диалоги могут перехватывать их. Отмена файловой операции выполняется в безопасных точках и не откатывает уже завершённые изменения.

### Мышь
- **Фото:** ПКМ — копировать; средняя кнопка — сбросить вид; колесо — масштаб или размер рамки обрезки; перетаскивание ЛКМ — перемещение при увеличении либо передача файла при обычном масштабе. Клик по имени — переименование; по миниатюре — открыть изображение/папку; перенос файла/папки в окно — открыть.
- **Музыка:** двойной клик запускает трек; перетаскивание меняет порядок; клик по названию — переименование. Колесо над громкостью — шаг 5%, над скоростью — 0.1×; клик по значению скорости — сброс. Перенос файлов/папок добавляет треки.

### Требования
- Windows 10 версии 2004 (сборка 19041) или новее / Windows 11.
- Рекомендуемая цель сборки: **Windows x64**, комплектный `jpegtran` — только x64.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) для сборки с внешним runtime; самодостаточной публикации не нужен.
- Языковые пакеты OCR Windows для локального распознавания.
- Нужные кодеки Windows для HEIC/HEIF/HIF, WEBP и RAW. Распознавание расширения ещё не гарантирует наличие декодера.
- Интернет, свои API-ключи и права/баланс сервисов — только для сетевых функций. Для сборки нужны .NET 8 SDK и доступ к восстановлению пакетов.

### Сборка и запуск

Команды из корня репозитория, где расположена папка `app/`:

```powershell
git clone https://github.com/re-quies/PhotoMusicViewer
cd PhotoMusicViewer
dotnet build app/PhotoMusicViewer.csproj -c Release
dotnet run --project app/PhotoMusicViewer.csproj -c Release
```

Самодостаточная публикация для Windows x64:

```powershell
dotnet publish app/PhotoMusicViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Результат: `app/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.

**Распространяйте всю папку publish**, а не один `PhotoMusicViewer.exe`: `tools/jpegtran/jpegtran.exe` и `libjpeg-62.dll` должны находиться вместе в `tools/jpegtran` рядом с приложением, с файлами лицензий. Параметр single-file не встраивает этот нативный инструмент. `.csproj` копирует его автоматически. При отсутствии инструмента или неподдерживаемой архитектуре соответствующие JPEG-операции без потерь отклоняются, без скрытого перехода к потерям.

Для установщика после публикации откройте корневой `installer.iss` в Inno Setup 6 и нажмите Compile. Сценарий упаковывает всё дерево publish, берёт версию из EXE и сохраняет установщик в `installer-output/`.

Архив для GitHub/сборки содержит исходники приложения и документацию установки, но не регрессионные тесты. Компиляция не заменяет запуск на Windows и проверку WPF, Shell, транзакций реестра, junction и нативных JPEG-операций.

### Технологии и комплектные компоненты
- C# / .NET 8 / WPF; Windows MediaPlayer, [NAudio](https://github.com/naudio/NAudio) 2.3.0 / NAudio.Vorbis 1.5.0.
- [Concentus](https://github.com/lostromb/concentus) 2.2.2 / Concentus.Oggfile 1.0.7 для OPUS.
- `Windows.Media.Ocr`; DPAPI Windows (`System.Security.Cryptography.ProtectedData` 8.0.0).
- [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo) 3.2.0, комплектный `jpegtran` Windows x64. Лицензии и происхождение бинарников: `app/tools/jpegtran/LICENSE.md`, `README.ijg`, `PROVENANCE.txt`. Лицензии сторонних компонентов не задают лицензию собственного кода приложения.

---

<a name="español"></a>

## Español

**PhotoMusicViewer** combina un visor de imágenes, edición básica, reconocimiento de texto y un reproductor de música. No requiere instalador. Las fotos, la música local y el OCR de Windows funcionan sin conexión; la traducción y el reconocimiento en la nube son opcionales y requieren tus propias claves API.

### Características

#### Modo Foto
- **Controles compactos:** icono → deslizador → valor en una fila para velocidad y volumen. Bloque superior de 28 (antes 42); cantidad sin cambios. Iconos con ayuda localizada; en ventanas estrechas el bloque se amplía a la izquierda sin reducir el texto.
- **Distribución musical:** barra de progreso, título y tiempo en el centro inferior, entre modos y acciones. Cantidad, velocidad y volumen en un bloque de 28 unidades justo encima de las acciones, desde Añadir carpeta hasta el borde derecho; el bloque superior usa el fondo de la lista y solo reserva la esquina inferior derecha; la lista llega a la barra inferior por la izquierda, sin franja vacía de ancho completo.
- **Barra de música:** iconos vectoriales originales con ayuda localizada. Reproducción/pausa, repetición no/todo/uno y ordenación tienen estados distintos. Texto restante (nombres, tiempo, velocidad, volumen, cantidad y ordenación) y ayuda: tamaño 13. Quitar/Vaciar solo cambian la lista; no borran archivos. Barra inferior de 42 unidades, selectores alineados con Foto.
- **Reflejar vista:** clic horizontal, doble clic vertical, intervalo de 150 ms como Abrir. El archivo no cambia hasta guardar. Se guardan el giro y el reflejo juntos: copia predeterminada o reemplazo explícito con respaldo. Se admite guardar JPEG, PNG, BMP y TIFF de una página; no se reflejan GIF animados. Texto de nombre, datos, zoom y ordenación: tamaño 13.
- **Barra de iconos:** Abrir → Cuadrícula → Recortar → Girar → Reflejar → OCR → Pantalla completa → EXIF → JPG (solo WebP) → Renombrar → Ajustes → Eliminar. Dibujos vectoriales originales con nombre y explicación previa al pasar el cursor; los selectores ocupan la misma posición en ambos modos; solo el nombre queda a la izquierda y los datos y el zoom quedan a la derecha con texto tenue. Ambas barras tienen 42 unidades de altura y las acciones de foto compactas se alinean a la derecha en una fila. Los selectores Foto/Música usan los mismos iconos en ambos modos. El idioma se cambia en Ajustes; se quitaron los botones de idioma de ambos modos.
- **Abrir:** un clic abre el selector de imágenes; doble clic, el selector de carpetas. La demora de un clic configurada por el usuario es **150 ms**; el segundo clic debe llegar antes de que expire. La carpeta elegida muestra miniaturas de fotos, subcarpetas y un botón para subir. Seleccione una miniatura para verla. Una carpeta vacía mantiene la cuadrícula. Arrastrar una carpeta en Foto abre la cuadrícula; en Música la añade a la lista.
- **Extensiones:** JPG, JPEG, JFIF, PNG, BMP, WEBP, TIFF, TIF, GIF, HEIC, HEIF, HIF y RAW (CR2, NEF, ARW). La decodificación depende de los códecs de Windows instalados; poder visualizar un formato no implica poder guardarlo en ese mismo formato.
- **GIF animados:** composición con transparencia, desplazamientos y eliminación de fotogramas; reproducir/pausar. Los límites de recursos pueden rechazar imágenes o animaciones grandes.
- **Navegación:** anterior/siguiente, primera/última imagen, arrastrar y soltar, cuadrícula con navegación por carpetas y pantalla completa.
- **Zoom:** porcentajes respecto a los píxeles originales, con escala del monitor: 100% corresponde a un píxel de imagen por píxel físico de pantalla. La imagen sigue ajustándose a la ventana y el botón central vuelve a esa vista, no necesariamente al 100%. Pasos y desplazamiento sin cambios; mínimo 0.2× de la vista ajustada, máximo 10 000% de píxeles originales. Se conserva el ajuste de imágenes diminutas que ya superen el límite. Se actualiza al redimensionar y no depende de la copia decodificada ni del DPI del archivo.
- **Acceso a 100%:** clic en el porcentaje muestra píxel por píxel y centra la imagen. El botón central sigue ajustando la imagen a la ventana; ayuda emergente para ambas acciones.
- **Portapapeles:** botón derecho o `Ctrl+C` copia la imagen; arrastrarla a zoom normal entrega el archivo a otra aplicación.
- **Controles de ordenación:** fondo oscuro como el área de vista y texto como el nombre de foto en ambos modos, también en la lista desplegada. Cambiar criterio o dirección reordena inmediatamente la carpeta actual en cuadrícula; subir y subcarpetas permanecen antes de las fotos.
- **Ordenación:** nombres en orden natural, fecha de modificación, tamaño o tipo, en ambas direcciones. Fotos por defecto: fecha de modificación, nuevas primero.
- **Orientación EXIF** aplicada al visualizar.
- **Giro:** pasos de 90° solo cambian la vista hasta elegir **Guardar giro**.
- **Recorte:** selección interactiva, guardando una copia por defecto. JPEG permite recorte sin pérdidas sobre la cuadrícula MCU o exacto con recodificación de calidad 95. El marco sin pérdidas puede ampliarse a los bloques JPEG; el diálogo muestra el área resultante.
- **Guardar giro / Quitar EXIF:** copia por defecto; reemplazo explícito. JPEG usa `jpegtran` sin pérdidas; se rechazan giros incompatibles en lugar de recortar o recodificar silenciosamente. La recodificación JPEG con pérdidas es una alternativa explícita.
- **Quitar EXIF** también elimina GPS, datos de cámara y perfiles de color. Quitar el perfil puede cambiar los colores. Admite JPEG/JFIF, PNG, BMP y TIFF.
- **WEBP → JPG:** crea un JPEG nuevo; eliminar el origen es una acción separada con confirmación. Se comprueba que el origen corresponde a la versión usada para convertirlo.
- **Renombrar:** foto actual en línea; renombrado por lotes para las categorías foto/RAW, vídeo y música seleccionadas, con elección de carpeta y subcarpetas opcionales. Véase la sección de renombrado masivo.
- **Eliminar:** mediante la papelera de Windows; advertencia adicional si se requiere eliminación permanente.
- **Restaurar original / Eliminar copia de seguridad:** gestión de la copia conservada tras reemplazar una imagen.

#### OCR y traducción
- **OCR local:** `Windows.Media.Ocr` con los idiomas de reconocimiento disponibles, sin subir la imagen.
- **Traducción de texto:** DeepL, Google Gemini, Google Cloud Translation v2 o Qwen (Alibaba Model Studio / DashScope).
- **Foto → texto / texto + traducción:** Gemini y modelos de visión Qwen procesan la propia imagen. Cloud Translation v2 solo admite texto.
- Idiomas de origen/destino, modelos, plantillas de prompts y opciones de cada proveedor. Qwen permite regiones y una URL API personalizada; la clave debe corresponder al destino/región.
- **Confirmación de envío activada por defecto.** Permisos separados para texto, imágenes y pruebas del servicio, asociados al origen del destino durante la sesión. Desactivarla permite solicitudes HTTPS admitidas sin preguntar cada vez.
- Se eliminan metadatos al preparar la imagen para enviarla por defecto, pero eso **no oculta datos privados visibles en los píxeles**. El proveedor puede cobrar por el uso de la API; revisa sus políticas de privacidad y retención antes de enviar contenido privado.

#### Modo Música
- **Extensiones:** MP3, FLAC, WAV, OGG, OPUS, AAC, M4A; algunos formatos dependen del soporte multimedia de Windows.
- **Decodificación según contenido:** Ogg Opus/Vorbis se reconoce incluso con una extensión incorrecta. MP4/M4A normal con Opus mono/estéreo usa Concentus integrado, sin códec Opus de Windows ni FFmpeg externo. AAC/ALAC y otros formatos del sistema siguen dependiendo de Windows. No admite MP4 fragmentado, Opus cifrado/multicanal, varias pistas de audio ni edit lists complejas. El filtro de extensiones no cambia; los archivos `.mp4` arbitrarios no se añaden automáticamente.
- Lista de reproducción, reordenación por arrastre, cambio de nombre del archivo y añadir archivos/carpetas.
- Reproducir/pausar, anterior/siguiente, búsqueda, aleatorio y repetición no/uno/todo.
- **Velocidad:** 0.1×–3.0×. Cambia también el tono; no se implementa estiramiento temporal conservando el tono. Clic en el valor para volver a 1.0×.
- Volumen y ordenación por nombre, fecha, tamaño o tipo.

#### Renombrado masivo: tipos multimedia
- **12 esquemas:** números con anchura 1–6 (`1`, `01`, `001`, `0001`, `00001`, `000001`); letras desde `a`, `aa` o `aaa`; mixtos `0a`, `00a` o `000a`.
- Letras latinas minúsculas: `a … z, aa … zz, aaa …`; las variantes más largas empiezan en `aa`/`aaa` y crecen al agotarse. Mixtos: `00a … 00z, 01a … 99z, 100a …`.
- Inicio numérico = número real (se admite 0); letras/mixtos = posición (1 = primer nombre). El filtro de nombres originales es independiente: elija **Todos los seleccionados** para convertir nombres numéricos a letras.
- Se omiten nombres ocupados, variantes de mayúsculas y nombres de dispositivos Windows (`con`, `aux`, etc.). Los originales permanecen reservados; sin sobrescritura ni renombrados cíclicos temporales. Puede haber saltos.
- Abra **Foto → Renombrar todo**. No hace falta abrir una imagen: **Elegir…** permite seleccionar cualquier carpeta, incluso solo de vídeo o música.
- Casillas independientes: **Fotos / RAW**, **Vídeos**, **Música / audio**. Una, varias o todas; al menos una es obligatoria. Por defecto: fotos activadas, vídeo/música desactivados.
- **Incluir auxiliares y copias** es una casilla separada, activada por defecto. Metadatos y copias siguen a su propietario seleccionado. Los subtítulos SRT/ASS/SSA/VTT/SUB/IDX siguen solo al vídeo seleccionado. Los medios no marcados no se renombran por compartir nombre: marque también vídeo/audio para Live Photo o notas de voz.
- Se deja un `same.xmp` ambiguo si `same.jpg`/`same.mp3`/`same.mov` pertenece a una categoría no marcada. Un `same.mp3.xmp` explícito sigue solo a su propietario marcado.
- Modo numérico separado: **Solo nombres no numéricos** (por defecto) o **Todos los seleccionados, incluidos nombres numéricos**. Se saltan todos los números ocupados, incluso de archivos participantes; puede haber huecos, no una renumeración compacta 001…N.
- Los medios primarios seleccionados con la misma base reciben el mismo nombre generado; carpetas/extensiones se conservan. RAW/JPG seleccionados pueden ir juntos aun sin auxiliares porque ambos son de la categoría de fotos.
- La confirmación muestra categorías, cantidades y un ejemplo. Se usa el orden de la barra del modo Foto, también en carpetas solo multimedia.
- Si la pista actual participa, se libera antes de moverla. La lista interna aplica solo cambios completados, incluidos resultados parciales/cancelados. No hay reproducción automática; Play vuelve a cargarla. No se reescriben listas externas M3U/PLS, etiquetas ni referencias de otras aplicaciones.
- Selección **por extensión**, no contenido decodificado. Estas listas solo permiten renombrar: no añaden códecs ni un reproductor de vídeo. Se excluye `.ts` por posible código TypeScript; se incluyen `.mts`/`.m2ts`.

| Categoría | Extensiones para renombrar |
|---|---|
| Fotos / RAW | `.jpg`, `.jpeg`, `.jfif`, `.png`, `.bmp`, `.webp`, `.tiff`, `.tif`, `.gif`, `.heic`, `.heif`, `.hif`, `.avif`, `.apng`, `.jxl`, `.jxr`, `.wdp`, `.hdp`, `.tga`, `.psd`, `.psb`, `.svg`, `.ico`, `.cr2`, `.cr3`, `.crw`, `.nef`, `.nrw`, `.arw`, `.srf`, `.sr2`, `.raf`, `.orf`, `.rw2`, `.rwl`, `.pef`, `.dng`, `.srw`, `.x3f`, `.3fr`, `.fff`, `.iiq`, `.mos`, `.mrw`, `.erf`, `.kdc`, `.dcr`, `.raw` |
| Vídeos | `.mp4`, `.m4v`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.webm`, `.mpeg`, `.mpg`, `.mpe`, `.m2v`, `.mts`, `.m2ts`, `.vob`, `.ogv`, `.3gp`, `.3g2`, `.flv`, `.f4v`, `.asf`, `.divx` |
| Música / audio | `.mp3`, `.flac`, `.wav`, `.ogg`, `.opus`, `.aac`, `.m4a`, `.m4b`, `.m4r`, `.wma`, `.aif`, `.aiff`, `.aifc`, `.alac`, `.ape`, `.wv`, `.mka`, `.mp2`, `.mpa`, `.oga`, `.spx`, `.ac3`, `.eac3`, `.dts`, `.amr`, `.au`, `.snd` |

#### Interfaz y datos locales
- Inglés, ruso y español, cambio instantáneo y tema oscuro.
- Idioma inicial, ordenación, volumen y confirmación al borrar fotos configurables. Se controla por separado si se recuerdan idioma, orden y volumen.
- **La aplicación sí escribe datos en disco.** `%LOCALAPPDATA%\PhotoMusicViewer` contiene `preferences.json`, el archivo opcional `translation-settings.json`, registros de recuperación en `pending` marcadores de opciones y caché PCM temporal de Opus sin cifrar en `OpusCache` (hasta 64 MiB por lector, borrada al cerrar).
- Guardar ajustes de traducción está **desactivado por defecto**. Al activarlo, las claves API se cifran con DPAPI para la cuenta actual de Windows; el resto se guarda en JSON. DPAPI no protege contra programas que ya se ejecutan como ese usuario.
- Registro de diagnóstico **desactivado por defecto**; archivos opcionales en `logs`, con límite, rotación y limpieza de campos sensibles. `PMV_LOG=on`, `debug` u `off` modifica la selección normal.
- Caché de miniaturas en disco **desactivada por defecto**; carpeta `thumbs`, límite objetivo de 256 MiB y limpieza desde ajustes. Las imágenes no están cifradas; los nombres hash no cifran las miniaturas.
- **Limpieza del historial de Windows opcional y desactivada por defecto.** Se omiten accesos directos cuyo destino no se pueda resolver. La limpieza del registro de Explorer exige una ruta completa inequívoca y usa una transacción para valores y orden MRU; no recurre a escrituras inseguras si falla el acceso transaccional. No garantiza anonimato ni elimina todas las huellas del sistema.
- El contenido copiado se marca para excluirlo del historial y la sincronización del portapapeles de Windows. Al salir, se intenta borrar solo el contenido propio, sin borrar una copia posterior de otra aplicación. Las marcas no impiden que otros programas lean el portapapeles.

### Guardado e integridad
- Reemplazar una imagen deja un `*.pmv-original-….bak` **sin cifrar** junto a ella, incluidos los metadatos privados originales. No se borra automáticamente. La restauración funciona tras reiniciar si el archivo guardado supera las comprobaciones; restaurar o eliminar la copia normalmente la envía a la papelera.
- Los artefactos temporales de reemplazo tienen registros de recuperación. Si falla la restauración, se conservan la copia superviviente y el registro para reintentar. Bloqueos, permisos o rutas inseguras pueden requerir recuperación manual. No borres `.bak` ni `pending` a ciegas después de un fallo.
- El renombrado por lotes y la limpieza de recuperación comprueban componentes padre, puntos de reanálisis y rutas reales de Windows. Se rechazan rutas ambiguas/no admitidas; no es un aislamiento general de todas las operaciones.
- Mantén copias independientes. El renombrado por lotes **no es una transacción atómica ante fallos de todo el grupo**: una interrupción puede separar archivos relacionados. Publicar una copia nueva no garantiza protección universal ante cortes de energía. Renombrar un archivo individual no agrupa auxiliares y copias como el modo por lotes.
- Los límites, comprobaciones de versión y recuperación son medidas defensivas, no una garantía contra cualquier fallo, proceso hostil del mismo usuario o comportamiento del sistema de archivos. La edición de TIFF multipágina y algunas operaciones están restringidas; recodificar formatos distintos de JPEG no garantiza conservar metadatos.

### Atajos de teclado

| Modo | Teclas | Acción |
| --- | --- | --- |
| Foto | `←` / `→` | Imagen anterior / siguiente |
| Foto | `Home` / `End` | Primera / última imagen |
| Foto | `Space` | Reproducir/pausar GIF |
| Foto | `R` | Girar la vista, no el archivo |
| Foto | `Ctrl+C` | Copiar imagen |
| Foto | `Delete` | Eliminar archivo actual |
| Foto | `F11` | Alternar pantalla completa |
| Foto | `Esc` | Salir de pantalla completa/cuadrícula/recorte o solicitar cancelar una operación de archivos |
| Música | `Space` | Reproducir/pausar |
| Música | `←` / `→` | Buscar 5 segundos atrás/adelante |
| Música | `Ctrl+←` / `Ctrl+→` | Pista anterior / siguiente |

Dependen del foco; los campos de texto y diálogos pueden capturarlos. La cancelación es cooperativa y no deshace cambios ya terminados.

### Ratón
- **Foto:** clic derecho copia; central restablece; rueda cambia zoom o tamaño del recorte; arrastre izquierdo desplaza con zoom o entrega el archivo a zoom normal. Clic en nombre para renombrar, en miniatura para abrir imagen/carpeta; soltar archivo/carpeta para abrir.
- **Música:** doble clic reproduce; arrastre reordena; clic en título renombra. Rueda sobre volumen: 5%; sobre velocidad: 0.1×; clic en el valor de velocidad restablece. Soltar archivos/carpetas añade pistas.

### Requisitos
- Windows 10 versión 2004 (compilación 19041) o posterior / Windows 11.
- Destino recomendado: **Windows x64**; `jpegtran` incluido es solo x64.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) para builds dependientes del framework; innecesario para publicación autocontenida.
- Paquetes de idiomas OCR de Windows.
- Códecs correspondientes para HEIC/HEIF/HIF, WEBP y RAW. Reconocer la extensión no garantiza un decodificador disponible.
- Internet, claves propias y permisos/saldo del proveedor solo para funciones en línea. Compilar requiere .NET 8 SDK y acceso a restauración de paquetes.

### Compilar y ejecutar

Desde la raíz del repositorio que contiene `app/`:

```powershell
git clone https://github.com/re-quies/PhotoMusicViewer
cd PhotoMusicViewer
dotnet build app/PhotoMusicViewer.csproj -c Release
dotnet run --project app/PhotoMusicViewer.csproj -c Release
```

Publicación autocontenida para Windows x64:

```powershell
dotnet publish app/PhotoMusicViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Salida: `app/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.

**Distribuye toda la carpeta publish**, no solo `PhotoMusicViewer.exe`: `tools/jpegtran/jpegtran.exe` y `libjpeg-62.dll` deben permanecer juntos en `tools/jpegtran` junto a la aplicación, con sus licencias. Single-file no integra esta herramienta nativa; el `.csproj` la copia automáticamente. Si falta o la arquitectura no se admite, se rechazan las operaciones JPEG sin pérdidas afectadas; no hay conversión con pérdidas silenciosa.

Para crear el instalador, abre `installer.iss` de la raíz en Inno Setup 6 después de publicar. Empaqueta todo el árbol publish, obtiene la versión del EXE y guarda el instalador en `installer-output/`.

El archivo GitHub/compilación contiene fuentes y documentación de instalación, no las pruebas de regresión. Compilar no sustituye las pruebas en Windows de WPF, Shell, transacciones del registro, junctions y operaciones JPEG nativas.

### Tecnologías y componentes incluidos
- C# / .NET 8 / WPF; Windows MediaPlayer y [NAudio](https://github.com/naudio/NAudio) 2.3.0 / NAudio.Vorbis 1.5.0.
- [Concentus](https://github.com/lostromb/concentus) 2.2.2 / Concentus.Oggfile 1.0.7 para OPUS.
- `Windows.Media.Ocr`; DPAPI de Windows (`System.Security.Cryptography.ProtectedData` 8.0.0).
- [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo) 3.2.0, `jpegtran` Windows x64 incluido. Avisos y procedencia en `app/tools/jpegtran/LICENSE.md`, `README.ijg` y `PROVENANCE.txt`. Las licencias de terceros no otorgan licencia al código propio de la aplicación.
