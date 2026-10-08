using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    /// <summary>
    /// Меню настроек перевода: ключи, модель, промты, языки, сеть и приватность.
    /// Редактирует копию настроек и применяет её только по кнопке OK.
    /// </summary>
    public partial class TranslationSettingsWindow : Window
    {
        private readonly TranslationSettings _draft;
        private readonly ObservableCollection<PromptPreset> _prompts;
        private PromptPreset? _editing;
        private CancellationTokenSource? _cts;
        private bool _loading;

        public TranslationSettingsWindow()
        {
            InitializeComponent();

            _draft = TranslationConfig.Current.Clone();
            _draft.EnsureValid();
            _prompts = new ObservableCollection<PromptPreset>(_draft.Prompts);

            ApplyLocalization();
            FillLanguages();
            FillFromDraft();

            Closed += (_, _) => { try { _cts?.Cancel(); } catch (Exception ex) { AppLog.Debug("TranslationSettingsWindow.Closed", ex); } };
        }

        /// <summary>Страница проекта. Открывается только проверенный https-адрес — не файл и не скрипт.</summary>
        private void GitHub_Click(object sender, RoutedEventArgs e)
        {
            if (!AppInfo.TryGetSafeLink(AppInfo.GitHubUrl, out var uri))
            {
                MessageBox.Show(this,
                    Loc.T("The GitHub link is invalid. Only https addresses are opened.",
                          "Неверная ссылка на GitHub. Открываются только адреса https.",
                          "El enlace de GitHub no es válido. Solo se abren direcciones https."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationSettingsWindow.OpenGitHub", ex);
                MessageBox.Show(this,
                    Loc.T("Couldn't open the browser: ", "Не удалось открыть браузер: ", "No se pudo abrir el navegador: ") + ex.Message,
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyLocalization()
        {
            Title = Loc.T("Settings", "Настройки", "Ajustes");

            InterfaceHeader.Text = Loc.T("Interface", "Интерфейс", "Interfaz");
            StartupLanguageLabel.Text = Loc.T("Language at startup", "Язык при запуске", "Idioma al iniciar");
            SetComboItemText(StartupLanguageCombo, 0, Loc.T("As in Windows", "Как в Windows", "Como en Windows"));
            RememberLanguageCheck.Content = Loc.T("Remember the language chosen with the EN / RU / ES button",
                "Запоминать язык, выбранный кнопкой EN / RU / ES", "Recordar el idioma elegido con el botón EN / RU / ES");
            PhotoSortLabel.Text = Loc.T("Photo sorting", "Сортировка фото", "Orden de fotos");
            MusicSortLabel.Text = Loc.T("Playlist sorting", "Сортировка плейлиста", "Orden de la lista");
            foreach (var combo in new[] { PhotoSortCombo, MusicSortCombo })
            {
                SetComboItemText(combo, 0, Loc.T("Name", "Имя", "Nombre"));
                SetComboItemText(combo, 1, Loc.T("Date modified", "Дата изменения", "Fecha de modificación"));
                SetComboItemText(combo, 2, Loc.T("Size", "Размер", "Tamaño"));
                SetComboItemText(combo, 3, Loc.T("Type", "Тип", "Tipo"));
            }
            foreach (var combo in new[] { PhotoSortDirectionCombo, MusicSortDirectionCombo })
            {
                SetComboItemText(combo, 0, Loc.T("Ascending ↑", "По возрастанию ↑", "Ascendente ↑"));
                SetComboItemText(combo, 1, Loc.T("Descending ↓", "По убыванию ↓", "Descendente ↓"));
            }
            RememberSortCheck.Content = Loc.T("Remember the sorting chosen on the toolbar",
                "Запоминать сортировку, выбранную на панели", "Recordar el orden elegido en la barra");
            VolumeLabel.Text = Loc.T("Volume at startup", "Громкость при запуске", "Volumen al iniciar");
            RememberVolumeCheck.Content = Loc.T("Remember the last volume", "Запоминать последнюю громкость", "Recordar el último volumen");
            ConfirmDeleteCheck.Content = Loc.T("Ask before moving a photo to the Recycle Bin",
                "Спрашивать перед перемещением фото в корзину", "Preguntar antes de mover una foto a la papelera");
            ConfirmDeleteHint.Text = Loc.T(
                "If a file can't go to the Recycle Bin (USB stick, network drive, too large), Windows always asks before deleting it permanently — even with this option off.",
                "Если файл не может попасть в корзину (флешка, сетевой диск, слишком большой), Windows всегда спросит перед безвозвратным удалением — даже при выключенной галочке.",
                "Si un archivo no puede ir a la papelera (USB, red, demasiado grande), Windows siempre pregunta antes de borrarlo definitivamente, incluso sin esta opción.");
            InterfaceHint.Text = Loc.T(
                "Applied when you press OK. Photo sorting is used for every newly opened folder. With remembering on, changes on the toolbar replace these values. Stored in %LOCALAPPDATA%\\PhotoMusicViewer\\preferences.json — no file names or paths.",
                "Применяется по кнопке OK. Сортировка фото действует для каждой новой открытой папки. С запоминанием изменения на панели заменяют эти значения. Хранится в %LOCALAPPDATA%\\PhotoMusicViewer\\preferences.json — без имён файлов и путей.",
                "Se aplica al pulsar OK. El orden de fotos se usa en cada carpeta nueva. Con «recordar», los cambios en la barra sustituyen estos valores. Se guarda en %LOCALAPPDATA%\\PhotoMusicViewer\\preferences.json, sin nombres ni rutas.");

            KeysHeader.Text = Loc.T("API keys", "Ключи API", "Claves de API");
            DeepLKeyLabel.Text = Loc.T("DeepL API key", "Ключ DeepL", "Clave de DeepL");
            DeepLKeyHint.Text = Loc.T(
                "Free keys end with :fx and switch to api-free.deepl.com automatically.",
                "Бесплатные ключи заканчиваются на :fx — адрес api-free.deepl.com выбирается автоматически.",
                "Las claves gratuitas terminan en :fx y usan api-free.deepl.com automáticamente.");
            GoogleKeyLabel.Text = Loc.T("Google API key", "Ключ Google", "Clave de Google");
            GoogleKeyHint.Text = Loc.T(
                "One key covers Gemini and Cloud Translation if both APIs are enabled for it.",
                "Один ключ годится и для Gemini, и для Cloud Translation, если оба API включены.",
                "Una clave sirve para Gemini y Cloud Translation si ambas API están habilitadas.");
            QwenKeyLabel.Text = Loc.T("Qwen API key", "Ключ Qwen", "Clave de Qwen");
            QwenKeyHint.Text = Loc.T(
                "Key from Alibaba Cloud Model Studio (DashScope). It works only in the region where it was created.",
                "Ключ из Alibaba Cloud Model Studio (DashScope). Работает только в том регионе, где создан.",
                "Clave de Alibaba Cloud Model Studio (DashScope). Solo funciona en la región donde se creó.");
            ShowKeysCheck.Content = Loc.T("Show keys", "Показать ключи", "Mostrar claves");

            DeepLHeader.Text = "DeepL";
            GoogleHeader.Text = "Google";
            QwenHeader.Text = "Qwen";
            LanguagesHeader.Text = Loc.T("Languages", "Языки", "Idiomas");
            TargetLangLabel.Text = Loc.T("Translate into", "Переводить на", "Traducir a");
            SourceLangLabel.Text = Loc.T("Source language", "Язык источника", "Idioma de origen");

            GoogleEngineLabel.Text = Loc.T("Service for text translation",
                "Сервис для перевода текста", "Servicio para traducir texto");
            GeminiModelLabel.Text = Loc.T("Gemini model", "Модель Gemini", "Modelo de Gemini");
            LoadModelsButton.Content = Loc.T("Load list", "Загрузить список", "Cargar lista");
            GeminiModelHint.Text = Loc.T(
                "Flash models are faster and cheaper; Pro models read messy photos better.",
                "Flash быстрее и дешевле, Pro лучше читает сложные фото.",
                "Flash es más rápido y barato; Pro lee mejor fotos difíciles.");
            TemperatureLabel.Text = Loc.T("Temperature (0 = most literal)",
                "Температура (0 = максимально точно)", "Temperatura (0 = más literal)");

            QwenRegionLabel.Text = Loc.T("Region (must match the key)",
                "Регион (должен совпадать с ключом)", "Región (debe coincidir con la clave)");
            QwenModelLabel.Text = Loc.T("Model for text", "Модель для текста", "Modelo para texto");
            QwenVisionModelLabel.Text = Loc.T("Model for photos", "Модель для фото", "Modelo para fotos");
            QwenVisionModelHint.Text = Loc.T(
                "Only VL models can read images: qwen-vl-max, qwen-vl-plus, qwen-vl-ocr.",
                "Картинки читают только модели VL: qwen-vl-max, qwen-vl-plus, qwen-vl-ocr.",
                "Solo los modelos VL leen imágenes: qwen-vl-max, qwen-vl-plus, qwen-vl-ocr.");
            QwenTemperatureLabel.Text = Loc.T("Temperature (0 = most literal)",
                "Температура (0 = максимально точно)", "Temperatura (0 = más literal)");
            LoadQwenModelsButton.Content = Loc.T("Load list", "Загрузить список", "Cargar lista");
            QwenLoopbackHttpCheck.Content = Loc.T("Allow unencrypted HTTP only for localhost / loopback",
                "Разрешить незашифрованный HTTP только для localhost / loopback",
                "Permitir HTTP sin cifrar solo para localhost / loopback");
            QwenBaseUrlLabel.Text = Loc.T("Own Base URL (workspace domain)",
                "Свой адрес API (домен рабочего пространства)", "URL propia (dominio del espacio)");
            QwenBaseUrlHint.Text = Loc.T(
                "Used when the region list is set to \"Own Base URL\". Copy the Base URL from the Model Studio console.",
                "Используется, когда в списке регионов выбран пункт «Свой адрес». Скопируйте Base URL из консоли Model Studio.",
                "Se usa con la opción «URL propia». Copie la Base URL de la consola de Model Studio.");

            DeepLEndpointLabel.Text = Loc.T("Endpoint", "Адрес API", "Endpoint");
            DeepLFormalityLabel.Text = Loc.T("Formality", "Формальность", "Formalidad");
            DeepLModelTypeLabel.Text = Loc.T("Model", "Модель", "Modelo");
            DeepLContextLabel.Text = Loc.T("Context (not translated, guides word choice)",
                "Контекст (не переводится, влияет на выбор слов)",
                "Contexto (no se traduce, guía la elección de palabras)");
            DeepLContextHint.Text = Loc.T(
                "DeepL has no prompts — this field is the closest equivalent. Prompts below apply to Gemini and Qwen.",
                "У DeepL нет промтов — это поле самое близкое. Промты ниже — для Gemini и Qwen.",
                "DeepL no tiene prompts: este campo es lo más parecido. Los prompts son para Gemini y Qwen.");
            PreserveFormattingCheck.Content = Loc.T("Preserve formatting and line breaks",
                "Сохранять форматирование и переносы строк",
                "Conservar el formato y los saltos de línea");

            PromptsHeader.Text = Loc.T("Prompts (Gemini and Qwen)", "Промты (Gemini и Qwen)", "Prompts (Gemini y Qwen)");
            PromptsHint.Text = Loc.T(
                "Placeholders: {text} = text to translate, {target} = target language, {source} = source language.",
                "Плейсхолдеры: {text} = текст, {target} = язык перевода, {source} = язык источника.",
                "Marcadores: {text} = texto, {target} = idioma destino, {source} = idioma de origen.");
            PromptNameLabel.Text = Loc.T("Name", "Название", "Nombre");
            PromptTextLabel.Text = Loc.T("Prompt text", "Текст промта", "Texto del prompt");
            AddPromptButton.Content = Loc.T("Add", "Добавить", "Añadir");
            DeletePromptButton.Content = Loc.T("Delete", "Удалить", "Eliminar");
            ResetPromptsButton.Content = Loc.T("Reset", "Сбросить", "Restablecer");

            ImageOcrPromptLabel.Text = Loc.T("Prompt for \"photo -> text\"",
                "Промт для «фото → текст»", "Prompt para «foto → texto»");
            ImageOcrTranslatePromptLabel.Text = Loc.T("Prompt for \"photo -> text + translation\"",
                "Промт для «фото → текст + перевод»", "Prompt para «foto → texto + traducción»");
            ImageOcrTranslateHint.Text = Loc.T(
                "Keep the === TEXT === and === TRANSLATION === markers to split the answer into two panes.",
                "Оставьте маркеры === TEXT === и === TRANSLATION ===, иначе ответ не разделится на две панели.",
                "Mantenga los marcadores === TEXT === y === TRANSLATION === para dividir la respuesta.");

            PrivacyHeader.Text = Loc.T("Network and privacy", "Сеть и приватность", "Red y privacidad");
            TimeoutLabel.Text = Loc.T("Timeout, seconds", "Таймаут, секунд", "Tiempo de espera, s");
            MaxImageSideLabel.Text = Loc.T("Max image side before sending, px",
                "Макс. сторона картинки перед отправкой, пкс",
                "Lado máximo de la imagen antes de enviar, px");
            StripMetaCheck.Content = Loc.T(
                "Re-encode the photo before sending (removes EXIF/GPS, sends pixels only)",
                "Пересжимать фото перед отправкой (убирает EXIF/GPS, уходят только пиксели)",
                "Recodificar la foto antes de enviarla (elimina EXIF/GPS)");
            AskNetworkCheck.Content = Loc.T(
                "Ask for confirmation before sending — separately for text and for photos",
                "Спрашивать подтверждение перед отправкой — отдельно для текста и для фотографий",
                "Pedir confirmación antes de enviar: por separado para texto y para fotos");
            PersistCheck.Content = Loc.T(
                "Remember settings and keys on this computer (keys are encrypted with Windows DPAPI)",
                "Запоминать настройки и ключи на этом компьютере (ключи шифруются средствами Windows, DPAPI)",
                "Recordar ajustes y claves en este equipo (las claves se cifran con DPAPI de Windows)");
            DeleteFileButton.Content = Loc.T("Delete settings file", "Удалить файл настроек", "Eliminar el archivo");

            LogCheck.Content = Loc.T(
                "Keep a diagnostic log (error types only, no file names or text)",
                "Вести журнал диагностики (только типы ошибок, без имён файлов и текстов)",
                "Registro de diagnóstico (solo tipos de error, sin nombres ni textos)");
            TracesHeader.Text = Loc.T("Traces in Windows", "Следы в Windows", "Rastros en Windows");
            CleanRecentCheck.Content = Loc.T(
                "Remove opened files from Recent items (shortcuts in the Recent folder and this app's jump list)",
                "Убирать открытые файлы из «Недавних» (ярлыки в папке Recent и список переходов приложения)",
                "Quitar los archivos abiertos de Recientes (accesos directos y lista de saltos de la aplicación)");
            CleanRegistryCheck.Content = Loc.T(
                "Remove opened files from Explorer history in the registry (RecentDocs, open/save dialog history)",
                "Убирать открытые файлы из истории Проводника в реестре (RecentDocs, история диалогов открытия)",
                "Quitar los archivos abiertos del historial del Explorador en el registro (RecentDocs, diálogos)");
            TracesHint.Text = Loc.T(
                "Off by default. Applies immediately to files opened with a double click, drag and drop or the Open dialog. These options change Windows data that other programs also use.",
                "По умолчанию выключено. Действует сразу — для файлов, открытых двойным кликом, перетаскиванием или через диалог открытия. Эти функции изменяют данные Windows, которыми пользуются и другие программы.",
                "Desactivado por defecto. Se aplica de inmediato. Estas opciones modifican datos de Windows que usan otros programas.");
            ThumbCacheCheck.Content = Loc.T(
                "Keep thumbnails on disk (the thumbnail grid opens faster)",
                "Хранить миниатюры на диске (сетка миниатюр открывается быстрее)",
                "Guardar miniaturas en el disco (la cuadrícula se abre más rápido)");
            ClearThumbCacheButton.Content = Loc.T("Clear thumbnail cache", "Очистить кэш миниатюр", "Borrar caché de miniaturas");
            OpenLogFolderButton.Content = Loc.T("Open log folder", "Открыть папку журнала", "Abrir la carpeta del registro");
            DeleteLogButton.Content = Loc.T("Delete log", "Удалить журнал", "Eliminar el registro");
            PrivacyNotice.Text = Loc.T(
                "Everything else in this app stays offline. Text or photos leave your computer only when you press a translate button, and only to the service you pressed. Text and photos are two separate permissions: allowing one does not allow the other.",
                "Всё остальное в приложении остаётся оффлайн. Текст или фото покидают компьютер только по нажатию кнопки перевода и только в тот сервис, который вы нажали. Текст и фотографии — два отдельных разрешения: согласие на одно не даёт согласия на другое.",
                "Todo lo demás permanece sin conexión. El texto o las fotos salen solo al pulsar un botón de traducción. Texto y fotos son dos permisos independientes.");

            AboutHeader.Text = Loc.T("About", "О программе", "Acerca de");
            VersionText.Text = "PhotoMusicViewer — " + Loc.T("version ", "версия ", "versión ") + AppInfo.Version;
            GitHubButton.Content = Loc.T("Open project page on GitHub", "Открыть страницу проекта на GitHub", "Abrir la página del proyecto en GitHub");
            GitHubHint.Text = AppInfo.GitHubUrl + "\n" + Loc.T(
                "Opens in your default browser. Nothing is sent until you press the button.",
                "Откроется в браузере по умолчанию. Пока кнопка не нажата, никуда ничего не отправляется.",
                "Se abre en el navegador predeterminado. No se envía nada hasta pulsar el botón.");

            TestButton.Content = Loc.T("Test keys", "Проверить ключи", "Probar claves");
            OkButton.Content = Loc.T("OK", "ОК", "OK");
            CancelButton.Content = Loc.T("Cancel", "Отмена", "Cancelar");
        }

        // ------------------------------------------------------------- заполнение

        private void FillLanguages()
        {
            foreach (var lang in TranslationLanguages.All) TargetLangCombo.Items.Add(lang);

            SourceLangCombo.Items.Add(new LanguageInfo(
                "", Loc.T("Auto-detect", "Автоопределение", "Detección automática"), ""));
            foreach (var lang in TranslationLanguages.All) SourceLangCombo.Items.Add(lang);
        }

        /// <summary>Настройки интерфейса — копия; применяются только по OK.</summary>
        private void FillPreferences()
        {
            var prefs = AppPreferences.Current;
            StartupLanguageCombo.SelectedIndex = (int)prefs.Language;
            RememberLanguageCheck.IsChecked = prefs.RememberLanguage;
            PhotoSortCombo.SelectedIndex = (int)prefs.PhotoSort;
            PhotoSortDirectionCombo.SelectedIndex = prefs.PhotoSortDescending ? 1 : 0;
            MusicSortCombo.SelectedIndex = (int)prefs.MusicSort;
            MusicSortDirectionCombo.SelectedIndex = prefs.MusicSortDescending ? 1 : 0;
            RememberSortCheck.IsChecked = prefs.RememberSort;
            StartupVolumeSlider.Value = Math.Round(prefs.Volume * 100);
            RememberVolumeCheck.IsChecked = prefs.RememberVolume;
            ConfirmDeleteCheck.IsChecked = prefs.ConfirmDelete;
            UpdateStartupVolumeText();
        }

        private PreferenceValues BuildPreferencesFromUi()
        {
            var prefs = AppPreferences.Current;
            prefs.Language = (StartupLanguage)Math.Max(0, StartupLanguageCombo.SelectedIndex);
            prefs.RememberLanguage = RememberLanguageCheck.IsChecked == true;
            prefs.PhotoSort = (FileSortKey)Math.Max(0, PhotoSortCombo.SelectedIndex);
            prefs.PhotoSortDescending = PhotoSortDirectionCombo.SelectedIndex == 1;
            prefs.MusicSort = (FileSortKey)Math.Max(0, MusicSortCombo.SelectedIndex);
            prefs.MusicSortDescending = MusicSortDirectionCombo.SelectedIndex == 1;
            prefs.RememberSort = RememberSortCheck.IsChecked == true;
            prefs.Volume = StartupVolumeSlider.Value / 100.0;
            prefs.RememberVolume = RememberVolumeCheck.IsChecked == true;
            prefs.ConfirmDelete = ConfirmDeleteCheck.IsChecked == true;
            prefs.EnsureValid();
            return prefs;
        }

        private void StartupVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateStartupVolumeText();

        private void UpdateStartupVolumeText()
        {
            if (StartupVolumeText != null && StartupVolumeSlider != null)
                StartupVolumeText.Text = $"{(int)Math.Round(StartupVolumeSlider.Value)}%";
        }

        private static void SetComboItemText(ComboBox combo, int index, string text)
        {
            if (index < combo.Items.Count && combo.Items[index] is ComboBoxItem item) item.Content = text;
        }

        /// <summary>
        /// Сначала язык (его смена запоминается как «последний выбранный»), затем сами
        /// значения — чтобы выбор «Как в Windows» не перезаписался конкретным языком.
        /// </summary>
        private void ApplyPreferences()
        {
            var prefs = BuildPreferencesFromUi();
            var before = AppPreferences.Current;
            if (prefs.Language != before.Language)
                Loc.SetLanguage(Loc.ResolveStartup(prefs.Language));
            if (!AppPreferences.Save(prefs))
                AppLog.Warn("TranslationSettingsWindow.SavePreferences", null, "interface preferences were applied for this session only");
        }

        private void FillFromDraft()
        {
            FillPreferences();
            _loading = true;
            try
            {
                DeepLKeyMasked.Password = _draft.DeepLKey;
                DeepLKeyPlain.Text = _draft.DeepLKey;
                GoogleKeyMasked.Password = _draft.GoogleKey;
                GoogleKeyPlain.Text = _draft.GoogleKey;
                QwenKeyMasked.Password = _draft.QwenKey;
                QwenKeyPlain.Text = _draft.QwenKey;

                SelectLanguage(TargetLangCombo, _draft.TargetLanguage);
                SelectLanguage(SourceLangCombo, _draft.SourceLanguage);

                SelectByTag(GoogleEngineCombo, _draft.GoogleEngine.ToString());

                foreach (var model in new[]
                         {
                             "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite",
                             "gemini-2.0-flash", "gemini-1.5-flash", "gemini-1.5-pro"
                         })
                    GeminiModelCombo.Items.Add(model);
                GeminiModelCombo.Text = _draft.GeminiModel;

                TemperatureBox.Text = _draft.GeminiTemperature.ToString(CultureInfo.InvariantCulture);

                SelectByTag(QwenRegionCombo, _draft.QwenRegion.ToString());
                QwenBaseUrlBox.Text = _draft.QwenBaseUrl;
                QwenLoopbackHttpCheck.IsChecked = _draft.QwenAllowLoopbackHttp;

                foreach (var model in TranslationService.QwenTextModels) QwenModelCombo.Items.Add(model);
                QwenModelCombo.Text = _draft.QwenModel;

                foreach (var model in TranslationService.QwenVisionModels) QwenVisionModelCombo.Items.Add(model);
                QwenVisionModelCombo.Text = _draft.QwenVisionModel;

                QwenTemperatureBox.Text = _draft.QwenTemperature.ToString(CultureInfo.InvariantCulture);

                SelectByTag(DeepLEndpointCombo, _draft.DeepLEndpoint.ToString());
                SelectByTag(DeepLFormalityCombo, _draft.DeepLFormality);
                SelectByTag(DeepLModelTypeCombo, _draft.DeepLModelType);
                DeepLContextBox.Text = _draft.DeepLContext;
                PreserveFormattingCheck.IsChecked = _draft.DeepLPreserveFormatting;

                PromptList.ItemsSource = _prompts;
                if (_prompts.Count > 0)
                    PromptList.SelectedIndex = Math.Clamp(_draft.SelectedPromptIndex, 0, _prompts.Count - 1);

                ImageOcrPromptBox.Text = _draft.ImageOcrPrompt;
                ImageOcrTranslatePromptBox.Text = _draft.ImageOcrTranslatePrompt;

                TimeoutBox.Text = _draft.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
                foreach (var side in new[] { "800", "1200", "1600", "2048", "3072", "4096" })
                    MaxImageSideCombo.Items.Add(side);
                MaxImageSideCombo.Text = _draft.MaxImageSide.ToString(CultureInfo.InvariantCulture);

                StripMetaCheck.IsChecked = _draft.StripMetadataBeforeSend;
                AskNetworkCheck.IsChecked = _draft.AskBeforeNetwork;
                PersistCheck.IsChecked = _draft.PersistToDisk;
                LogCheck.IsChecked = AppLog.Enabled;
                CleanRecentCheck.IsChecked = TraceCleanupOptions.RecentItems;
                CleanRegistryCheck.IsChecked = TraceCleanupOptions.ExplorerRegistry;
                ThumbCacheCheck.IsChecked = ThumbnailDiskCache.Enabled;
            }
            finally { _loading = false; }

            UpdateEngineDependentControls();
            UpdatePersistInfo();
            UpdateLogInfo();
            UpdateThumbCacheInfo();
        }

        private void UpdateThumbCacheInfo()
        {
            long bytes = ThumbnailDiskCache.SizeOnDisk();
            string size = (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture);
            ThumbCacheText.Text = ThumbnailDiskCache.Enabled
                ? string.Format(Loc.T(
                    "Folder: {0}. Now {1} MB of {2} MB. File names are salted hashes, not paths; off by default because it leaves a trace of viewed photos.",
                    "Папка: {0}. Сейчас {1} МБ из {2} МБ. Имена файлов — хэши с солью, а не пути. По умолчанию выключено: это след просмотренных фото на диске.",
                    "Carpeta: {0}. Ahora {1} MB de {2} MB. Desactivado por defecto: deja rastro de las fotos vistas."),
                    ThumbnailDiskCache.CacheDirectory, size, ThumbnailDiskCache.MaxBytes / (1024 * 1024))
                : Loc.T("Off: thumbnails are kept only in memory while the app is open. Turning it off deletes the cache.",
                        "Выключено: миниатюры живут только в памяти, пока приложение открыто. Выключение удаляет кэш.",
                        "Desactivado: las miniaturas solo se guardan en memoria. Al desactivarlo se borra la caché.");
            ClearThumbCacheButton.IsEnabled = bytes > 0;
        }

        private void ThumbCacheCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            // Применяется сразу, как журнал; выключение удаляет все записи
            bool saved = ThumbnailDiskCache.SetEnabled(ThumbCacheCheck.IsChecked == true);
            UpdateThumbCacheInfo();
            SetStatus(!saved
                ? Loc.T("Could not save the setting.", "Не удалось сохранить настройку.", "No se pudo guardar el ajuste.")
                : ThumbnailDiskCache.Enabled
                    ? Loc.T("Thumbnail disk cache is on.", "Кэш миниатюр на диске включён.", "Caché de miniaturas activada.")
                    : Loc.T("Thumbnail disk cache is off and deleted.", "Кэш миниатюр на диске выключен и удалён.", "Caché de miniaturas desactivada y borrada."));
        }

        private void ClearThumbCache_Click(object sender, RoutedEventArgs e)
        {
            bool cleared = ThumbnailDiskCache.Clear();
            UpdateThumbCacheInfo();
            SetStatus(cleared
                ? Loc.T("Thumbnail cache deleted.", "Кэш миниатюр удалён.", "Caché de miniaturas borrada.")
                : Loc.T("Could not delete the thumbnail cache.", "Не удалось удалить кэш миниатюр.", "No se pudo borrar la caché."));
        }

        private static void SelectLanguage(ComboBox combo, string? code)
        {
            var wanted = (code ?? "").Trim();
            foreach (var item in combo.Items)
            {
                if (item is LanguageInfo info &&
                    string.Equals(info.Code, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            // Неизвестный код — добавляем в список, чтобы не потерять настройку
            if (wanted.Length > 0)
            {
                var custom = new LanguageInfo(wanted, wanted, wanted.ToLowerInvariant());
                combo.Items.Add(custom);
                combo.SelectedItem = custom;
            }
            else if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem cbi &&
                    string.Equals(cbi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static string TagOf(ComboBox combo, string fallback) =>
            combo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag ? tag : fallback;

        private void UpdateEngineDependentControls()
        {
            bool gemini = TagOf(GoogleEngineCombo, "Gemini") == "Gemini";

            GeminiModelCombo.IsEnabled = gemini;
            LoadModelsButton.IsEnabled = gemini;
            TemperatureBox.IsEnabled = gemini;

            // Промты нужны и Gemini, и Qwen, поэтому выбор движка Google их больше не блокирует
            PromptList.IsEnabled = true;
            PromptNameBox.IsEnabled = true;
            PromptTextBox.IsEnabled = true;
            AddPromptButton.IsEnabled = true;
            DeletePromptButton.IsEnabled = true;
            ResetPromptsButton.IsEnabled = true;
        }

        private void UpdatePersistInfo()
        {
            PersistPathText.Text = PersistCheck.IsChecked == true
                ? string.Format(
                    Loc.T("File: {0} — keys are encrypted for your Windows account, so the file is useless under another account or on another computer.",
                          "Файл: {0} — ключи зашифрованы под вашу учётную запись Windows: под другой учётной записью или на другом компьютере файл бесполезен.",
                          "Archivo: {0}: las claves se cifran para su cuenta de Windows, así que el archivo es inútil en otra cuenta u otro equipo."),
                    TranslationConfig.SettingsFilePath)
                : Loc.T("Keys stay in memory only and disappear when the app closes.",
                        "Ключи хранятся только в памяти и исчезают при закрытии приложения.",
                        "Las claves solo se guardan en memoria y desaparecen al cerrar la aplicación.");

            DeleteFileButton.IsEnabled = TranslationConfig.SettingsFileExists;
        }

        private void UpdateLogInfo()
        {
            LogPathText.Text = AppLog.Enabled
                ? string.Format(Loc.T("File: {0}", "Файл: {0}", "Archivo: {0}"), AppLog.FilePath)
                : Loc.T("Nothing is written to disk. Turn it on only while reproducing a problem.",
                        "На диск ничего не пишется. Включайте только на время воспроизведения проблемы.",
                        "No se escribe nada en el disco. Actívelo solo para reproducir un problema.");

            OpenLogFolderButton.IsEnabled = AppLog.HasLogs;
            DeleteLogButton.IsEnabled = AppLog.HasLogs;
        }

        // ------------------------------------------------------------- обработчики

        private void ShowKeysCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (DeepLKeyPlain == null || DeepLKeyMasked == null) return;

            bool show = ShowKeysCheck.IsChecked == true;
            if (show)
            {
                DeepLKeyPlain.Text = DeepLKeyMasked.Password;
                GoogleKeyPlain.Text = GoogleKeyMasked.Password;
                QwenKeyPlain.Text = QwenKeyMasked.Password;
            }
            else
            {
                DeepLKeyMasked.Password = DeepLKeyPlain.Text;
                GoogleKeyMasked.Password = GoogleKeyPlain.Text;
                QwenKeyMasked.Password = QwenKeyPlain.Text;
            }

            DeepLKeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            GoogleKeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            QwenKeyPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DeepLKeyMasked.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            GoogleKeyMasked.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            QwenKeyMasked.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        }

        private string CurrentDeepLKey() =>
            (ShowKeysCheck.IsChecked == true ? DeepLKeyPlain.Text : DeepLKeyMasked.Password).Trim();

        private string CurrentGoogleKey() =>
            (ShowKeysCheck.IsChecked == true ? GoogleKeyPlain.Text : GoogleKeyMasked.Password).Trim();

        private string CurrentQwenKey() =>
            (ShowKeysCheck.IsChecked == true ? QwenKeyPlain.Text : QwenKeyMasked.Password).Trim();

        private void GoogleEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || GeminiModelCombo == null) return;
            UpdateEngineDependentControls();
        }

        private void PersistCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || PersistPathText == null) return;
            UpdatePersistInfo();
        }

        private void PromptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CommitPromptEdits();

            _editing = PromptList.SelectedItem as PromptPreset;
            PromptNameBox.Text = _editing?.Name ?? "";
            PromptTextBox.Text = _editing?.Text ?? "";
        }

        private void PromptNameBox_LostFocus(object sender, RoutedEventArgs e) => CommitPromptEdits();

        /// <summary>Переносит правки из полей в выбранный промт (иначе они потеряются при смене выбора).</summary>
        private void CommitPromptEdits()
        {
            if (_editing == null) return;

            var name = PromptNameBox.Text.Trim();
            _editing.Name = name.Length > 0
                ? name
                : Loc.T("Prompt", "Промт", "Prompt");
            _editing.Text = PromptTextBox.Text;

            try { PromptList.Items.Refresh(); } catch (Exception ex) { AppLog.Debug("TranslationSettingsWindow.RefreshPrompts", ex); }
        }

        private void AddPrompt_Click(object sender, RoutedEventArgs e)
        {
            CommitPromptEdits();

            var preset = new PromptPreset
            {
                Name = Loc.T("New prompt", "Новый промт", "Nuevo prompt"),
                Text = "Translate the text below into {target}. Output only the translation.\n\n{text}"
            };

            _prompts.Add(preset);
            PromptList.SelectedItem = preset;
            PromptNameBox.Focus();
            PromptNameBox.SelectAll();
        }

        private void DeletePrompt_Click(object sender, RoutedEventArgs e)
        {
            if (PromptList.SelectedItem is not PromptPreset selected) return;

            if (_prompts.Count <= 1)
            {
                SetStatus(Loc.T("At least one prompt must remain.",
                                "Должен остаться хотя бы один промт.",
                                "Debe quedar al menos un prompt."));
                return;
            }

            int index = _prompts.IndexOf(selected);
            _editing = null;
            _prompts.Remove(selected);
            PromptList.SelectedIndex = Math.Clamp(index, 0, _prompts.Count - 1);
        }

        private void ResetPrompts_Click(object sender, RoutedEventArgs e)
        {
            _editing = null;
            _prompts.Clear();
            foreach (var preset in TranslationSettings.DefaultPrompts()) _prompts.Add(preset);

            ImageOcrPromptBox.Text = TranslationSettings.DefaultImageOcrPrompt;
            ImageOcrTranslatePromptBox.Text = TranslationSettings.DefaultImageOcrTranslatePrompt;

            PromptList.SelectedIndex = 0;
        }

        private string _networkDestination = "";
        private bool EnsureProbeConsent(Func<System.Collections.Generic.IEnumerable<string>> endpoints)
        {
            _networkDestination = "";
            try
            {
                var recipients = System.Linq.Enumerable.ToArray(endpoints());
                _networkDestination = string.Join(", ", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(recipients, NetworkEndpointPolicy.Origin)));
                bool allowed = NetworkConsentDialog.Ensure(this, NetworkDataKind.ServiceProbe, recipients);
                if (!allowed) SetStatus(NetworkConsentDialog.Declined);
                return allowed;
            }
            catch (TranslationException ex) { SetStatus(ex.Message); return false; }
        }

        private async void LoadModelsButton_Click(object sender, RoutedEventArgs e)
        {
            var probe = BuildSettingsFromUi();
            if (!TranslationService.HasGoogleKey(probe))
            {
                SetStatus(Loc.T("Enter the Google API key first.",
                                "Сначала введите ключ Google.",
                                "Introduzca primero la clave de Google."));
                return;
            }

            // Список моделей — тоже сетевой запрос, раньше он уходил вообще без спроса
            if (!EnsureProbeConsent(() => new[] { "https://generativelanguage.googleapis.com" }))
            {
                return;
            }

            LoadModelsButton.IsEnabled = false;
            SetStatus(Loc.T("Loading models…", "Загружаю список моделей…", "Cargando modelos…") + " → " + _networkDestination);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                var models = await TranslationService.ListGeminiModelsAsync(probe, _cts.Token);
                string current = GeminiModelCombo.Text;

                GeminiModelCombo.Items.Clear();
                foreach (var model in models) GeminiModelCombo.Items.Add(model);
                GeminiModelCombo.Text = current;

                SetStatus(string.Format(
                    Loc.T("{0} models available.", "Доступно моделей: {0}.", "{0} modelos disponibles."),
                    models.Count));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc.T("Cancelled.", "Отменено.", "Cancelado."));
            }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationSettingsWindow.ListModels", ex);
                SetStatus(ex.Message);
            }
            finally
            {
                LoadModelsButton.IsEnabled = TagOf(GoogleEngineCombo, "Gemini") == "Gemini";
            }
        }

        private async void LoadQwenModelsButton_Click(object sender, RoutedEventArgs e)
        {
            var probe = BuildSettingsFromUi();
            if (!TranslationService.HasQwenKey(probe))
            {
                SetStatus(Loc.T("Enter the Qwen API key first.",
                                "Сначала введите ключ Qwen.",
                                "Introduzca primero la clave de Qwen."));
                return;
            }

            // Список моделей — тоже сетевой запрос, как и у Gemini
            if (!EnsureProbeConsent(() => new[] { TranslationService.GetEndpoint(TranslationProvider.Qwen, NetworkDataKind.ServiceProbe, probe) }))
            {
                return;
            }

            LoadQwenModelsButton.IsEnabled = false;
            SetStatus(Loc.T("Loading models…", "Загружаю список моделей…", "Cargando modelos…") + " → " + _networkDestination);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                var models = await TranslationService.ListQwenModelsAsync(probe, _cts.Token);

                string currentText = QwenModelCombo.Text;
                string currentVision = QwenVisionModelCombo.Text;

                QwenModelCombo.Items.Clear();
                QwenVisionModelCombo.Items.Clear();
                foreach (var model in models)
                {
                    QwenModelCombo.Items.Add(model);
                    QwenVisionModelCombo.Items.Add(model);
                }

                QwenModelCombo.Text = currentText;
                QwenVisionModelCombo.Text = currentVision;

                SetStatus(string.Format(
                    Loc.T("{0} models available.", "Доступно моделей: {0}.", "{0} modelos disponibles."),
                    models.Count));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc.T("Cancelled.", "Отменено.", "Cancelado."));
            }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationSettingsWindow.ListQwenModels", ex);
                SetStatus(ex.Message);
            }
            finally
            {
                LoadQwenModelsButton.IsEnabled = true;
            }
        }

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            var probe = BuildSettingsFromUi();

            // Проверка ключа — сетевой запрос, хоть ни текст, ни фото в нём не участвуют
            if (!EnsureProbeConsent(() => TranslationService.GetProbeEndpoints(probe)))
            {
                return;
            }

            TestButton.IsEnabled = false;
            SetStatus(Loc.T("Checking…", "Проверяю…", "Comprobando…") + " → " + _networkDestination);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var lines = new System.Collections.Generic.List<string>();

            if (TranslationService.HasDeepLKey(probe))
            {
                try { lines.Add(await TranslationService.CheckDeepLAsync(probe, _cts.Token)); }
                catch (OperationCanceledException) { lines.Add("DeepL: " + Loc.T("cancelled", "отменено", "cancelado")); }
                catch (Exception ex) { AppLog.Warn("TranslationSettingsWindow.CheckKeys", ex); lines.Add(ex.Message); }
            }
            else
            {
                lines.Add("DeepL: " + Loc.T("no key", "ключ не указан", "sin clave"));
            }

            if (TranslationService.HasGoogleKey(probe))
            {
                try { lines.Add(await TranslationService.CheckGoogleAsync(probe, _cts.Token)); }
                catch (OperationCanceledException) { lines.Add("Google: " + Loc.T("cancelled", "отменено", "cancelado")); }
                catch (Exception ex) { AppLog.Warn("TranslationSettingsWindow.CheckKeys", ex); lines.Add(ex.Message); }
            }
            else
            {
                lines.Add("Google: " + Loc.T("no key", "ключ не указан", "sin clave"));
            }

            if (TranslationService.HasQwenKey(probe))
            {
                try { lines.Add(await TranslationService.CheckQwenAsync(probe, _cts.Token)); }
                catch (OperationCanceledException) { lines.Add("Qwen: " + Loc.T("cancelled", "отменено", "cancelado")); }
                catch (Exception ex) { AppLog.Warn("TranslationSettingsWindow.CheckKeys", ex); lines.Add(ex.Message); }
            }
            else
            {
                lines.Add("Qwen: " + Loc.T("no key", "ключ не указан", "sin clave"));
            }

            SetStatus(string.Join("    |    ", lines));
            TestButton.IsEnabled = true;
        }

        private void DeleteFile_Click(object sender, RoutedEventArgs e)
        {
            try { TranslationConfig.DeleteFile(); }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationSettingsWindow.DeleteSettings", ex);
                PersistCheck.IsChecked = false; _draft.PersistToDisk = false;
                UpdatePersistInfo(); SetStatus(ex.Message); return;
            }
            PersistCheck.IsChecked = false;
            _draft.PersistToDisk = false;
            UpdatePersistInfo();
            SetStatus(Loc.T("Settings and their backup files deleted.", "Настройки и их резервные файлы удалены.", "Archivo de ajustes eliminado."));
        }

        private void LogCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || LogPathText == null) return;

            // Журнал не часть настроек перевода: применяется сразу, а не по OK,
            // и помнится файлом-меткой, чтобы пережить перезапуск
            AppLog.SetEnabled(LogCheck.IsChecked == true);
            UpdateLogInfo();
            SetStatus(AppLog.Enabled
                ? Loc.T("Diagnostic log is on.", "Журнал диагностики включён.", "Registro de diagnóstico activado.")
                : Loc.T("Diagnostic log is off.", "Журнал диагностики выключен.", "Registro de diagnóstico desactivado."));
        }

        private void TraceCleanupCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            // Как и журнал, это не часть настроек перевода: применяется сразу, не по OK,
            // и помнится файлом-меткой, чтобы работать уже при запуске двойным кликом.
            bool on = ((System.Windows.Controls.CheckBox)sender).IsChecked == true;
            bool saved = ReferenceEquals(sender, CleanRegistryCheck)
                ? TraceCleanupOptions.SetExplorerRegistry(on)
                : TraceCleanupOptions.SetRecentItems(on);

            string what = ReferenceEquals(sender, CleanRegistryCheck)
                ? Loc.T("Explorer registry history cleanup", "Очистка истории Проводника в реестре", "Limpieza del registro del Explorador")
                : Loc.T("Recent items cleanup", "Очистка «Недавних»", "Limpieza de Recientes");
            string state = on ? Loc.T("on", "включена", "activada") : Loc.T("off", "выключена", "desactivada");
            SetStatus(saved
                ? what + ": " + state + "."
                : what + ": " + state + " " + Loc.T("until the app is closed (could not save the setting).",
                    "до закрытия приложения (не удалось сохранить настройку).",
                    "hasta cerrar la aplicación (no se pudo guardar)."));
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppLog.DirectoryPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationSettingsWindow.OpenLogFolder", ex);
                SetStatus(Loc.T("Could not open the folder.", "Не удалось открыть папку.", "No se pudo abrir la carpeta."));
            }
        }

        private void DeleteLog_Click(object sender, RoutedEventArgs e)
        {
            var deleted = AppLog.DeleteLogs();
            UpdateLogInfo();
            SetStatus(deleted
                ? Loc.T("Log deleted.", "Журнал удалён.", "Registro eliminado.")
                : Loc.T("Could not delete the log file.", "Не удалось удалить файл журнала.", "No se pudo eliminar el archivo del registro."));
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            CommitPromptEdits();

            if (!int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ||
                timeout < 5 || timeout > 600)
            {
                SetStatus(Loc.T("Timeout must be a number between 5 and 600.",
                                "Таймаут — число от 5 до 600.",
                                "El tiempo de espera debe estar entre 5 y 600."));
                TimeoutBox.Focus();
                return;
            }

            if (!double.TryParse(TemperatureBox.Text.Trim().Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ||
                temperature < 0 || temperature > 2)
            {
                SetStatus(Loc.T("Temperature must be a number between 0 and 2.",
                                "Температура — число от 0 до 2.",
                                "La temperatura debe estar entre 0 y 2."));
                TemperatureBox.Focus();
                return;
            }

            if (!double.TryParse(QwenTemperatureBox.Text.Trim().Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var qwenTemperature) ||
                qwenTemperature < 0 || qwenTemperature > 2)
            {
                SetStatus(Loc.T("Qwen temperature must be a number between 0 and 2.",
                                "Температура Qwen — число от 0 до 2.",
                                "La temperatura de Qwen debe estar entre 0 y 2."));
                QwenTemperatureBox.Focus();
                return;
            }

            var settings = BuildSettingsFromUi();
            settings.TimeoutSeconds = timeout;
            settings.GeminiTemperature = temperature;
            settings.QwenTemperature = qwenTemperature;
            settings.EnsureValid();
            if (settings.QwenRegion == QwenRegionMode.Custom)
            {
                try { _ = TranslationService.GetEndpoint(TranslationProvider.Qwen, NetworkDataKind.Text, settings); }
                catch (TranslationException ex) { SetStatus(ex.Message); QwenBaseUrlBox.Focus(); return; }
            }
            TranslationConfig.Current = settings;

            try
            {
                TranslationConfig.SaveOrDelete();
            }
            catch (Exception ex)
            {
                AppLog.Error("TranslationConfig.SaveOrDelete", ex);
                MessageBox.Show(this,
                    Loc.T("Settings are applied, but the file could not be written. They will be kept in memory only.",
                          "Настройки применены, но файл записать не удалось. Они останутся только в памяти.",
                          "Los ajustes se aplicaron, pero no se pudo escribir el archivo."),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ApplyPreferences();
            DialogResult = true;
        }

        /// <summary>Собирает настройки из полей формы (используется и для проверки ключей до OK).</summary>
        private TranslationSettings BuildSettingsFromUi()
        {
            CommitPromptEdits();

            var settings = _draft.Clone();

            settings.DeepLKey = CurrentDeepLKey();
            settings.GoogleKey = CurrentGoogleKey();
            settings.QwenKey = CurrentQwenKey();

            settings.TargetLanguage = (TargetLangCombo.SelectedItem as LanguageInfo)?.Code ?? settings.TargetLanguage;
            settings.SourceLanguage = (SourceLangCombo.SelectedItem as LanguageInfo)?.Code ?? "";

            settings.GoogleEngine = TagOf(GoogleEngineCombo, "Gemini") == "CloudTranslationV2"
                ? GoogleTextEngine.CloudTranslationV2
                : GoogleTextEngine.Gemini;

            var model = (GeminiModelCombo.Text ?? "").Trim();
            if (model.Length > 0) settings.GeminiModel = model;

            settings.QwenRegion = TagOf(QwenRegionCombo, "International") switch
            {
                "Beijing" => QwenRegionMode.Beijing,
                "UsVirginia" => QwenRegionMode.UsVirginia,
                "CodingPlan" => QwenRegionMode.CodingPlan,
                "Custom" => QwenRegionMode.Custom,
                _ => QwenRegionMode.International
            };

            settings.QwenBaseUrl = QwenBaseUrlBox.Text.Trim();
            settings.QwenAllowLoopbackHttp = QwenLoopbackHttpCheck.IsChecked == true;

            var qwenModel = (QwenModelCombo.Text ?? "").Trim();
            if (qwenModel.Length > 0) settings.QwenModel = qwenModel;

            var qwenVisionModel = (QwenVisionModelCombo.Text ?? "").Trim();
            if (qwenVisionModel.Length > 0) settings.QwenVisionModel = qwenVisionModel;

            if (double.TryParse(QwenTemperatureBox.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var qwenTemp))
                settings.QwenTemperature = qwenTemp;

            settings.DeepLEndpoint = TagOf(DeepLEndpointCombo, "Auto") switch
            {
                "Free" => DeepLEndpointMode.Free,
                "Pro" => DeepLEndpointMode.Pro,
                _ => DeepLEndpointMode.Auto
            };
            settings.DeepLFormality = TagOf(DeepLFormalityCombo, "default");
            settings.DeepLModelType = TagOf(DeepLModelTypeCombo, "prefer_quality_optimized");
            settings.DeepLContext = DeepLContextBox.Text.Trim();
            settings.DeepLPreserveFormatting = PreserveFormattingCheck.IsChecked == true;

            settings.Prompts = new System.Collections.Generic.List<PromptPreset>();
            foreach (var preset in _prompts)
                settings.Prompts.Add(new PromptPreset { Name = preset.Name, Text = preset.Text });
            settings.SelectedPromptIndex = Math.Max(0, PromptList.SelectedIndex);

            settings.ImageOcrPrompt = ImageOcrPromptBox.Text.Trim();
            settings.ImageOcrTranslatePrompt = ImageOcrTranslatePromptBox.Text.Trim();

            if (int.TryParse(TimeoutBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout))
                settings.TimeoutSeconds = timeout;

            if (int.TryParse((MaxImageSideCombo.Text ?? "").Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var side))
                settings.MaxImageSide = side;

            if (double.TryParse(TemperatureBox.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var temperature))
                settings.GeminiTemperature = temperature;

            settings.StripMetadataBeforeSend = StripMetaCheck.IsChecked == true;
            settings.AskBeforeNetwork = AskNetworkCheck.IsChecked == true;
            settings.PersistToDisk = PersistCheck.IsChecked == true;

            settings.EnsureValid();
            return settings;
        }

        private void SetStatus(string text) => StatusText.Text = text;
    }
}
