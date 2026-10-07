using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>Ошибка перевода с уже понятным пользователю текстом (без стека и без ключей).</summary>
    public sealed class TranslationException : Exception
    {
        public TranslationException(string message) : base(message) { }
    }

    /// <summary>
    /// Результат запроса. SourceText заполняется только когда текст получен из картинки
    /// (режимы «фото → текст» и «фото → текст + перевод»).
    /// </summary>
    public sealed record TranslationResult(
        string Service,
        string? SourceText,
        string Translation,
        string? DetectedSourceLanguage,
        string? Note);

    /// <summary>
    /// Переводчики DeepL и Google. Сетевые запросы делаются ТОЛЬКО из этих методов
    /// и только по явному нажатию кнопки пользователем.
    /// </summary>
    public static class TranslationService
    {
        // Один общий HttpClient на процесс. Реальный таймаут задаётся через CancellationToken,
        // поэтому собственный таймаут клиента выставлен с большим запасом.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

        // Пробуем v1beta, затем v1: на разных ключах и моделях доступны разные версии API
        private static readonly string[] GeminiHosts =
        {
            "https://generativelanguage.googleapis.com/v1beta",
            "https://generativelanguage.googleapis.com/v1"
        };
        private const string CloudTranslateUrl = "https://translation.googleapis.com/language/translate/v2";

        public static bool HasDeepLKey(TranslationSettings s) => !string.IsNullOrWhiteSpace(s.DeepLKey);
        public static bool HasGoogleKey(TranslationSettings s) => !string.IsNullOrWhiteSpace(s.GoogleKey);
        public static bool HasQwenKey(TranslationSettings s) => !string.IsNullOrWhiteSpace(s.QwenKey);

        // ------------------------------------------------------------------ DeepL

        public static async Task<TranslationResult> TranslateWithDeepLAsync(
            string text, TranslationSettings settings, CancellationToken ct)
        {
            RequireText(text);
            if (!HasDeepLKey(settings))
                throw new TranslationException(Loc.T(
                    "No DeepL API key. Add it in Translation settings.",
                    "Не указан ключ DeepL. Добавьте его в настройках перевода.",
                    "Falta la clave de DeepL. Añádela en los ajustes de traducción."));

            var fields = new List<KeyValuePair<string, string>>
            {
                new("text", text),
                new("target_lang", TranslationLanguages.DeepLTarget(settings.TargetLanguage))
            };

            if (!string.IsNullOrWhiteSpace(settings.SourceLanguage))
                fields.Add(new("source_lang", TranslationLanguages.DeepLSource(settings.SourceLanguage)));

            if (!string.IsNullOrWhiteSpace(settings.DeepLFormality) &&
                settings.DeepLFormality != "default")
                fields.Add(new("formality", settings.DeepLFormality));

            if (!string.IsNullOrWhiteSpace(settings.DeepLModelType))
                fields.Add(new("model_type", settings.DeepLModelType));

            if (!string.IsNullOrWhiteSpace(settings.DeepLContext))
                fields.Add(new("context", settings.DeepLContext));

            if (settings.DeepLPreserveFormatting)
                fields.Add(new("preserve_formatting", "1"));

            using var request = new HttpRequestMessage(HttpMethod.Post, DeepLBase(settings) + "/v2/translate");
            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + settings.DeepLKey.Trim());
            request.Content = new FormUrlEncodedContent(fields);

            string body = await SendAsync(request, "DeepL", settings, ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var translations = doc.RootElement.GetProperty("translations");
                if (translations.GetArrayLength() == 0)
                    throw new TranslationException(EmptyAnswer("DeepL"));

                var first = translations[0];
                string translated = first.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                string? detected = first.TryGetProperty("detected_source_language", out var d)
                    ? d.GetString()
                    : null;

                return new TranslationResult("DeepL", null, translated, detected, null);
            }
            catch (TranslationException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationService.DeepL разбор ответа", ex, hideMessage: true);
                throw new TranslationException(BadAnswer("DeepL"));
            }
        }

        /// <summary>Проверка ключа DeepL: показывает израсходованный объём символов.</summary>
        public static async Task<string> CheckDeepLAsync(TranslationSettings settings, CancellationToken ct)
        {
            if (!HasDeepLKey(settings))
                throw new TranslationException(Loc.T("No DeepL API key.", "Не указан ключ DeepL.", "Falta la clave de DeepL."));

            using var request = new HttpRequestMessage(HttpMethod.Get, DeepLBase(settings) + "/v2/usage");
            request.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + settings.DeepLKey.Trim());

            string body = await SendAsync(request, "DeepL", settings, ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                long used = doc.RootElement.TryGetProperty("character_count", out var c) ? c.GetInt64() : 0;
                long limit = doc.RootElement.TryGetProperty("character_limit", out var l) ? l.GetInt64() : 0;
                return $"DeepL OK — {used:N0} / {limit:N0} " +
                       Loc.T("characters used", "символов израсходовано", "caracteres usados");
            }
            catch (Exception ex)
            {
                AppLog.Debug("TranslationService.CheckDeepL разбор ответа", ex, hideMessage: true);
                return "DeepL OK";
            }
        }

        private static string DeepLBase(TranslationSettings settings)
        {
            bool free = settings.DeepLEndpoint switch
            {
                DeepLEndpointMode.Free => true,
                DeepLEndpointMode.Pro => false,
                _ => settings.DeepLKey.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            };

            return free ? "https://api-free.deepl.com" : "https://api.deepl.com";
        }

        // ----------------------------------------------------------------- Google

        /// <summary>
        /// Перевод текста через Google: Gemini (с моделью и промтом) либо Cloud Translation v2.
        /// </summary>
        public static async Task<TranslationResult> TranslateWithGoogleAsync(
            string text, TranslationSettings settings, string? promptTemplate, CancellationToken ct)
        {
            RequireText(text);
            RequireGoogleKey(settings);

            if (settings.GoogleEngine == GoogleTextEngine.CloudTranslationV2)
                return await CloudTranslateAsync(text, settings, ct).ConfigureAwait(false);

            string targetName = TranslationLanguages.EnglishName(settings.TargetLanguage);
            string sourceName = TranslationLanguages.EnglishName(settings.SourceLanguage);
            string prompt = settings.BuildPrompt(
                promptTemplate ?? settings.SelectedPrompt?.Text ?? "", text, targetName, sourceName);

            var parts = new List<object> { new Dictionary<string, object?> { ["text"] = prompt } };
            string answer = await GeminiAsync(parts, settings, ct).ConfigureAwait(false);

            return new TranslationResult(
                "Google " + settings.GeminiModel, null, answer.Trim(), null, null);
        }

        private static async Task<TranslationResult> CloudTranslateAsync(
            string text, TranslationSettings settings, CancellationToken ct)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new("q", text),
                new("target", TranslationLanguages.GoogleCode(settings.TargetLanguage)),
                new("format", "text")
            };

            if (!string.IsNullOrWhiteSpace(settings.SourceLanguage))
                fields.Add(new("source", TranslationLanguages.GoogleCode(settings.SourceLanguage)));

            using var request = new HttpRequestMessage(
                HttpMethod.Post, CloudTranslateUrl + "?key=" + Uri.EscapeDataString(settings.GoogleKey.Trim()));
            request.Content = new FormUrlEncodedContent(fields);

            string body = await SendAsync(request, "Google Translate", settings, ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var items = doc.RootElement.GetProperty("data").GetProperty("translations");
                if (items.GetArrayLength() == 0)
                    throw new TranslationException(EmptyAnswer("Google Translate"));

                var first = items[0];
                // Даже при format=text ответ приходит с HTML-сущностями (&#39; и т.п.)
                string translated = WebUtility.HtmlDecode(
                    first.TryGetProperty("translatedText", out var t) ? t.GetString() ?? "" : "");
                string? detected = first.TryGetProperty("detectedSourceLanguage", out var d)
                    ? d.GetString()
                    : null;

                return new TranslationResult("Google Translate v2", null, translated, detected, null);
            }
            catch (TranslationException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationService.GoogleTranslate разбор ответа", ex, hideMessage: true);
                throw new TranslationException(BadAnswer("Google Translate"));
            }
        }

        /// <summary>
        /// Отправляет саму картинку в Gemini: читает текст с изображения и, если нужно,
        /// сразу переводит его. Полезно, когда локальный OCR Windows не справился.
        /// </summary>
        public static async Task<TranslationResult> RecognizeImageWithGoogleAsync(
            string imagePath, TranslationSettings settings, bool alsoTranslate, CancellationToken ct)
        {
            RequireGoogleKey(settings);

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new TranslationException(Loc.T(
                    "The image file is not available any more.",
                    "Файл изображения больше недоступен.",
                    "El archivo de imagen ya no está disponible."));

            var prepared = await Task.Run(() => PrepareImage(imagePath, settings), ct).ConfigureAwait(false);

            string targetName = TranslationLanguages.EnglishName(settings.TargetLanguage);
            string template = alsoTranslate ? settings.ImageOcrTranslatePrompt : settings.ImageOcrPrompt;
            string prompt = settings.BuildPrompt(template, "", targetName,
                TranslationLanguages.EnglishName(settings.SourceLanguage));

            var parts = new List<object>
            {
                new Dictionary<string, object?> { ["text"] = prompt },
                new Dictionary<string, object?>
                {
                    ["inline_data"] = new Dictionary<string, object?>
                    {
                        ["mime_type"] = prepared.Mime,
                        ["data"] = Convert.ToBase64String(prepared.Bytes)
                    }
                }
            };

            string answer = (await GeminiAsync(parts, settings, ct).ConfigureAwait(false)).Trim();

            string note = string.Format(
                Loc.T("Sent to Google: {0} ({1} KB)", "Отправлено в Google: {0} ({1} КБ)", "Enviado a Google: {0} ({1} KB)"),
                prepared.Description, prepared.Bytes.Length / 1024);

            return SplitRecognizedAnswer("Google " + settings.GeminiModel, answer, alsoTranslate, note);
        }

        /// <summary>
        /// Раскладывает ответ модели на исходный текст и перевод по маркерам
        /// === TEXT === / === TRANSLATION ===. Если модель их проигнорировала,
        /// отдаём весь ответ как перевод. Промты и маркеры у Gemini и Qwen общие.
        /// </summary>
        private static TranslationResult SplitRecognizedAnswer(
            string service, string answer, bool alsoTranslate, string note)
        {
            if (!alsoTranslate)
                return new TranslationResult(service, answer, "", null, note);

            int textIndex = answer.IndexOf(TranslationSettings.TextMarker, StringComparison.OrdinalIgnoreCase);
            int translationIndex = answer.IndexOf(TranslationSettings.TranslationMarker, StringComparison.OrdinalIgnoreCase);

            if (textIndex >= 0 && translationIndex > textIndex)
            {
                int textStart = textIndex + TranslationSettings.TextMarker.Length;
                string original = answer.Substring(textStart, translationIndex - textStart).Trim();
                string translated = answer
                    .Substring(translationIndex + TranslationSettings.TranslationMarker.Length).Trim();

                return new TranslationResult(service, original, translated, null, note);
            }

            return new TranslationResult(service, null, answer, null, note);
        }

        /// <summary>Проверка ключа Google: запрашивает описание выбранной модели.</summary>
        public static async Task<string> CheckGoogleAsync(TranslationSettings settings, CancellationToken ct)
        {
            RequireGoogleKey(settings);

            if (settings.GoogleEngine == GoogleTextEngine.CloudTranslationV2)
            {
                var probe = await CloudTranslateAsync("ping", settings, ct).ConfigureAwait(false);
                return "Google Translate v2 OK — \"ping\" -> \"" + probe.Translation + "\"";
            }

            string body = await SendGeminiAsync(
                host => new HttpRequestMessage(HttpMethod.Get,
                    $"{host}/models/{NormalizeModel(settings.GeminiModel)}"),
                settings, ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                string name = doc.RootElement.TryGetProperty("displayName", out var d)
                    ? d.GetString() ?? settings.GeminiModel
                    : settings.GeminiModel;
                return "Google OK — " + name;
            }
            catch (Exception ex)
            {
                AppLog.Debug("TranslationService.CheckGoogle разбор ответа", ex, hideMessage: true);
                return "Google OK";
            }
        }

        /// <summary>Список доступных моделей Gemini для выпадающего списка настроек.</summary>
        public static async Task<List<string>> ListGeminiModelsAsync(TranslationSettings settings, CancellationToken ct)
        {
            RequireGoogleKey(settings);

            string body = await SendGeminiAsync(
                host => new HttpRequestMessage(HttpMethod.Get, $"{host}/models?pageSize=200"),
                settings, ct).ConfigureAwait(false);

            var models = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("models", out var list)) return models;

                foreach (var model in list.EnumerateArray())
                {
                    if (!model.TryGetProperty("name", out var nameProp)) continue;
                    string name = nameProp.GetString() ?? "";
                    if (name.StartsWith("models/", StringComparison.Ordinal)) name = name.Substring(7);

                    // оставляем только те, что умеют generateContent
                    if (model.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        bool supported = false;
                        foreach (var m in methods.EnumerateArray())
                        {
                            if (string.Equals(m.GetString(), "generateContent", StringComparison.Ordinal))
                            {
                                supported = true;
                                break;
                            }
                        }
                        if (!supported) continue;
                    }

                    if (name.Length > 0) models.Add(name);
                }
            }
            catch (Exception ex)
            {
                // не разобрали ответ — вернём то, что успели собрать
                AppLog.Warn("TranslationService.ListGeminiModels разбор ответа", ex, hideMessage: true);
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        /// <summary>
        /// Приводит имя модели к виду "gemini-2.5-flash": убирает префикс "models/",
        /// косые черты и пробелы. Имя модели — часть пути URL, его нельзя прогонять
        /// через EscapeDataString: "/" превратится в %2F и Google ответит 400.
        /// </summary>
        private static string NormalizeModel(string model)
        {
            string result = (model ?? "").Trim().Trim('/');

            if (result.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(7);

            int slash = result.LastIndexOf('/');
            if (slash >= 0) result = result.Substring(slash + 1);

            return result.Length == 0 ? "gemini-2.5-flash" : result;
        }

        /// <summary>
        /// Отправляет запрос в Gemini. Ключ идёт заголовком x-goog-api-key,
        /// а не в адресе — так надёжнее и ключ не попадает в URL. Если версия API
        /// не подходит (400/404), автоматически пробуем следующую.
        /// </summary>
        private static async Task<string> SendGeminiAsync(
            Func<string, HttpRequestMessage> build, TranslationSettings settings, CancellationToken ct)
        {
            TranslationException? firstError = null;

            for (int i = 0; i < GeminiHosts.Length; i++)
            {
                using var request = build(GeminiHosts[i]);
                request.Headers.TryAddWithoutValidation("x-goog-api-key", settings.GoogleKey.Trim());

                try
                {
                    return await SendAsync(request, "Google", settings, ct).ConfigureAwait(false);
                }
                catch (TranslationException ex)
                    when (i + 1 < GeminiHosts.Length &&
                          (ex.Message.Contains("HTTP 400", StringComparison.Ordinal) ||
                           ex.Message.Contains("HTTP 404", StringComparison.Ordinal)))
                {
                    firstError ??= ex;
                }
            }

            throw firstError ?? new TranslationException(BadAnswer("Google"));
        }

        /// <summary>Достаёт человеческое сообщение из JSON-ошибки сервиса.</summary>
        private static string? ExtractApiError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String) return error.GetString();

                    if (error.ValueKind == JsonValueKind.Object)
                    {
                        string? message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                        string? status = error.TryGetProperty("status", out var st) ? st.GetString() : null;

                        // DashScope пишет причину в code: invalid_api_key, Arrearage и т.п.
                        if (string.IsNullOrEmpty(status) &&
                            error.TryGetProperty("code", out var errorCode) &&
                            errorCode.ValueKind == JsonValueKind.String)
                            status = errorCode.GetString();

                        if (!string.IsNullOrEmpty(message))
                            return string.IsNullOrEmpty(status) ? message : status + ": " + message;
                    }
                }

                if (root.TryGetProperty("message", out var plain) && plain.ValueKind == JsonValueKind.String)
                    return plain.GetString();
            }
            catch (Exception ex)
            {
                // не JSON — покажем тело как есть
                AppLog.Debug("TranslationService.ParseError ответ не JSON", ex, hideMessage: true);
            }

            return null;
        }

        private static async Task<string> GeminiAsync(
            List<object> parts, TranslationSettings settings, CancellationToken ct)
        {
            var payload = new Dictionary<string, object?>
            {
                ["contents"] = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["parts"] = parts
                    }
                },
                ["generationConfig"] = new Dictionary<string, object?>
                {
                    ["temperature"] = settings.GeminiTemperature
                }
            };

            string json = JsonSerializer.Serialize(payload);
            string model = NormalizeModel(settings.GeminiModel);

            string body = await SendGeminiAsync(host => new HttpRequestMessage(
                HttpMethod.Post, $"{host}/models/{model}:generateContent")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }, settings, ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // Запрос мог быть заблокирован фильтрами — объясняем это, а не молчим
                if (root.TryGetProperty("promptFeedback", out var feedback) &&
                    feedback.TryGetProperty("blockReason", out var blockReason))
                {
                    throw new TranslationException(string.Format(
                        Loc.T("Google refused the request (reason: {0}).",
                              "Google отклонил запрос (причина: {0}).",
                              "Google rechazó la solicitud (motivo: {0})."),
                        blockReason.GetString()));
                }

                if (!root.TryGetProperty("candidates", out var candidates) ||
                    candidates.GetArrayLength() == 0)
                    throw new TranslationException(EmptyAnswer("Google"));

                var candidate = candidates[0];
                var sb = new StringBuilder();

                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var answerParts))
                {
                    foreach (var part in answerParts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var textProp))
                            sb.Append(textProp.GetString());
                    }
                }

                string answer = sb.ToString();

                if (answer.Length == 0)
                {
                    string? finish = candidate.TryGetProperty("finishReason", out var f) ? f.GetString() : null;
                    throw new TranslationException(string.IsNullOrEmpty(finish)
                        ? EmptyAnswer("Google")
                        : string.Format(
                            Loc.T("Google returned no text (finishReason: {0}).",
                                  "Google не вернул текст (finishReason: {0}).",
                                  "Google no devolvió texto (finishReason: {0})."),
                            finish));
                }

                return answer;
            }
            catch (TranslationException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationService.Gemini разбор ответа", ex, hideMessage: true);
                throw new TranslationException(BadAnswer("Google"));
            }
        }

        // -------------------------------------------------------------------- Qwen

        private const string QwenIntlBase = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
        private const string QwenBeijingBase = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        private const string QwenUsBase = "https://dashscope-us.aliyuncs.com/compatible-mode/v1";
        private const string QwenCodingBase = "https://coding-intl.dashscope.aliyuncs.com/v1";

        /// <summary>
        /// Адрес OpenAI-совместимого API Alibaba Model Studio (DashScope).
        /// Ключ привязан и к региону, и к тарифу: на чужом адресе будет 401.
        /// Домен рабочего пространства задаётся вручную, шаблона у него нет.
        /// </summary>
        private static string QwenBase(TranslationSettings settings)
        {
            if (settings.QwenRegion == QwenRegionMode.Custom)
            {
                string custom = NormalizeQwenBase(settings.QwenBaseUrl);
                if (custom.Length == 0)
                    throw new TranslationException(Loc.T(
                        "Qwen: the custom address is empty. Paste the Base URL from the Model Studio console.",
                        "Qwen: свой адрес не заполнен. Вставьте Base URL из консоли Model Studio.",
                        "Qwen: falta la URL propia. Pegue la Base URL de la consola de Model Studio."));
                return custom;
            }

            return settings.QwenRegion switch
            {
                QwenRegionMode.Beijing => QwenBeijingBase,
                QwenRegionMode.UsVirginia => QwenUsBase,
                QwenRegionMode.CodingPlan => QwenCodingBase,
                _ => QwenIntlBase
            };
        }

        /// <summary>
        /// Приводит вставленный адрес к рабочему виду: дописывает https://, убирает
        /// хвостовые косые черты и /chat/completions, добавляет /compatible-mode/v1,
        /// если скопировали только домен рабочего пространства.
        /// </summary>
        private static string NormalizeQwenBase(string? url)
        {
            string result = (url ?? "").Trim().Trim('"', '\'').TrimEnd('/');
            if (result.Length == 0) return "";

            if (!result.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !result.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                result = "https://" + result;

            const string tail = "/chat/completions";
            if (result.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.Length - tail.Length);

            if (!result.Contains("/v1", StringComparison.OrdinalIgnoreCase))
                result += "/compatible-mode/v1";

            return result.TrimEnd('/');
        }

        /// <summary>
        /// Чистит ключ перед отправкой: из консоли его нередко копируют вместе с
        /// «Bearer », кавычками, переносом строки или невидимыми символами —
        /// с ними сервис отвечает 401, хотя сам ключ верный.
        /// </summary>
        private static string CleanQwenKey(TranslationSettings settings)
        {
            string key = (settings.QwenKey ?? "").Trim().Trim('"', '\'');

            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("Bearer ".Length);

            var sb = new StringBuilder(key.Length);
            foreach (char c in key)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
                if (c == '\u200b' || c == '\u200e' || c == '\u200f' || c == '\ufeff') continue;
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>Ставит заголовок авторизации и сразу проверяет, что он принят.</summary>
        private static void AddQwenAuth(HttpRequestMessage request, TranslationSettings settings)
        {
            if (!request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + CleanQwenKey(settings)))
                throw new TranslationException(Loc.T(
                    "Qwen: the key contains characters that cannot be sent. Copy it from the console again.",
                    "Qwen: в ключе есть символы, которые нельзя отправить. Скопируйте его из консоли заново.",
                    "Qwen: la clave tiene caracteres no admitidos. Cópiela de nuevo desde la consola."));
        }

        /// <summary>Текстовые модели для выпадающего списка настроек.</summary>
        public static readonly string[] QwenTextModels =
        {
            "qwen-plus", "qwen-max", "qwen-turbo", "qwen-flash"
        };

        /// <summary>Модели со зрением: только они читают текст с картинки.</summary>
        public static readonly string[] QwenVisionModels =
        {
            "qwen-vl-max", "qwen-vl-plus", "qwen-vl-ocr", "qwen3-vl-plus"
        };

        /// <summary>Перевод текста через Qwen: те же промты и плейсхолдеры, что у Gemini.</summary>
        public static async Task<TranslationResult> TranslateWithQwenAsync(
            string text, TranslationSettings settings, string? promptTemplate, CancellationToken ct)
        {
            RequireText(text);
            RequireQwenKey(settings);

            string targetName = TranslationLanguages.EnglishName(settings.TargetLanguage);
            string sourceName = TranslationLanguages.EnglishName(settings.SourceLanguage);
            string prompt = settings.BuildPrompt(
                promptTemplate ?? settings.SelectedPrompt?.Text ?? "", text, targetName, sourceName);

            string model = NormalizeQwenModel(settings.QwenModel, "qwen-plus");
            string answer = await QwenChatAsync(model, prompt, settings, ct).ConfigureAwait(false);

            return new TranslationResult("Qwen " + model, null, answer.Trim(), null, null);
        }

        /// <summary>
        /// Отправляет само фото в Qwen-VL: читает текст с картинки и, если нужно, сразу переводит.
        /// Картинка готовится тем же способом, что и для Gemini (уменьшение + пересжатие без EXIF).
        /// </summary>
        public static async Task<TranslationResult> RecognizeImageWithQwenAsync(
            string imagePath, TranslationSettings settings, bool alsoTranslate, CancellationToken ct)
        {
            RequireQwenKey(settings);

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new TranslationException(Loc.T(
                    "The image file is not available any more.",
                    "Файл изображения больше недоступен.",
                    "El archivo de imagen ya no está disponible."));

            var prepared = await Task.Run(() => PrepareImage(imagePath, settings), ct).ConfigureAwait(false);

            string targetName = TranslationLanguages.EnglishName(settings.TargetLanguage);
            string template = alsoTranslate ? settings.ImageOcrTranslatePrompt : settings.ImageOcrPrompt;
            string prompt = settings.BuildPrompt(template, "", targetName,
                TranslationLanguages.EnglishName(settings.SourceLanguage));

            // Картинка уходит внутри самого запроса как data-URL: никуда не выкладывается
            string dataUrl = "data:" + prepared.Mime + ";base64," + Convert.ToBase64String(prepared.Bytes);

            var content = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?> { ["url"] = dataUrl }
                },
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt }
            };

            string model = NormalizeQwenModel(settings.QwenVisionModel, "qwen-vl-max");
            string answer = (await QwenChatAsync(model, content, settings, ct).ConfigureAwait(false)).Trim();

            string note = string.Format(
                Loc.T("Sent to Qwen: {0} ({1} KB)", "Отправлено в Qwen: {0} ({1} КБ)", "Enviado a Qwen: {0} ({1} KB)"),
                prepared.Description, prepared.Bytes.Length / 1024);

            return SplitRecognizedAnswer("Qwen " + model, answer, alsoTranslate, note);
        }

        /// <summary>
        /// Проверка ключа Qwen. Если выбранный адрес ответил 401/403, сами обходим
        /// остальные известные адреса и говорим, какой из них принимает этот ключ, —
        /// иначе пользователю пришлось бы угадывать регион вслепую.
        /// </summary>
        public static async Task<string> CheckQwenAsync(TranslationSettings settings, CancellationToken ct)
        {
            RequireQwenKey(settings);

            string model = NormalizeQwenModel(settings.QwenModel, "qwen-plus");

            string vision = NormalizeQwenModel(settings.QwenVisionModel, "qwen-vl-max");

            try
            {
                await QwenPingAsync(QwenBase(settings), model, settings, ct).ConfigureAwait(false);
            }
            catch (TranslationException ex) when (ex.Message.Contains("HTTP 403"))
            {
                throw new TranslationException(ex.Message + "\n\n" +
                    await ExplainQwenForbiddenAsync(model, settings, ct).ConfigureAwait(false));
            }
            catch (TranslationException ex) when (ex.Message.Contains("HTTP 401"))
            {
                var match = await FindQwenEndpointAsync(model, settings, ct).ConfigureAwait(false);

                if (match == null)
                    throw new TranslationException(ex.Message + "\n\n" + Loc.T(
                        "No known Qwen address accepted this key. Check the key itself, or paste your workspace Base URL.",
                        "Ни один известный адрес Qwen не принял этот ключ. Проверьте сам ключ или вставьте Base URL своего рабочего пространства.",
                        "Ninguna dirección conocida de Qwen aceptó la clave. Revise la clave o pegue la Base URL de su espacio de trabajo."));

                throw new TranslationException(string.Format(
                    Loc.T("Qwen: this address rejected the key, but it works with \"{0}\". Select it in the region list.",
                          "Qwen: выбранный адрес не принял ключ, но он работает с «{0}». Выберите этот пункт в списке регионов.",
                          "Qwen: esta dirección rechazó la clave, pero funciona con «{0}». Selecciónela en la lista de regiones."),
                    match));
            }

            // Текст прошёл. Модель для фото оплачивается и разрешается отдельно,
            // поэтому проверяем и её: иначе 403 всплывёт только при распознавании.
            string visionNote;
            try
            {
                await QwenPingAsync(QwenBase(settings), vision, settings, ct).ConfigureAwait(false);
                visionNote = string.Format(
                    Loc.T("photo model {0}: OK",
                          "модель для фото {0}: OK",
                          "modelo para fotos {0}: OK"),
                    vision);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Debug("TranslationService.CheckQwen vision", ex, hideMessage: true);
                visionNote = string.Format(
                    Loc.T("photo model {0} is unavailable — {1}",
                          "модель для фото {0} недоступна — {1}",
                          "el modelo para fotos {0} no está disponible — {1}"),
                    vision, FirstLine(ex.Message));
            }

            return "Qwen OK — " + model + "\n" + visionNote;
        }

        /// <summary>
        /// 403 приходит уже после проверки ключа, значит дело не в авторизации.
        /// Разделяем два случая: закрыта одна модель или весь аккаунт.
        /// </summary>
        private static async Task<string> ExplainQwenForbiddenAsync(
            string model, TranslationSettings settings, CancellationToken ct)
        {
            if (!string.Equals(model, "qwen-plus", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await QwenPingAsync(QwenBase(settings), "qwen-plus", settings, ct).ConfigureAwait(false);

                    return string.Format(
                        Loc.T("The key itself works here — qwen-plus answers, but \"{0}\" is closed for it. Pick another model, or open access to this one in the Model Studio console.",
                              "Сам ключ здесь работает — qwen-plus отвечает, а «{0}» ему закрыта. Выберите другую модель или откройте доступ к этой в консоли Model Studio.",
                              "La clave funciona aquí — qwen-plus responde, pero «{0}» está cerrada. Elija otro modelo o abra el acceso en la consola de Model Studio."),
                        model);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Debug("TranslationService.ExplainQwenForbidden qwen-plus", ex, hideMessage: true);
                }
            }

            return Loc.T(
                "Access is closed for the whole account at this address: Model Studio is usually not activated in this exact region, the balance is overdue, or a workspace key has no model permissions.",
                "Доступ закрыт для всего аккаунта на этом адресе: обычно Model Studio не активирован именно в этом регионе, на аккаунте задолженность или у ключа рабочего пространства нет прав на модели.",
                "El acceso está cerrado para toda la cuenta en esta dirección: normalmente Model Studio no está activado en esta región, la cuenta tiene deuda o la clave del espacio de trabajo no tiene permisos.");
        }

        /// <summary>Короткий запрос к конкретному адресу: важен сам факт успеха.</summary>
        private static async Task QwenPingAsync(
            string baseUrl, string model, TranslationSettings settings, CancellationToken ct)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object?> { ["role"] = "user", ["content"] = "ping" }
                },
                ["max_tokens"] = 16
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            AddQwenAuth(request, settings);

            await SendAsync(request, "Qwen", settings, ct).ConfigureAwait(false);
        }

        /// <summary>Ищет адрес, который принимает этот ключ. Возвращает подпись пункта списка.</summary>
        private static async Task<string?> FindQwenEndpointAsync(
            string model, TranslationSettings settings, CancellationToken ct)
        {
            string current = "";
            try { current = QwenBase(settings); }
            catch (TranslationException) { /* свой адрес не заполнен — проверим стандартные */ }

            var candidates = new List<(string Label, string Url)>
            {
                ("International (Singapore)", QwenIntlBase),
                ("China (Beijing)", QwenBeijingBase),
                ("US (Virginia)", QwenUsBase),
                ("Coding Plan", QwenCodingBase)
            };

            foreach (var (label, url) in candidates)
            {
                if (string.Equals(url, current, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    await QwenPingAsync(url, model, settings, ct).ConfigureAwait(false);
                    return label;
                }
                catch (OperationCanceledException) { throw; }
                catch (TranslationException ex)
                    when (ex.Message.Contains("HTTP 403") || ex.Message.Contains("HTTP 404"))
                {
                    // Ключ здесь приняли: ошибка уже про модель или права, а не про авторизацию,
                    // значит адрес для этого ключа верный.
                    AppLog.Debug("TranslationService.FindQwenEndpoint " + label, ex, hideMessage: true);
                    return label;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("TranslationService.FindQwenEndpoint " + label, ex, hideMessage: true);
                }
            }

            return null;
        }

        /// <summary>
        /// Список моделей для выпадающего списка. Если аккаунту недоступен /models,
        /// возвращаем встроенный список, чтобы кнопка всё равно была полезной.
        /// </summary>
        public static async Task<List<string>> ListQwenModelsAsync(TranslationSettings settings, CancellationToken ct)
        {
            RequireQwenKey(settings);

            var models = new List<string>();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, QwenBase(settings) + "/models");
                AddQwenAuth(request, settings);

                string body = await SendAsync(request, "Qwen", settings, ct).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        if (id.Length > 0) models.Add(id);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // На части аккаунтов список моделей закрыт — это не ошибка перевода
                AppLog.Debug("TranslationService.ListQwenModels", ex, hideMessage: true);
            }

            if (models.Count == 0)
            {
                models.AddRange(QwenTextModels);
                models.AddRange(QwenVisionModels);
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        /// <summary>
        /// Один запрос к /chat/completions. content — либо строка (текст),
        /// либо список частей (картинка + текст) для моделей со зрением.
        /// </summary>
        private static async Task<string> QwenChatAsync(
            string model, object content, TranslationSettings settings, CancellationToken ct)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = content
                    }
                },
                ["temperature"] = settings.QwenTemperature
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, QwenBase(settings) + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            AddQwenAuth(request, settings);

            string body = await SendAsync(request, "Qwen", settings, ct).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    throw new TranslationException(EmptyAnswer("Qwen"));

                var choice = choices[0];
                string answer = ReadQwenContent(choice);

                if (answer.Length == 0)
                {
                    string? finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
                    throw new TranslationException(
                        string.IsNullOrEmpty(finish) || finish == "stop"
                            ? EmptyAnswer("Qwen")
                            : string.Format(
                                Loc.T("Qwen returned no text (finish_reason: {0}).",
                                      "Qwen не вернул текст (finish_reason: {0}).",
                                      "Qwen no devolvió texto (finish_reason: {0})."),
                                finish));
                }

                return answer;
            }
            catch (TranslationException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("TranslationService.Qwen разбор ответа", ex, hideMessage: true);
                throw new TranslationException(BadAnswer("Qwen"));
            }
        }

        /// <summary>
        /// Достаёт текст ответа: обычно это строка, но модели со зрением иногда
        /// отвечают массивом частей — учитываем оба варианта.
        /// </summary>
        private static string ReadQwenContent(JsonElement choice)
        {
            if (!choice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content)) return "";

            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) { sb.Append(part.GetString()); continue; }
                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
                }
                return sb.ToString();
            }

            return "";
        }

        /// <summary>Имя модели уходит в теле запроса: убираем пробелы и лишние косые черты.</summary>
        private static string NormalizeQwenModel(string? model, string fallback)
        {
            string result = (model ?? "").Trim().Trim('/');
            return result.Length == 0 ? fallback : result;
        }

        private static void RequireQwenKey(TranslationSettings settings)
        {
            if (!HasQwenKey(settings))
                throw new TranslationException(Loc.T(
                    "No Qwen API key. Add it in Translation settings.",
                    "Не указан ключ Qwen. Добавьте его в настройках перевода.",
                    "Falta la clave de Qwen. Añádela en los ajustes de traducción."));

            // Ключ тарифного плана живёт на своём адресе. Несовпадение — гарантированный 401,
            // поэтому ловим его до сети и объясняем словами.
            string key = CleanQwenKey(settings);
            bool planKey = key.StartsWith("sk-sp-", StringComparison.OrdinalIgnoreCase);
            bool planEndpoint = settings.QwenRegion == QwenRegionMode.CodingPlan ||
                NormalizeQwenBase(settings.QwenBaseUrl).Contains("coding", StringComparison.OrdinalIgnoreCase);

            if (planKey && !planEndpoint)
                throw new TranslationException(Loc.T(
                    "This is a Coding Plan key (sk-sp-…). Select \"Coding Plan\" in the region list.",
                    "Это ключ тарифа Coding Plan (sk-sp-…). Выберите в списке регионов пункт «Coding Plan».",
                    "Es una clave de Coding Plan (sk-sp-…). Elija «Coding Plan» en la lista de regiones."));

            if (!planKey && planEndpoint)
                throw new TranslationException(Loc.T(
                    "The Coding Plan address needs a plan key (sk-sp-…). Choose a normal region instead.",
                    "Для адреса Coding Plan нужен ключ тарифа (sk-sp-…). Выберите обычный регион.",
                    "La dirección Coding Plan requiere una clave de plan (sk-sp-…). Elija una región normal."));
        }

        // ------------------------------------------------------- подготовка картинки

        private sealed record PreparedImage(byte[] Bytes, string Mime, string Description);

        /// <summary>
        /// Готовит картинку к отправке: применяет EXIF-ориентацию, уменьшает до
        /// MaxImageSide и пересжимает в JPEG. Пересжатие заодно убирает EXIF/GPS —
        /// в сеть уходят только пиксели.
        /// </summary>
        private static PreparedImage PrepareImage(string path, TranslationSettings settings)
        {
            byte[] raw = File.ReadAllBytes(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();

            byte[] original = raw;
            using var input = new MemoryStream(raw);
            var decoder = BitmapDecoder.Create(input,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            int limit = Math.Clamp(settings.MaxImageSide, 512, 4096);
            int maxSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
            bool needsResize = maxSide > limit;

            // Без пересжатия отправляем исходный файл как есть — но только
            // для форматов, которые понимает API, и если уменьшать не требуется
            if (!settings.StripMetadataBeforeSend && !needsResize && original.Length <= 15 * 1024 * 1024)
            {
                string? directMime = ext switch
                {
                    ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".heic" => "image/heic",
                    _ => null
                };

                if (directMime != null)
                    return new PreparedImage(original, directMime,
                        $"{Path.GetFileName(path)}, {frame.PixelWidth}x{frame.PixelHeight}, " +
                        Loc.T("original file with metadata", "исходный файл с метаданными", "archivo original con metadatos"));
            }

            BitmapSource image = ExifOrientationService.ApplyOrientation(
                frame, ExifOrientationService.GetOrientation(frame));

            if (needsResize)
            {
                double scale = (double)limit / maxSide;
                image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            }

            if (image.CanFreeze) image.Freeze();

            // BitmapFrame.Create(BitmapSource) не переносит метаданные — EXIF/GPS не уедут в сеть
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var output = new MemoryStream();
            encoder.Save(output);

            return new PreparedImage(output.ToArray(), "image/jpeg",
                $"JPEG {image.PixelWidth}x{image.PixelHeight}, " +
                Loc.T("pixels only, no metadata", "только пиксели, без метаданных", "solo píxeles, sin metadatos"));
        }

        // ------------------------------------------------------------- общий транспорт

        /// <summary>
        /// 403 = ключ приняли, но доступ не дали. Причина лежит в коде ответа,
        /// поэтому подсказку выбираем по нему, а не по номеру ошибки.
        /// </summary>
        private static string ForbiddenHint(string body)
        {
            body ??= "";

            if (body.Contains("Unpurchased", StringComparison.OrdinalIgnoreCase))
                return Loc.T(
                    "The service is not activated for this account in this region. Open the Model Studio console, switch it to the same region as the key, and accept the terms.",
                    "Сервис не активирован для этого аккаунта в этом регионе. Откройте консоль Model Studio, переключите её на регион ключа и примите условия.",
                    "El servicio no está activado para esta cuenta en esta región. Abra la consola de Model Studio en la región de la clave y acepte los términos.");

            if (body.Contains("Model.AccessDenied", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("Model access denied", StringComparison.OrdinalIgnoreCase))
                return Loc.T(
                    "The key has no permission for this model. Pick another model, or grant this model to the key or the workspace in the Model Studio console — a key created with a limited model scope can call only the models selected for it.",
                    "У ключа нет прав на эту модель. Выберите другую модель или выдайте права на неё ключу или рабочему пространству в консоли Model Studio: ключ с ограниченной областью вызывает только выбранные при создании модели.",
                    "La clave no tiene permiso para este modelo. Elija otro modelo o conceda el acceso a la clave o al espacio de trabajo en la consola de Model Studio.");

            if (body.Contains("Arrearage", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("in debt", StringComparison.OrdinalIgnoreCase))
                return Loc.T(
                    "The account has an unpaid balance or the free quota is used up — top up the balance and try again.",
                    "На аккаунте задолженность или закончилась бесплатная квота — пополните баланс и повторите.",
                    "La cuenta tiene un saldo pendiente o la cuota gratuita se agotó: recargue el saldo e inténtelo de nuevo.");

            return Loc.T(
                "The key was accepted, but access is denied: the service may not be activated in this region, the model may be closed for this key, or the balance may be overdue.",
                "Ключ принят, но доступ запрещён: сервис может быть не активирован в этом регионе, модель закрыта для этого ключа или на аккаунте задолженность.",
                "La clave fue aceptada, pero el acceso está denegado: el servicio puede no estar activado en esta región, el modelo puede estar cerrado o la cuenta tiene deuda.");
        }

        /// <summary>Первая строка сообщения — для коротких сводок.</summary>
        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int i = text.IndexOf('\n');
            return (i < 0 ? text : text.Substring(0, i)).Trim();
        }

        private static async Task<string> SendAsync(
            HttpRequestMessage request, string service, TranslationSettings settings, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 600)));

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // отмена пользователем — пусть обработает вызывающая сторона
            }
            catch (OperationCanceledException)
            {
                throw new TranslationException(string.Format(
                    Loc.T("{0}: request timed out after {1} s.",
                          "{0}: превышено время ожидания ({1} с).",
                          "{0}: tiempo de espera agotado ({1} s)."),
                    service, settings.TimeoutSeconds));
            }
            catch (HttpRequestException ex)
            {
                throw new TranslationException(string.Format(
                    Loc.T("{0}: could not reach the service ({1}).",
                          "{0}: не удалось связаться с сервисом ({1}).",
                          "{0}: no se pudo conectar con el servicio ({1})."),
                    service, ex.Message));
            }

            using (response)
            {
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("TranslationService.ReadBody", ex, hideMessage: true);
                    body = "";
                }

                if (response.IsSuccessStatusCode) return body;

                int code = (int)response.StatusCode;
                string hint = code switch
                {
                    400 => Loc.T(
                        "Check the model name and the target language. If the service says the key is invalid, the key may be for a different API (Gemini keys come from Google AI Studio).",
                        "Проверьте название модели и язык перевода. Если сервис пишет, что ключ неверный, ключ может быть от другого API (ключи Gemini выдаёт Google AI Studio).",
                        "Compruebe el nombre del modelo y el idioma. Si el servicio dice que la clave no es válida, puede ser de otra API (las claves de Gemini vienen de Google AI Studio)."),
                    401 => Loc.T(
                        "The API key looks wrong, disabled, or does not match this address. A Qwen key works only with the region and the plan it was created for; keys from the new console may need your workspace Base URL.",
                        "Ключ неверный, отключён или не подходит этому адресу. Ключ Qwen работает только со своим регионом и тарифом; ключам из новой консоли может требоваться Base URL вашего рабочего пространства.",
                        "La clave es incorrecta, está desactivada o no corresponde a esta dirección. Una clave de Qwen solo sirve para su región y su plan; las claves nuevas pueden requerir la Base URL de su espacio de trabajo."),
                    403 => ForbiddenHint(body),
                    404 => Loc.T("Model or endpoint not found — check the model name.",
                                 "Модель или адрес не найдены — проверьте название модели.",
                                 "Modelo o endpoint no encontrado: compruebe el nombre del modelo."),
                    429 => Loc.T("Rate limit or quota exceeded — try again later.",
                                 "Превышен лимит запросов или квота — попробуйте позже.",
                                 "Límite de solicitudes o cuota excedidos: inténtelo más tarde."),
                    456 => Loc.T("DeepL character quota for this key is exhausted.",
                                 "Квота символов для этого ключа DeepL исчерпана.",
                                 "La cuota de caracteres de esta clave de DeepL está agotada."),
                    >= 500 => Loc.T("The service is having problems — try again later.",
                                    "Проблемы на стороне сервиса — попробуйте позже.",
                                    "El servicio tiene problemas: inténtelo más tarde."),
                    _ => ""
                };

                string details = Shorten(Sanitize(ExtractApiError(body) ?? body, settings));

                var sb = new StringBuilder();
                sb.Append(service).Append(": HTTP ").Append(code);
                if (!string.IsNullOrEmpty(response.ReasonPhrase)) sb.Append(' ').Append(response.ReasonPhrase);
                if (hint.Length > 0) sb.Append('\n').Append(hint);
                if (details.Length > 0) sb.Append("\n\n").Append(details);

                throw new TranslationException(sb.ToString());
            }
        }

        private static void RequireText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new TranslationException(Loc.T(
                    "There is no text to translate.",
                    "Нет текста для перевода.",
                    "No hay texto para traducir."));
        }

        private static void RequireGoogleKey(TranslationSettings settings)
        {
            if (!HasGoogleKey(settings))
                throw new TranslationException(Loc.T(
                    "No Google API key. Add it in Translation settings.",
                    "Не указан ключ Google. Добавьте его в настройках перевода.",
                    "Falta la clave de Google. Añádela en los ajustes de traducción."));
        }

        private static string EmptyAnswer(string service) => string.Format(
            Loc.T("{0} returned an empty answer.", "{0} вернул пустой ответ.", "{0} devolvió una respuesta vacía."),
            service);

        private static string BadAnswer(string service) => string.Format(
            Loc.T("Could not read the answer from {0}.",
                  "Не удалось разобрать ответ от {0}.",
                  "No se pudo interpretar la respuesta de {0}."),
            service);

        /// <summary>Вырезает ключи из текста ошибки, чтобы они не попали на экран/скриншот.</summary>
        private static string Sanitize(string body, TranslationSettings settings)
        {
            if (string.IsNullOrEmpty(body)) return "";

            string result = body;
            foreach (var key in new[] { settings.GoogleKey, settings.DeepLKey, settings.QwenKey })
            {
                var trimmed = (key ?? "").Trim();
                if (trimmed.Length >= 8)
                    result = result.Replace(trimmed, "***", StringComparison.Ordinal);
            }
            return result;
        }

        private static string Shorten(string text, int limit = 400)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Trim();
            return text.Length <= limit ? text : text.Substring(0, limit) + "…";
        }
    }
}
