using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Какой язык интерфейса включать при запуске приложения.
    /// System — язык интерфейса Windows (ru / es, иначе английский).
    /// </summary>
    public enum StartupLanguage { English, Russian, Spanish, System }

    /// <summary>
    /// Общие (не секретные) настройки приложения. Сейчас здесь только язык при запуске.
    ///
    /// Файл app-settings.json лежит отдельно от translation-settings.json и никогда не
    /// содержит ключей API: язык нужно помнить между запусками, а ключи по-прежнему
    /// остаются в памяти, если пользователь не включил их сохранение сам.
    /// Файл ищется рядом с exe, а если папка только для чтения — в
    /// %LOCALAPPDATA%\PhotoMusicViewer.
    /// </summary>
    public static class AppSettings
    {
        public const string FileName = "app-settings.json";

        private sealed class Data
        {
            public StartupLanguage StartupLanguage { get; set; } = StartupLanguage.English;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static string PrimaryPath => Path.Combine(AppContext.BaseDirectory, FileName);

        private static string FallbackPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoMusicViewer", FileName);

        /// <summary>Выбранный язык запуска (по умолчанию английский, как и раньше).</summary>
        public static StartupLanguage Startup { get; private set; } = StartupLanguage.English;

        /// <summary>Читает файл, если он есть. Ошибки игнорируются — остаётся значение по умолчанию.</summary>
        public static void Load()
        {
            foreach (var path in new[] { PrimaryPath, FallbackPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), JsonOptions);
                    if (data == null) continue;

                    Startup = Enum.IsDefined(typeof(StartupLanguage), data.StartupLanguage)
                        ? data.StartupLanguage
                        : StartupLanguage.English;
                    return;
                }
                catch (Exception ex)
                {
                    // битый/недоступный файл — работаем на значении по умолчанию, без окон
                    AppLog.Warn("AppSettings.Load", ex);
                }
            }
        }

        /// <summary>Сохраняет язык запуска. Возвращает false, если записать файл не удалось.</summary>
        public static bool SaveStartupLanguage(StartupLanguage value)
        {
            Startup = value;

            var json = JsonSerializer.Serialize(new Data { StartupLanguage = value }, JsonOptions);

            foreach (var path in new[] { PrimaryPath, FallbackPath })
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, json);

                    // Если раньше файл лежал в другом месте — убираем старую копию,
                    // иначе при чтении она могла бы перекрыть новое значение
                    var other = path == PrimaryPath ? FallbackPath : PrimaryPath;
                    try { if (File.Exists(other)) File.Delete(other); }
                    catch (Exception ex) { AppLog.Debug("AppSettings.DeleteOldCopy", ex); }

                    return true;
                }
                catch (Exception ex)
                {
                    // папка только для чтения (например, Program Files) — пробуем следующий путь
                    AppLog.Debug("AppSettings.Save путь недоступен", ex);
                }
            }

            return false;
        }

        /// <summary>Переводит выбор пользователя в конкретный язык интерфейса.</summary>
        public static Loc.AppLanguage Resolve(StartupLanguage value) => value switch
        {
            StartupLanguage.Russian => Loc.AppLanguage.Russian,
            StartupLanguage.Spanish => Loc.AppLanguage.Spanish,
            StartupLanguage.System => Loc.DetectSystemLanguage(),
            _ => Loc.AppLanguage.English
        };

        /// <summary>Включает язык, выбранный для запуска. Вызывается один раз при старте.</summary>
        public static void ApplyStartupLanguage()
        {
            Load();
            Loc.SetLanguage(Resolve(Startup));
        }
    }
}
