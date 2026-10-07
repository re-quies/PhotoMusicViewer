using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Журнал диагностики. Нужен, чтобы перехваченная ошибка не исчезала
    /// бесследно: десятки catch-блоков раньше глотали исключения молча.
    ///
    /// Приватность важнее удобства разбора, поэтому:
    /// - журнал выключен по умолчанию и включается галочкой в настройках;
    /// - имён файлов и путей в записях нет: только расширение и размер (Describe);
    /// - текст ответа сервиса перевода не пишется (hideMessage);
    /// - ключи API и любые пути вырезаются из готовой строки (Sanitize);
    /// - журнал никогда не бросает исключение (иначе catch сам станет источником сбоя);
    /// - файл ограничен по размеру и ротируется, занять весь диск он не может.
    ///
    /// Переменная среды PMV_LOG: off - запретить совсем, on - включить,
    /// debug - включить с подробным уровнем.
    ///
    /// Папка: %LOCALAPPDATA%\PhotoMusicViewer\logs
    /// </summary>
    internal static class AppLog
    {
        public enum Level
        {
            /// <summary>Ожидаемый отказ (нет доступа к папке, отмена, нет EXIF).</summary>
            Debug = 0,
            Info = 1,
            /// <summary>Операция не выполнилась, но приложение продолжает работу.</summary>
            Warn = 2,
            /// <summary>Ошибка, которую видит пользователь.</summary>
            Error = 3
        }

        private const long MaxFileBytes = 1024 * 1024;   // 1 МБ, дальше ротация
        private const int MaxEntryChars = 4000;          // стек бывает очень длинным

        private static readonly object Gate = new();
        private static bool _diskUnavailable;            // после первой неудачи больше не пишем на диск

        /// <summary>Папка журнала (пустая строка - определить не удалось).</summary>
        public static string DirectoryPath { get; } = BuildDirectory();

        /// <summary>Файл журнала (пустая строка - запись на диск невозможна).</summary>
        public static string FilePath { get; } =
            DirectoryPath.Length == 0 ? "" : System.IO.Path.Combine(DirectoryPath, "app.log");

        /// <summary>Файл-метка: его наличие значит, что пользователь включил журнал.</summary>
        private static string MarkerPath { get; } =
            DirectoryPath.Length == 0 ? "" : System.IO.Path.Combine(DirectoryPath, "logging-enabled");

        /// <summary>Запись включена. По умолчанию выключена — тишина на диске важнее.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Минимальный уровень записи. PMV_LOG=debug опускает его до Debug.</summary>
        public static Level MinLevel { get; private set; } = Level.Info;

        /// <summary>Файлы журнала есть на диске (есть что открывать и что удалять).</summary>
        public static bool HasLogs => Exists(FilePath) || Exists(FilePath + ".1");

        static AppLog()
        {
            string? mode = null;
            try { mode = Environment.GetEnvironmentVariable("PMV_LOG"); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AppLog: PMV_LOG не прочитан: " + ex.Message); }

            if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                Enabled = false;
            }
            else if (string.Equals(mode, "debug", StringComparison.OrdinalIgnoreCase))
            {
                Enabled = true;
                MinLevel = Level.Debug;
            }
            else if (string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase) || mode == "1")
            {
                Enabled = true;
            }
            else
            {
                // Обычный запуск: журнал есть только если его включили в настройках
                Enabled = Exists(MarkerPath);
            }
        }

        // ------------------------------------------------------------- управление

        /// <summary>
        /// Включает или выключает журнал. Состояние запоминается файлом-меткой,
        /// иначе после перезапуска (а баг часто воспроизводится именно при запуске)
        /// запись снова была бы выключена. Сама метка пустая и данных не содержит.
        /// </summary>
        public static bool SetEnabled(bool on)
        {
            Enabled = on;

            lock (Gate)
            {
                try
                {
                    if (MarkerPath.Length == 0) return false;

                    if (on)
                    {
                        Directory.CreateDirectory(DirectoryPath);
                        File.WriteAllText(MarkerPath, "", Encoding.UTF8);
                        _diskUnavailable = false;
                    }
                    else if (File.Exists(MarkerPath))
                    {
                        File.Delete(MarkerPath);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("AppLog: метку записать не удалось: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>Удаляет файлы журнала (кнопка «Удалить журнал» в настройках).</summary>
        public static bool DeleteLogs()
        {
            lock (Gate)
            {
                try
                {
                    if (Exists(FilePath)) File.Delete(FilePath);
                    if (Exists(FilePath + ".1")) File.Delete(FilePath + ".1");
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("AppLog: журнал не удалён: " + ex.Message);
                    return false;
                }
            }
        }

        // ------------------------------------------------------------- запись

        public static void Debug(string operation, Exception? ex = null, string? detail = null, bool hideMessage = false) =>
            Write(Level.Debug, operation, ex, detail, hideMessage);

        public static void Info(string operation, Exception? ex = null, string? detail = null, bool hideMessage = false) =>
            Write(Level.Info, operation, ex, detail, hideMessage);

        public static void Warn(string operation, Exception? ex = null, string? detail = null, bool hideMessage = false) =>
            Write(Level.Warn, operation, ex, detail, hideMessage);

        public static void Error(string operation, Exception? ex = null, string? detail = null, bool hideMessage = false) =>
            Write(Level.Error, operation, ex, detail, hideMessage);

        /// <summary>
        /// Обезличенное описание файла для detail: расширение и размер, без имени и пути.
        /// Имя файла — это ровно тот след, который приложение в других местах стирает,
        /// а для разбора ошибки хватает типа и размера.
        /// </summary>
        public static string Describe(string? path)
        {
            if (!Enabled || string.IsNullOrEmpty(path)) return "";

            try
            {
                var ext = System.IO.Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) ext = "без расширения";

                var info = new FileInfo(path!);
                return info.Exists ? ext + ", " + FormatSize(info.Length) : ext;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AppLog.Describe: " + ex.Message);
                return "";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        private static void Write(Level level, string operation, Exception? ex, string? detail, bool hideMessage)
        {
            if (!Enabled || level < MinLevel) return;

            string line;
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                sb.Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5));
                sb.Append(' ').Append(operation);

                if (!string.IsNullOrEmpty(detail))
                    sb.Append(" | ").Append(detail);

                if (ex != null)
                {
                    sb.Append(" | ").Append(ex.GetType().Name);

                    // Текст исключения от сервиса перевода может цитировать кусок ответа,
                    // то есть распознанный со снимка текст — тогда пишем только тип
                    if (!hideMessage)
                        sb.Append(": ").Append(ex.Message);

                    if (ex.InnerException != null)
                    {
                        sb.Append(" <- ").Append(ex.InnerException.GetType().Name);
                        if (!hideMessage)
                            sb.Append(": ").Append(ex.InnerException.Message);
                    }

                    // Стек нужен только для настоящих ошибок: у ожидаемых отказов
                    // он только раздувает файл
                    if (level >= Level.Warn && !string.IsNullOrEmpty(ex.StackTrace))
                        sb.Append(Environment.NewLine).Append(Indent(ex.StackTrace!));
                }

                line = Sanitize(sb.ToString());
                if (line.Length > MaxEntryChars)
                    line = line.Substring(0, MaxEntryChars) + "...[обрезано]";
            }
            catch (Exception formatEx)
            {
                line = "LOG FORMAT ERROR: " + formatEx.GetType().Name;
            }

            // В релизной сборке этот вызов вырезает компилятор ([Conditional("DEBUG")]),
            // так что через OutputDebugString ничего не утечёт
            System.Diagnostics.Debug.WriteLine(line);

            if (_diskUnavailable || FilePath.Length == 0) return;

            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    RotateIfNeeded();
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception writeEx)
                {
                    // Единственный оправданный молчаливый catch во всё1м проекте: журнал
                    // не имеет права ломать приложение. Дальше молчим до перезапуска.
                    _diskUnavailable = true;
                    System.Diagnostics.Debug.WriteLine("AppLog отключён: " + writeEx.Message);
                }
            }
        }

        /// <summary>Старый файл сохраняется как app.log.1, так что на диске максимум 2 МБ.</summary>
        private static void RotateIfNeeded()
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxFileBytes) return;

            var previous = FilePath + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(FilePath, previous);
        }

        private static bool Exists(string path)
        {
            try { return path.Length > 0 && File.Exists(path); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AppLog.Exists: " + ex.Message);
                return false;
            }
        }

        private static string Indent(string text) =>
            "    " + text.Replace(Environment.NewLine, Environment.NewLine + "    ");

        private static string BuildDirectory()
        {
            try
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PhotoMusicViewer", "logs");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AppLog: не удалось определить папку журнала: " + ex.Message);
                return "";
            }
        }

        // ------------------------------------------------------------- очистка строки

        // В текст исключения легко попадает URL с ?key=... или заголовок
        // авторизации. Писать чужой ключ в файл на диске нельзя.
        private static readonly (Regex Pattern, string Replacement)[] SecretPatterns =
        {
            (new Regex(@"([?&](?:key|api[_-]?key|access_token|token|auth)=)[^&\s""']+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "$1***"),
            (new Regex(@"AIza[0-9A-Za-z\-_]{10,}", RegexOptions.CultureInvariant), "AIza***"),
            (new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?::fx)?\b",
                RegexOptions.CultureInvariant), "***"),
            (new Regex(@"(Bearer|DeepL-Auth-Key)\s+\S+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "$1 ***")
        };

        // Сообщения IOException всегда цитируют полный путь с именем файла:
        // оставляем только расширение, всё остальное — приватные данные
        private static readonly Regex PathPattern =
            new(@"(?:[A-Za-z]:\\|\\\\)[^\s""'<>|]*", RegexOptions.CultureInvariant);

        private static readonly Regex ExtensionPattern =
            new(@"\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant);

        private static string Sanitize(string text)
        {
            foreach (var (pattern, replacement) in SecretPatterns)
                text = pattern.Replace(text, replacement);

            text = PathPattern.Replace(text, ScrubPath);

            // На случай пути в формате, который не поймала регулярка выше
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 3)
                text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

            return text;
        }

        private static string ScrubPath(Match match)
        {
            // Хвостовая пунктуация принадлежит предложению, а не пути
            var value = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '\'', '"');
            var tail = match.Value.Substring(value.Length);

            var ext = ExtensionPattern.Match(value);
            return "<путь>" + (ext.Success ? ext.Value : "") + tail;
        }
    }
}
