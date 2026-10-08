using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    /// <summary>
    /// Окно распознанного текста + перевод через DeepL или Google.
    /// Левая панель редактируемая: ошибки OCR можно поправить до перевода,
    /// а можно вообще отправить в Google само фото, если локальный OCR не справился.
    /// </summary>
    public partial class OcrResultWindow : Window
    {
        private readonly string? _imagePath;
        private CancellationTokenSource? _cts;
        private bool _busy;
        private bool _loading;

        public OcrResultWindow(IReadOnlyList<OcrService.OcrVariant> variants, string? imagePath = null)
        {
            InitializeComponent();

            _imagePath = imagePath;

            ApplyLocalization();
            FillSourceText(variants);
            BuildCopyButtons(variants);
            RefreshTranslationControls();
            UpdateButtons();

            Closed += (_, _) => { try { _cts?.Cancel(); } catch (Exception ex) { AppLog.Debug("OcrResultWindow.Closed", ex); } };

            Loaded += (_, _) => SourceTextBox.Focus();
        }

        // ------------------------------------------------------------- инициализация

        private void ApplyLocalization()
        {
            Title = Loc.T("Scanned text and translation",
                          "Распознанный текст и перевод",
                          "Texto reconocido y traducción");

            TargetLangLabel.Text = Loc.T("Into:", "На:", "A:");
            PromptLabel.Text = Loc.T("Prompt:", "Промт:", "Prompt:");

            SourceHeader.Text = Loc.T("Recognized text (editable)",
                                      "Распознанный текст (можно править)",
                                      "Texto reconocido (editable)");
            ResultHeader.Text = Loc.T("Translation", "Перевод", "Traducción");

            DeepLButton.Content = Loc.T("DeepL", "DeepL", "DeepL");
            GoogleTextButton.Content = Loc.T("Google", "Google", "Google");
            QwenTextButton.Content = Loc.T("Qwen", "Qwen", "Qwen");
            GooglePhotoTextButton.Content = Loc.T("Google: photo → text",
                                                  "Google: фото → текст",
                                                  "Google: foto → texto");
            GooglePhotoTranslateButton.Content = Loc.T("Google: photo → text + translation",
                                                       "Google: фото → текст + перевод",
                                                       "Google: foto → texto + traducción");
            QwenPhotoTextButton.Content = Loc.T("Qwen: photo → text",
                                                "Qwen: фото → текст",
                                                "Qwen: foto → texto");
            QwenPhotoTranslateButton.Content = Loc.T("Qwen: photo → text + translation",
                                                     "Qwen: фото → текст + перевод",
                                                     "Qwen: foto → texto + traducción");
            CancelRequestButton.Content = Loc.T("Stop", "Остановить", "Detener");
            SettingsButton.Content = Loc.T("Settings…", "Настройки…", "Ajustes…");

            CopyResultButton.Content = Loc.T("Copy translation", "Копировать перевод", "Copiar traducción");
            UseResultAsSourceButton.Content = Loc.T("Translation → source",
                                                    "Перевод → в исходный",
                                                    "Traducción → origen");
            CloseButton.Content = Loc.T("Close", "Закрыть", "Cerrar");

            GooglePhotoTextButton.ToolTip = Loc.T(
                "Sends the photo itself to Google and reads the text from it — useful when local OCR fails.",
                "Отправляет само фото в Google и считывает текст с него — полезно, когда локальный OCR не справился.",
                "Envía la foto a Google y lee el texto — útil cuando el OCR local falla.");
            GooglePhotoTranslateButton.ToolTip = Loc.T(
                "Sends the photo to Google, reads the text and translates it in one request.",
                "Отправляет фото в Google, считывает текст и сразу переводит его одним запросом.",
                "Envía la foto a Google, lee el texto y lo traduce en una sola solicitud.");
            QwenTextButton.ToolTip = Loc.T(
                "Translates the text on the left with Qwen, using the selected prompt.",
                "Переводит текст слева через Qwen выбранным промтом.",
                "Traduce el texto de la izquierda con Qwen usando el prompt seleccionado.");
            QwenPhotoTextButton.ToolTip = Loc.T(
                "Sends the photo itself to Qwen (VL model) and reads the text from it.",
                "Отправляет само фото в Qwen (модель VL) и считывает текст с него.",
                "Envía la foto a Qwen (modelo VL) y lee el texto.");
            QwenPhotoTranslateButton.ToolTip = Loc.T(
                "Sends the photo to Qwen, reads the text and translates it in one request.",
                "Отправляет фото в Qwen, считывает текст и сразу переводит его одним запросом.",
                "Envía la foto a Qwen, lee el texto y lo traduce en una sola solicitud.");
        }

        private void FillSourceText(IReadOnlyList<OcrService.OcrVariant> variants)
        {
            _loading = true;
            try
            {
                if (variants.Count == 0)
                {
                    SourceTextBox.Text = "";
                    SetStatus(Loc.T(
                        "Local OCR found no text. You can send the photo to Google or Qwen instead.",
                        "Локальный OCR не нашёл текст. Можно отправить фото в Google или Qwen.",
                        "El OCR local no encontró texto. Puede enviar la foto a Google o Qwen."));
                }
                else if (variants.Count == 1)
                {
                    SourceTextBox.Text = variants[0].Text;
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var variant in variants)
                    {
                        sb.AppendLine($"====== {variant.Language} ======");
                        sb.AppendLine(variant.Text);
                        sb.AppendLine();
                    }
                    SourceTextBox.Text = sb.ToString().Trim();
                }
            }
            finally { _loading = false; }
        }

        private void BuildCopyButtons(IReadOnlyList<OcrService.OcrVariant> variants)
        {
            // Один вариант (или ни одного) — одна кнопка на текущее содержимое левой панели,
            // так скопируется и правленный текст, и текст, пришедший от Google по фото.
            if (variants.Count <= 1)
            {
                AddCopyButton(
                    Loc.T("Copy text", "Копировать текст", "Copiar texto"),
                    () => SourceTextBox.Text);
                return;
            }

            foreach (var variant in variants)
            {
                var text = variant.Text;
                AddCopyButton(Loc.T("Copy", "Копировать", "Copiar") + " " + variant.Code, () => text);
            }

            AddCopyButton(Loc.T("Copy all", "Копировать всё", "Copiar todo"), () => SourceTextBox.Text);
        }

        private void AddCopyButton(string label, Func<string> textProvider)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 120,
                Margin = new Thickness(0, 0, 8, 0)
            };

            button.Click += (_, _) => CopyWithFeedback(button, label, textProvider());
            CopyButtonsPanel.Children.Add(button);
        }

        // ------------------------------------------------------------- перевод

        /// <summary>Перезаполняет списки языков и промтов из текущих настроек.</summary>
        private void RefreshTranslationControls()
        {
            var settings = TranslationConfig.Current;

            _loading = true;
            try
            {
                TargetLangCombo.Items.Clear();
                object? selected = null;

                foreach (var lang in TranslationLanguages.All)
                {
                    TargetLangCombo.Items.Add(lang);
                    if (string.Equals(lang.Code, settings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
                        selected = lang;
                }

                if (selected == null)
                {
                    var custom = new LanguageInfo(settings.TargetLanguage, settings.TargetLanguage,
                        settings.TargetLanguage.ToLowerInvariant());
                    TargetLangCombo.Items.Add(custom);
                    selected = custom;
                }

                TargetLangCombo.SelectedItem = selected;

                PromptCombo.Items.Clear();
                foreach (var preset in settings.Prompts) PromptCombo.Items.Add(preset);
                if (PromptCombo.Items.Count > 0)
                    PromptCombo.SelectedIndex = Math.Clamp(settings.SelectedPromptIndex, 0, PromptCombo.Items.Count - 1);

                // Промты применяются и к Gemini, и к Qwen; их игнорирует только Cloud Translation v2
                bool promptsUsed = settings.GoogleEngine == GoogleTextEngine.Gemini ||
                                   TranslationService.HasQwenKey(settings);
                PromptCombo.IsEnabled = promptsUsed;
                PromptCombo.ToolTip = promptsUsed
                    ? null
                    : Loc.T("Cloud Translation v2 ignores prompts. Switch to Gemini or add a Qwen key.",
                            "Cloud Translation v2 не использует промты. Переключитесь на Gemini или добавьте ключ Qwen.",
                            "Cloud Translation v2 ignora los prompts. Cambie a Gemini o añada una clave de Qwen.");
            }
            finally { _loading = false; }
        }

        private void TargetLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (TargetLangCombo.SelectedItem is LanguageInfo lang)
                TranslationConfig.Current.TargetLanguage = lang.Code;
        }

        private void PromptCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (PromptCombo.SelectedIndex >= 0)
                TranslationConfig.Current.SelectedPromptIndex = PromptCombo.SelectedIndex;
        }

        private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            UpdateButtons();
        }

        private void DeepLButton_Click(object sender, RoutedEventArgs e)
        {
            string text = TextToTranslate();
            if (text.Length == 0) return;

            _ = RunAsync(NetworkDataKind.Text, TranslationProvider.DeepL, (settings, ct) => TranslationService.TranslateWithDeepLAsync(text, settings, ct));
        }

        private void GoogleTextButton_Click(object sender, RoutedEventArgs e)
        {
            string text = TextToTranslate();
            if (text.Length == 0) return;

            var prompt = (PromptCombo.SelectedItem as PromptPreset)?.Text;
            _ = RunAsync(NetworkDataKind.Text, TranslationProvider.Google, (settings, ct) => TranslationService.TranslateWithGoogleAsync(
                text, settings, prompt, ct));
        }

        private void GooglePhotoTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_imagePath == null) return;
            _ = RunAsync(NetworkDataKind.Image, TranslationProvider.Google, (settings, ct) => TranslationService.RecognizeImageWithGoogleAsync(
                _imagePath, settings, false, ct));
        }

        private void GooglePhotoTranslateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_imagePath == null) return;
            _ = RunAsync(NetworkDataKind.Image, TranslationProvider.Google, (settings, ct) => TranslationService.RecognizeImageWithGoogleAsync(
                _imagePath, settings, true, ct));
        }

        private void QwenTextButton_Click(object sender, RoutedEventArgs e)
        {
            string text = TextToTranslate();
            if (text.Length == 0) return;

            var prompt = (PromptCombo.SelectedItem as PromptPreset)?.Text;
            _ = RunAsync(NetworkDataKind.Text, TranslationProvider.Qwen, (settings, ct) => TranslationService.TranslateWithQwenAsync(
                text, settings, prompt, ct));
        }

        private void QwenPhotoTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_imagePath == null) return;
            _ = RunAsync(NetworkDataKind.Image, TranslationProvider.Qwen, (settings, ct) => TranslationService.RecognizeImageWithQwenAsync(
                _imagePath, settings, false, ct));
        }

        private void QwenPhotoTranslateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_imagePath == null) return;
            _ = RunAsync(NetworkDataKind.Image, TranslationProvider.Qwen, (settings, ct) => TranslationService.RecognizeImageWithQwenAsync(
                _imagePath, settings, true, ct));
        }

        /// <summary>Если часть текста выделена — переводим только её, иначе всю панель.</summary>
        private string TextToTranslate()
        {
            string text = SourceTextBox.SelectionLength > 0
                ? SourceTextBox.SelectedText
                : SourceTextBox.Text;

            text = text.Trim();
            if (text.Length == 0)
                SetStatus(Loc.T("There is no text to translate.",
                                "Нет текста для перевода.",
                                "No hay texto para traducir."));
            return text;
        }

        private async Task RunAsync(NetworkDataKind kind, TranslationProvider provider, Func<TranslationSettings, CancellationToken, Task<TranslationResult>> action)
        {
            if (_busy) return;
            var settings = TranslationConfig.Current.Clone();
            string endpoint;
            try
            {
                endpoint = TranslationService.GetEndpoint(provider, kind, settings);
                if (!EnsureNetworkConsent(kind, endpoint)) return;
            }
            catch (TranslationException ex) { SetStatus(ex.Message); return; }

            SetBusy(true);
            SetStatus(Loc.T("Sending the request…", "Отправляю запрос…", "Enviando la solicitud…") + " → " + NetworkEndpointPolicy.Origin(endpoint));

            _cts = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();

            try
            {
                var result = await action(settings, _cts.Token);
                watch.Stop();

                if (!string.IsNullOrWhiteSpace(result.SourceText))
                {
                    _loading = true;
                    try { SourceTextBox.Text = result.SourceText; }
                    finally { _loading = false; }
                }

                if (!string.IsNullOrEmpty(result.Translation))
                    ResultTextBox.Text = result.Translation;

                var parts = new List<string>
                {
                    result.Service,
                    string.Format(Loc.T("{0:0.0} s", "{0:0.0} с", "{0:0.0} s"), watch.Elapsed.TotalSeconds)
                };

                if (!string.IsNullOrEmpty(result.DetectedSourceLanguage))
                    parts.Add(Loc.T("detected", "определён язык", "idioma detectado") + ": " + result.DetectedSourceLanguage);

                if (!string.IsNullOrEmpty(result.Note))
                    parts.Add(result.Note);

                SetStatus(string.Join("    |    ", parts));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc.T("Request cancelled.", "Запрос отменён.", "Solicitud cancelada."));
            }
            catch (TranslationException ex)
            {
                AppLog.Warn("OcrResultWindow.Translate", ex, hideMessage: true);
                SetStatus(ex.Message);
            }
            catch (Exception ex)
            {
                AppLog.Error("OcrResultWindow.Translate", ex);
                SetStatus(Loc.T("Unexpected error: ", "Неожиданная ошибка: ", "Error inesperado: ") + ex.Message);
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        /// <summary>
        /// Подтверждение перед отправкой. Согласия раздельные: «да» на текст не разрешает
        /// отправку самой фотографии — в кадр попадает куда больше, чем в распознанный текст.
        /// </summary>
        private bool EnsureNetworkConsent(NetworkDataKind kind, string endpoint)
        {
            if (NetworkConsentDialog.Ensure(this, kind, new[] { endpoint })) return true;

            SetStatus(NetworkConsentDialog.Declined);
            return false;
        }

        private void CancelRequest_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch (Exception ex) { AppLog.Debug("OcrResultWindow.CancelRequest", ex); }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new TranslationSettingsWindow { Owner = this };
            if (window.ShowDialog() == true)
            {
                RefreshTranslationControls();
                UpdateButtons();
            }
        }

        // ------------------------------------------------------------- буфер обмена

        private void CopyResult_Click(object sender, RoutedEventArgs e) =>
            CopyWithFeedback(CopyResultButton,
                Loc.T("Copy translation", "Копировать перевод", "Copiar traducción"),
                ResultTextBox.Text);

        private void UseResultAsSource_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ResultTextBox.Text)) return;

            SourceTextBox.Text = ResultTextBox.Text;
            ResultTextBox.Text = "";
            UpdateButtons();
        }

        private static void CopyWithFeedback(Button button, string originalLabel, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Флаги приватности: текст не уйдёт в историю буфера и в облако
            if (!PrivacyClipboard.TrySetText(text)) return;

            button.Content = Loc.T("Copied!", "Скопировано!", "¡Copiado!");

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                button.Content = originalLabel;
            };
            timer.Start();
        }

        // ------------------------------------------------------------- состояние UI

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CancelRequestButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SettingsButton.IsEnabled = !busy;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool hasText = !string.IsNullOrWhiteSpace(SourceTextBox.Text);
            bool hasImage = _imagePath != null;

            DeepLButton.IsEnabled = !_busy && hasText;
            GoogleTextButton.IsEnabled = !_busy && hasText;
            QwenTextButton.IsEnabled = !_busy && hasText;
            GooglePhotoTextButton.IsEnabled = !_busy && hasImage;
            GooglePhotoTranslateButton.IsEnabled = !_busy && hasImage;
            QwenPhotoTextButton.IsEnabled = !_busy && hasImage;
            QwenPhotoTranslateButton.IsEnabled = !_busy && hasImage;
            CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(ResultTextBox.Text);
            UseResultAsSourceButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(ResultTextBox.Text);
        }

        private void SetStatus(string text) => StatusText.Text = text;
    }
}
