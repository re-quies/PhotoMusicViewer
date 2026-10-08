using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoMusicViewer.Services
{
    /// <summary>Какой сервис Google использовать для перевода текста.</summary>
    public enum GoogleTextEngine
    {
        /// <summary>Gemini (generativelanguage.googleapis.com) — понимает модель и промты, умеет читать картинки.</summary>
        Gemini,

        /// <summary>Cloud Translation v2 — быстрый машинный перевод, промты и модели не поддерживает.</summary>
        CloudTranslationV2
    }

    /// <summary>Какой хост DeepL использовать. Auto определяет по суффиксу ":fx" в ключе.</summary>
    public enum DeepLEndpointMode { Auto, Free, Pro }

    /// <summary>
    /// Адрес API Alibaba Model Studio (Qwen). Ключ привязан и к региону, и к тарифу:
    /// на чужом адресе сервис отвечает 401. CodingPlan — ключи вида sk-sp-…,
    /// Custom — свой адрес рабочего пространства ({id}.{region}.maas.aliyuncs.com).
    /// </summary>
    public enum QwenRegionMode { International, Beijing, UsVirginia, CodingPlan, Custom }

    /// <summary>Именованный шаблон промта. Плейсхолдеры: {text}, {target}, {source}.</summary>
    public sealed class PromptPreset
    {
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";

        public override string ToString() => Name;
    }

    /// <summary>
    /// Все настройки перевода. По умолчанию живут только в памяти процесса —
    /// как и остальные настройки этого приложения. На диск попадают лишь при явно
    /// включённой галочке PersistToDisk, причём ключи API шифруются DPAPI под
    /// текущую учётную запись Windows — см. SecretProtector.
    /// </summary>
    public sealed class TranslationSettings
    {
        // --- Ключи API (вводит пользователь) ---
        // В JSON эти три свойства не попадают никогда: на диск уходят только
        // зашифрованные варианты ниже.
        [JsonIgnore] public string DeepLKey { get; set; } = "";
        [JsonIgnore] public string GoogleKey { get; set; } = "";
        [JsonIgnore] public string QwenKey { get; set; } = "";

        /// <summary>Ключ DeepL, зашифрованный DPAPI. Заполняется только на время записи/чтения файла.</summary>
        [JsonPropertyName("DeepLKeyProtected")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DeepLKeyProtected { get; set; }

        /// <summary>Ключ Google, зашифрованный DPAPI.</summary>
        [JsonPropertyName("GoogleKeyProtected")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GoogleKeyProtected { get; set; }

        /// <summary>Ключ Qwen, зашифрованный DPAPI.</summary>
        [JsonPropertyName("QwenKeyProtected")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? QwenKeyProtected { get; set; }

        // Ключи из файлов старых версий — там они лежали открытым текстом.
        // Читаются один раз при миграции и больше никогда не записываются
        // (сразу после чтения обнуляются, а null не сериализуется).
        [JsonPropertyName("DeepLKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyDeepLKey { get; set; }

        [JsonPropertyName("GoogleKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyGoogleKey { get; set; }

        [JsonPropertyName("QwenKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyQwenKey { get; set; }

        // --- Языки ---
        /// <summary>Код языка перевода в нотации DeepL (RU, EN-US, PT-BR, ...).</summary>
        public string TargetLanguage { get; set; } = "RU";

        /// <summary>Пусто = автоопределение.</summary>
        public string SourceLanguage { get; set; } = "";

        // --- DeepL ---
        public DeepLEndpointMode DeepLEndpoint { get; set; } = DeepLEndpointMode.Auto;
        public string DeepLFormality { get; set; } = "default";
        public string DeepLModelType { get; set; } = "prefer_quality_optimized";
        /// <summary>Контекст (не переводится, помогает выбрать смысл). Аналог промта у DeepL.</summary>
        public string DeepLContext { get; set; } = "";
        public bool DeepLPreserveFormatting { get; set; } = true;

        // --- Google ---
        public GoogleTextEngine GoogleEngine { get; set; } = GoogleTextEngine.Gemini;
        public string GeminiModel { get; set; } = "gemini-2.5-flash";
        public double GeminiTemperature { get; set; } = 0.2;

        // --- Qwen (Alibaba Model Studio / DashScope) ---
        /// <summary>Регион адреса API. Должен совпадать с регионом, где выдан ключ.</summary>
        public QwenRegionMode QwenRegion { get; set; } = QwenRegionMode.International;

        /// <summary>
        /// Свой адрес API (Base URL) — нужен для домена рабочего пространства вида
        /// https://{workspace-id}.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1.
        /// Учитывается, когда в списке регионов выбран пункт «Свой адрес».
        /// </summary>
        public string QwenBaseUrl { get; set; } = "";
        public bool QwenAllowLoopbackHttp { get; set; } = false;

        /// <summary>Модель для перевода текста.</summary>
        public string QwenModel { get; set; } = "qwen-plus";

        /// <summary>
        /// Модель для чтения текста с фото. Обычные текстовые модели Qwen картинки
        /// не принимают, поэтому модель зрения хранится отдельно (в отличие от Gemini).
        /// </summary>
        public string QwenVisionModel { get; set; } = "qwen-vl-max";

        public double QwenTemperature { get; set; } = 0.2;

        // --- Промты ---
        public List<PromptPreset> Prompts { get; set; } = new();
        public int SelectedPromptIndex { get; set; }

        /// <summary>Промт для режима «фото → текст» (без перевода).</summary>
        public string ImageOcrPrompt { get; set; } = "";

        /// <summary>Промт для режима «фото → текст + перевод».</summary>
        public string ImageOcrTranslatePrompt { get; set; } = "";

        // --- Сеть и приватность ---
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>Максимальная сторона картинки перед отправкой (меньше трафика и быстрее ответ).</summary>
        public int MaxImageSide { get; set; } = 1600;

        /// <summary>Пересжимать картинку перед отправкой, чтобы не отправлять EXIF/GPS.</summary>
        public bool StripMetadataBeforeSend { get; set; } = true;

        /// <summary>Спрашивать подтверждение перед первым сетевым запросом в сессии.</summary>
        public bool AskBeforeNetwork { get; set; } = true;

        /// <summary>Сохранять настройки (включая ключи) в файл открытым текстом.</summary>
        public bool PersistToDisk { get; set; }

        public PromptPreset? SelectedPrompt =>
            Prompts.Count == 0
                ? null
                : Prompts[Math.Clamp(SelectedPromptIndex, 0, Prompts.Count - 1)];

        public static TranslationSettings CreateDefault()
        {
            var s = new TranslationSettings();
            s.Prompts = DefaultPrompts();
            s.ImageOcrPrompt = DefaultImageOcrPrompt;
            s.ImageOcrTranslatePrompt = DefaultImageOcrTranslatePrompt;
            return s;
        }

        public const string TextMarker = "=== TEXT ===";
        public const string TranslationMarker = "=== TRANSLATION ===";

        /// <summary>Языки, для которых есть встроенные промты.</summary>
        internal static readonly Loc.AppLanguage[] AllLanguages =
        {
            Loc.AppLanguage.English, Loc.AppLanguage.Russian, Loc.AppLanguage.Spanish
        };

        /// <summary>Промт распознавания текста на языке интерфейса.</summary>
        public static string DefaultImageOcrPrompt => DefaultImageOcrPromptFor(Loc.Language);

        /// <summary>Промт «распознать и перевести» на языке интерфейса.</summary>
        public static string DefaultImageOcrTranslatePrompt => DefaultImageOcrTranslatePromptFor(Loc.Language);

        // Плейсхолдеры ({target}, [no text]) и маркеры ответа одинаковы во всех
        // языках: разбор ответа в TranslationService от языка интерфейса не зависит.
        public static string DefaultImageOcrPromptFor(Loc.AppLanguage lang) => lang switch
        {
            Loc.AppLanguage.Russian =>
                "Прочитай весь текст, видный на этом изображении, ровно так, как он напечатан. " +
                "Сохрани исходные переносы строк и естественный порядок чтения. " +
                "Не переводи, не комментируй, ничего не добавляй. " +
                "Выведи только текст. Если текста на изображении нет, выведи ровно: [no text]",
            Loc.AppLanguage.Spanish =>
                "Lee todo el texto visible en esta imagen exactamente como está impreso. " +
                "Conserva los saltos de línea originales y el orden natural de lectura. " +
                "No traduzcas, no comentes, no añadas nada. " +
                "Devuelve solo el texto. Si la imagen no contiene texto, devuelve exactamente: [no text]",
            _ =>
                "Read every piece of text visible in this image exactly as printed. " +
                "Keep the original line breaks and natural reading order. " +
                "Do not translate, do not comment, do not add anything. " +
                "Output only the text. If the image contains no text, output exactly: [no text]"
        };

        public static string DefaultImageOcrTranslatePromptFor(Loc.AppLanguage lang) => lang switch
        {
            Loc.AppLanguage.Russian =>
                "Прочитай весь текст, видный на этом изображении, ровно так, как он напечатан, затем переведи его на {target}.\n" +
                "Ответь строго в этом формате и не добавляй ничего лишнего:\n" +
                TextMarker + "\n<исходный текст, переносы строк сохранены>\n" +
                TranslationMarker + "\n<перевод на {target}>",
            Loc.AppLanguage.Spanish =>
                "Lee todo el texto visible en esta imagen exactamente como está impreso y luego tradúcelo a {target}.\n" +
                "Responde estrictamente con este formato y no añadas nada más:\n" +
                TextMarker + "\n<el texto original, con los saltos de línea conservados>\n" +
                TranslationMarker + "\n<la traducción a {target}>",
            _ =>
                "Read every piece of text visible in this image exactly as printed, then translate it into {target}.\n" +
                "Reply strictly in this format and add nothing else:\n" +
                TextMarker + "\n<the original text, line breaks preserved>\n" +
                TranslationMarker + "\n<the translation into {target}>"
        };

        /// <summary>Промты по умолчанию на языке интерфейса.</summary>
        public static List<PromptPreset> DefaultPrompts() => DefaultPrompts(Loc.Language);

        /// <summary>Промты по умолчанию на заданном языке.</summary>
        public static List<PromptPreset> DefaultPrompts(Loc.AppLanguage lang) => lang switch
        {
            Loc.AppLanguage.Russian => RussianPrompts(),
            Loc.AppLanguage.Spanish => SpanishPrompts(),
            _ => EnglishPrompts()
        };

        private static List<PromptPreset> EnglishPrompts() => new()
        {
            new PromptPreset
            {
                Name = "Natural translation",
                Text = "You are a professional translator. Translate the text below into {target}. " +
                       "Keep the original line breaks. Preserve names, numbers, URLs and code as they are. " +
                       "Output only the translation: no comments, no quotes, no explanations.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Literal translation",
                Text = "Translate the text below into {target} as literally as possible, " +
                       "keeping sentence structure and word order where the target language allows it. " +
                       "Output only the translation.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "OCR cleanup + translation",
                Text = "The text below came from OCR and may contain recognition errors, broken words " +
                       "and stray characters. First silently fix the obvious OCR damage, then translate the " +
                       "repaired text into {target}. Output only the translation.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Technical / documentation",
                Text = "Translate the technical text below into {target}. Keep established technical " +
                       "terminology, do not translate identifiers, file names, code, CLI flags or UI labels " +
                       "in quotes. Output only the translation.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Original + translation side by side",
                Text = "For the text below, output the original line, then its translation into {target} " +
                       "on the next line, for every line. Separate pairs with a blank line. " +
                       "Output nothing else.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Explain, don't just translate",
                Text = "Translate the text below into {target}, then add a short section titled \"Notes\" " +
                       "explaining idioms, slang or ambiguous places in {target}. Keep it brief.\n\n{text}"
            }
        };

        private static List<PromptPreset> RussianPrompts() => new()
        {
            new PromptPreset
            {
                Name = "Естественный перевод",
                Text = "Ты профессиональный переводчик. Переведи текст ниже на {target}. " +
                       "Сохрани исходные переносы строк. Имена, числа, ссылки и код оставь как есть. " +
                       "Выведи только перевод: без комментариев, кавычек и пояснений.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Дословный перевод",
                Text = "Переведи текст ниже на {target} максимально дословно, " +
                       "сохраняя структуру предложений и порядок слов, если это допустимо в языке перевода. " +
                       "Выведи только перевод.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Чистка OCR + перевод",
                Text = "Текст ниже получен распознаванием и может содержать ошибки, разорванные слова " +
                       "и лишние символы. Сначала молча исправь явные повреждения распознавания, затем переведи " +
                       "восстановленный текст на {target}. Выведи только перевод.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Технический / документация",
                Text = "Переведи технический текст ниже на {target}. Соблюдай принятую " +
                       "терминологию, не переводи идентификаторы, имена файлов, код, ключи командной строки " +
                       "и названия элементов интерфейса в кавычках. Выведи только перевод.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Оригинал + перевод рядом",
                Text = "Для текста ниже выведи исходную строку, а на следующей строке - её перевод " +
                       "на {target}, и так для каждой строки. Пары разделяй пустой строкой. " +
                       "Больше ничего не выводи.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Перевести и пояснить",
                Text = "Переведи текст ниже на {target}, затем добавь короткий раздел «Примечания» " +
                       "с пояснением идиом, сленга и спорных мест на {target}. Кратко.\n\n{text}"
            }
        };

        private static List<PromptPreset> SpanishPrompts() => new()
        {
            new PromptPreset
            {
                Name = "Traducción natural",
                Text = "Eres un traductor profesional. Traduce el texto de abajo a {target}. " +
                       "Conserva los saltos de línea originales. Deja nombres, números, URLs y código tal cual. " +
                       "Devuelve solo la traducción: sin comentarios, sin comillas, sin explicaciones.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Traducción literal",
                Text = "Traduce el texto de abajo a {target} de la forma más literal posible, " +
                       "conservando la estructura de las frases y el orden de las palabras cuando el idioma lo permita. " +
                       "Devuelve solo la traducción.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Limpieza de OCR + traducción",
                Text = "El texto de abajo proviene de un OCR y puede contener errores de reconocimiento, " +
                       "palabras partidas y caracteres sueltos. Primero corrige en silencio los daños evidentes del OCR " +
                       "y luego traduce el texto reparado a {target}. Devuelve solo la traducción.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Técnico / documentación",
                Text = "Traduce el texto técnico de abajo a {target}. Mantén la terminología técnica " +
                       "establecida, no traduzcas identificadores, nombres de archivo, código, opciones de línea de " +
                       "comandos ni etiquetas de interfaz entre comillas. Devuelve solo la traducción.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Original + traducción en paralelo",
                Text = "Para el texto de abajo, devuelve la línea original y, en la línea siguiente, su " +
                       "traducción a {target}, para cada línea. Separa los pares con una línea en blanco. " +
                       "No devuelvas nada más.\n\n{text}"
            },
            new PromptPreset
            {
                Name = "Traducir y explicar",
                Text = "Traduce el texto de abajo a {target} y añade después una sección breve titulada \"Notas\" " +
                       "que explique modismos, argot o pasajes ambiguos en {target}. Sé breve.\n\n{text}"
            }
        };

        /// <summary>Нормализует промт перед сравнением: поле ввода WPF переписывает переносы строк.</summary>
        internal static string NormalizePrompt(string? s) =>
            (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        /// <summary>Совпадает ли пресет с встроенным промтом на любом из языков.</summary>
        internal static bool TryMatchDefaultPrompt(PromptPreset preset, out int index)
        {
            index = -1;
            if (preset == null) return false;

            string text = NormalizePrompt(preset.Text);
            if (text.Length == 0) return false;

            foreach (var lang in AllLanguages)
            {
                var defaults = DefaultPrompts(lang);
                for (int i = 0; i < defaults.Count; i++)
                {
                    if (NormalizePrompt(defaults[i].Text) == text)
                    {
                        index = i;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Совпадает ли промт распознавания с встроенным (на любом из языков).</summary>
        internal static bool IsDefaultImageOcrPrompt(string? text)
        {
            string value = NormalizePrompt(text);
            return value.Length > 0 &&
                   AllLanguages.Any(l => NormalizePrompt(DefaultImageOcrPromptFor(l)) == value);
        }

        /// <summary>Совпадает ли промт «распознать и перевести» с встроенным (на любом из языков).</summary>
        internal static bool IsDefaultImageOcrTranslatePrompt(string? text)
        {
            string value = NormalizePrompt(text);
            return value.Length > 0 &&
                   AllLanguages.Any(l => NormalizePrompt(DefaultImageOcrTranslatePromptFor(l)) == value);
        }

        /// <summary>Подставляет {text}/{target}/{source} в шаблон промта.</summary>
        public string BuildPrompt(string template, string text, string targetName, string sourceName)
        {
            string result = string.IsNullOrWhiteSpace(template)
                ? DefaultPrompts()[0].Text
                : template;

            result = result
                .Replace("{target}", targetName, StringComparison.OrdinalIgnoreCase)
                .Replace("{source}", string.IsNullOrWhiteSpace(sourceName) ? "the source language" : sourceName,
                    StringComparison.OrdinalIgnoreCase);

            // Если в шаблоне забыли {text} — добавляем текст в конец, иначе запрос уйдёт пустым
            if (result.Contains("{text}", StringComparison.OrdinalIgnoreCase))
                result = result.Replace("{text}", text, StringComparison.OrdinalIgnoreCase);
            else if (!string.IsNullOrEmpty(text))
                result = result.TrimEnd() + "\n\n" + text;

            return result;
        }

        public TranslationSettings Clone()
        {
            var json = JsonSerializer.Serialize(this, TranslationConfig.JsonOptions);
            var copy = JsonSerializer.Deserialize<TranslationSettings>(json, TranslationConfig.JsonOptions)
                       ?? CreateDefault();

            // Ключи помечены JsonIgnore и через JSON не проходят — переносим вручную
            copy.DeepLKey = DeepLKey;
            copy.GoogleKey = GoogleKey;
            copy.QwenKey = QwenKey;
            copy.DeepLKeyProtected = null;
            copy.GoogleKeyProtected = null;
            copy.QwenKeyProtected = null;
            copy.LegacyDeepLKey = null;
            copy.LegacyGoogleKey = null;
            copy.LegacyQwenKey = null;
            return copy;
        }

        /// <summary>Готовит объект к записи: ключи шифруются, открытые копии не сериализуются.</summary>
        internal void ProtectKeysForDisk()
        {
            DeepLKeyProtected = SecretProtector.Protect(DeepLKey);
            GoogleKeyProtected = SecretProtector.Protect(GoogleKey);
            QwenKeyProtected = SecretProtector.Protect(QwenKey);
            LegacyDeepLKey = null;
            LegacyGoogleKey = null;
            LegacyQwenKey = null;
        }

        /// <summary>
        /// Восстанавливает ключи после чтения файла. Если файл от старой версии
        /// и ключи в нём открытым текстом — выставляет migratedFromPlainText,
        /// чтобы вызывающий код тут же перезаписал файл зашифрованным.
        /// </summary>
        internal void UnprotectKeysFromDisk(out bool migratedFromPlainText)
        {
            migratedFromPlainText = false;

            DeepLKey = SecretProtector.Unprotect(DeepLKeyProtected) ?? "";
            GoogleKey = SecretProtector.Unprotect(GoogleKeyProtected) ?? "";
            QwenKey = SecretProtector.Unprotect(QwenKeyProtected) ?? "";

            if (DeepLKey.Length == 0 && !string.IsNullOrWhiteSpace(LegacyDeepLKey))
            {
                DeepLKey = LegacyDeepLKey.Trim();
                migratedFromPlainText = true;
            }

            if (GoogleKey.Length == 0 && !string.IsNullOrWhiteSpace(LegacyGoogleKey))
            {
                GoogleKey = LegacyGoogleKey.Trim();
                migratedFromPlainText = true;
            }

            if (QwenKey.Length == 0 && !string.IsNullOrWhiteSpace(LegacyQwenKey))
            {
                QwenKey = LegacyQwenKey.Trim();
                migratedFromPlainText = true;
            }

            DeepLKeyProtected = null;
            GoogleKeyProtected = null;
            QwenKeyProtected = null;
            LegacyDeepLKey = null;
            LegacyGoogleKey = null;
            LegacyQwenKey = null;
        }

        /// <summary>Дозаполняет пустые списки/строки, чтобы интерфейс никогда не остался без промтов.</summary>
        public void EnsureValid()
        {
            if (Prompts == null || Prompts.Count == 0) Prompts = DefaultPrompts();
            if (string.IsNullOrWhiteSpace(ImageOcrPrompt)) ImageOcrPrompt = DefaultImageOcrPrompt;
            if (string.IsNullOrWhiteSpace(ImageOcrTranslatePrompt)) ImageOcrTranslatePrompt = DefaultImageOcrTranslatePrompt;
            if (string.IsNullOrWhiteSpace(TargetLanguage)) TargetLanguage = "RU";
            if (string.IsNullOrWhiteSpace(GeminiModel)) GeminiModel = "gemini-2.5-flash";
            if (string.IsNullOrWhiteSpace(QwenModel)) QwenModel = "qwen-plus";
            if (string.IsNullOrWhiteSpace(QwenVisionModel)) QwenVisionModel = "qwen-vl-max";
            QwenBaseUrl = (QwenBaseUrl ?? "").Trim();
            SelectedPromptIndex = Math.Clamp(SelectedPromptIndex, 0, Prompts.Count - 1);
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 600);
            MaxImageSide = Math.Clamp(MaxImageSide, 512, 4096);
            GeminiTemperature = Math.Clamp(GeminiTemperature, 0, 2);
            QwenTemperature = Math.Clamp(QwenTemperature, 0, 2);
        }
    }

    /// <summary>
    /// Текущие настройки перевода на время работы приложения + необязательное
    /// сохранение в файл рядом с exe (открытым текстом, только по галочке).
    /// </summary>
    public static class TranslationConfig
    {
        public const string FileName = "translation-settings.json";

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly object SettingsDiskGate = new();
        private static TranslationSettings _current = TranslationSettings.CreateDefault();

        static TranslationConfig()
        {
            // Промты по умолчанию следуют за языком интерфейса
            Loc.LanguageChanged += SyncDefaultPromptsWithAppLanguage;
        }

        public static TranslationSettings Current
        {
            get => _current;
            set
            {
                var previous = _current;

                _current = value ?? TranslationSettings.CreateDefault();
                _current.EnsureValid();

                // Пользователь снова включил подтверждение — согласия, выданные пока
                // оно было выключено, не должны молча продолжать действовать
                if ((_current.AskBeforeNetwork && previous is { AskBeforeNetwork: false }) ||
                    (previous.QwenAllowLoopbackHttp && !_current.QwenAllowLoopbackHttp))
                    NetworkConsent.RevokeAll();

                Changed?.Invoke();
            }
        }

        /// <summary>Срабатывает после применения новых настроек.</summary>
        public static event Action? Changed;

        /// <summary>
        /// Переводит на новый язык интерфейса те промты, которые пользователь не менял:
        /// текст сравнивается с встроенными промтами всех языков, свои промты остаются как есть.
        /// </summary>
        internal static void SyncDefaultPromptsWithAppLanguage()
        {
            var settings = _current;
            if (settings == null) return;

            bool changed = false;
            var defaults = TranslationSettings.DefaultPrompts();

            if (settings.Prompts != null)
            {
                foreach (var preset in settings.Prompts)
                {
                    if (!TranslationSettings.TryMatchDefaultPrompt(preset, out int index)) continue;
                    if (index < 0 || index >= defaults.Count) continue;

                    var fresh = defaults[index];
                    if (preset.Name == fresh.Name && preset.Text == fresh.Text) continue;

                    preset.Name = fresh.Name;
                    preset.Text = fresh.Text;
                    changed = true;
                }
            }

            if (TranslationSettings.IsDefaultImageOcrPrompt(settings.ImageOcrPrompt) &&
                settings.ImageOcrPrompt != TranslationSettings.DefaultImageOcrPrompt)
            {
                settings.ImageOcrPrompt = TranslationSettings.DefaultImageOcrPrompt;
                changed = true;
            }

            if (TranslationSettings.IsDefaultImageOcrTranslatePrompt(settings.ImageOcrTranslatePrompt) &&
                settings.ImageOcrTranslatePrompt != TranslationSettings.DefaultImageOcrTranslatePrompt)
            {
                settings.ImageOcrTranslatePrompt = TranslationSettings.DefaultImageOcrTranslatePrompt;
                changed = true;
            }

            if (!changed) return;

            // Файл на диске трогаем только если сохранение уже разрешено пользователем
            if (settings.PersistToDisk)
            {
                try { SaveOrDelete(); } catch (Exception ex) { AppLog.Warn("TranslationConfig.SaveOrDelete", ex); }
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// Единственное место хранения — профиль пользователя.
        ///
        /// Папка рядом с exe (AppContext.BaseDirectory) больше не используется: она одна
        /// на всех пользователей машины, часто лежит в синхронизируемой папке и уезжает
        /// целиком в архив вместе с приложением. Файл, оставшийся там от старых
        /// версий, переносится сюда и удаляется — см. MigrateLegacyFile.
        /// </summary>
        public static string SettingsFilePath => Path.Combine(SettingsDirectory, FileName);

        private static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoMusicViewer");

        /// <summary>Где файл лежал раньше. Нужен только для миграции и удаления.</summary>
        private static string LegacyFilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        public static bool SettingsFileExists
        {
            get { try { return File.Exists(SettingsFilePath); } catch (Exception ex) { AppLog.Debug("TranslationConfig.SettingsFileExists", ex); return false; } }
        }

        /// <summary>Читает файл настроек, если он есть. Ошибки игнорируются — остаются значения по умолчанию.</summary>
        public static void LoadFromDiskIfPresent()
        {
            try
            {
                MigrateLegacyFile();

                var path = SettingsFilePath;
                if (!File.Exists(path)) return;

                var loaded = JsonSerializer.Deserialize<TranslationSettings>(
                    File.ReadAllText(path), JsonOptions);
                if (loaded == null) return;

                loaded.UnprotectKeysFromDisk(out bool migratedFromPlainText);
                loaded.EnsureValid();
                _current = loaded;

                // Раньше здесь стояло PersistToDisk = true: сам факт наличия файла
                // включал сохранение, даже если внутри было записано обратное. Теперь
                // решает записанное значение, а противоречащий ему файл убирается.
                if (!loaded.PersistToDisk)
                {
                    AppLog.Info("TranslationConfig.LoadFromDisk", null,
                        "файл настроек при выключенном сохранении — удаляю");
                    DeleteFile();
                    return;
                }

                // Файл от старой версии с открытыми ключами — перезаписываем зашифрованным
                if (migratedFromPlainText)
                {
                    try { SaveOrDelete(); }
                    catch (Exception ex) { AppLog.Warn("TranslationConfig.ReencryptKeys", ex); }
                }
            }
            catch (Exception ex)
            {
                // битый/недоступный файл — работаем на значениях по умолчанию, без окон
                AppLog.Warn("TranslationConfig.LoadFromDisk", ex);
            }
        }

        /// <summary>
        /// Сохраняет настройки, если включено PersistToDisk, иначе удаляет файл
        /// (выключение галочки должно убирать ключи с диска, а не оставлять их).
        /// Возвращает описание результата или null, если делать было нечего.
        /// </summary>
        public static string? SaveOrDelete()
        {
            lock (SettingsDiskGate) return SaveOrDeleteCore();
        }
        private static string? SaveOrDeleteCore()
        {
            if (!Current.PersistToDisk)
            {
                DeleteFile();
                return null;
            }

            // Шифруем ключи в копии: у объекта в памяти они должны остаться открытыми
            var forDisk = Current.Clone();
            forDisk.ProtectKeysForDisk();

            var json = JsonSerializer.Serialize(forDisk, JsonOptions);
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

            var path = SettingsFilePath;
            Directory.CreateDirectory(SettingsDirectory);

            // Та же атомарная запись, что и для фотографий: временный файл рядом,
            // сброс на диск, замена одной операцией. Оборванная запись больше не оставляет
            // пустой или наполовину записанный файл настроек.
            SafeFileReplace.WriteThenReplace(path, stream => stream.Write(bytes, 0, bytes.Length));

            return path;
        }

        public static void DeleteFile()
        {
            lock (SettingsDiskGate)
            {
                Current.PersistToDisk = false; // Последующий SaveOrDelete не создаст файл заново.
                var failures = new List<Exception>();
                try { SettingsArtifacts.Clean(SettingsDirectory, FileName); }
                catch (Exception ex) { failures.Add(ex); }
                // Миграционный оригинал удаляется только по точному старому пути;
                // соседние файлы в каталоге EXE не сканируются.
                try
                {
                    var attributes = File.GetAttributes(LegacyFilePath);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new IOException("Legacy settings file is a link or directory.");
                    File.Delete(LegacyFilePath);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) { failures.Add(ex); }
                if (failures.Count > 0) throw new AggregateException(Loc.T(
                    "Some saved settings could not be deleted. Check file permissions or locks.",
                    "Часть сохранённых настроек удалить не удалось. Проверьте права доступа и блокировки файлов.",
                    "No se pudieron eliminar algunos ajustes. Compruebe permisos y bloqueos."), failures);
            }
        }

        /// <summary>
        /// Переносит файл настроек из папки приложения в профиль пользователя и удаляет оригинал.
        /// Нужно один раз после обновления: до этой версии ключи лежали рядом с exe
        /// и открытым текстом.
        /// </summary>
        private static void MigrateLegacyFile()
        {
            string legacy;
            try
            {
                legacy = LegacyFilePath;
                if (!File.Exists(legacy)) return;
            }
            catch (Exception ex)
            {
                AppLog.Debug("TranslationConfig.MigrateLegacyFile", ex);
                return;
            }

            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                    File.Copy(legacy, SettingsFilePath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                // Копия не удалась — исходник не трогаем, иначе потеряем настройки
                AppLog.Warn("TranslationConfig.MigrateLegacyFile", ex);
                return;
            }

            try { File.Delete(legacy); }
            catch (Exception ex) { AppLog.Warn("TranslationConfig.MigrateLegacyFile удаление", ex); }
        }
    }

    /// <summary>Список языков и сопоставление кодов между DeepL, Google и Gemini.</summary>
    public sealed record LanguageInfo(string Code, string EnglishName, string GoogleCode)
    {
        public string Display => $"{EnglishName}  ({Code})";
        public override string ToString() => Display;
    }

    public static class TranslationLanguages
    {
        public static readonly IReadOnlyList<LanguageInfo> All = new List<LanguageInfo>
        {
            new("RU", "Russian", "ru"),
            new("EN-US", "English (US)", "en"),
            new("EN-GB", "English (UK)", "en"),
            new("ES", "Spanish", "es"),
            new("DE", "German", "de"),
            new("FR", "French", "fr"),
            new("IT", "Italian", "it"),
            new("PT-BR", "Portuguese (Brazil)", "pt-BR"),
            new("PT-PT", "Portuguese (Portugal)", "pt-PT"),
            new("NL", "Dutch", "nl"),
            new("PL", "Polish", "pl"),
            new("UK", "Ukrainian", "uk"),
            new("CS", "Czech", "cs"),
            new("SK", "Slovak", "sk"),
            new("SL", "Slovenian", "sl"),
            new("BG", "Bulgarian", "bg"),
            new("RO", "Romanian", "ro"),
            new("HU", "Hungarian", "hu"),
            new("EL", "Greek", "el"),
            new("TR", "Turkish", "tr"),
            new("SV", "Swedish", "sv"),
            new("DA", "Danish", "da"),
            new("FI", "Finnish", "fi"),
            new("NB", "Norwegian", "no"),
            new("ET", "Estonian", "et"),
            new("LV", "Latvian", "lv"),
            new("LT", "Lithuanian", "lt"),
            new("AR", "Arabic", "ar"),
            new("HE", "Hebrew", "he"),
            new("JA", "Japanese", "ja"),
            new("KO", "Korean", "ko"),
            new("ZH", "Chinese (Simplified)", "zh-CN"),
            new("ZH-HANT", "Chinese (Traditional)", "zh-TW"),
            new("ID", "Indonesian", "id"),
            new("TH", "Thai", "th"),
            new("VI", "Vietnamese", "vi"),
            new("HI", "Hindi", "hi"),
            new("KA", "Georgian", "ka"),
            new("KK", "Kazakh", "kk")
        };

        public static LanguageInfo? Find(string? code) =>
            string.IsNullOrWhiteSpace(code)
                ? null
                : All.FirstOrDefault(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Название языка словами — для промтов Gemini (LLM понимает его лучше, чем код).</summary>
        public static string EnglishName(string? code) => Find(code)?.EnglishName ?? (code ?? "").Trim();

        /// <summary>Код для Cloud Translation v2.</summary>
        public static string GoogleCode(string? code)
        {
            var found = Find(code);
            if (found != null) return found.GoogleCode;
            return (code ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>target_lang для DeepL (регион допустим).</summary>
        public static string DeepLTarget(string? code) => (code ?? "RU").Trim().ToUpperInvariant();

        /// <summary>source_lang для DeepL: регион не допускается, поэтому обрезаем его.</summary>
        public static string DeepLSource(string? code)
        {
            var c = (code ?? "").Trim().ToUpperInvariant();
            int dash = c.IndexOf('-');
            return dash > 0 ? c.Substring(0, dash) : c;
        }
    }
}
