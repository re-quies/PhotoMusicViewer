using System;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Простейшая локализация без файлов настроек и реестра:
    /// выбранный язык живёт только в памяти процесса (приватность и простота).
    /// </summary>
    public static class Loc
    {
        public enum AppLanguage { English, Russian, Spanish }

        public static AppLanguage Language { get; private set; } = AppLanguage.English;

        /// <summary>Срабатывает при каждой смене языка.</summary>
        public static event Action? LanguageChanged;

        /// <summary>Код текущего языка для кнопки-переключателя.</summary>
        public static string Code => Language switch
        {
            AppLanguage.Russian => "RU",
            AppLanguage.Spanish => "ES",
            _ => "EN"
        };

        /// <summary>Переключает язык по кругу: EN -> RU -> ES -> EN.</summary>
        public static void Toggle()
        {
            SetLanguage(Language switch
            {
                AppLanguage.English => AppLanguage.Russian,
                AppLanguage.Russian => AppLanguage.Spanish,
                _ => AppLanguage.English
            });
        }

        /// <summary>Устанавливает язык; событие срабатывает только если язык действительно сменился.</summary>
        public static void SetLanguage(AppLanguage language)
        {
            if (Language == language) return;

            Language = language;
            LanguageChanged?.Invoke();
        }

        /// <summary>Язык интерфейса Windows, если он поддерживается (иначе английский).</summary>
        public static AppLanguage DetectSystemLanguage()
        {
            try
            {
                return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
                {
                    "ru" => AppLanguage.Russian,
                    "es" => AppLanguage.Spanish,
                    _ => AppLanguage.English
                };
            }
            catch (Exception ex)
            {
                AppLog.Debug("Loc.DetectSystemLanguage", ex);
                return AppLanguage.English;
            }
        }

        /// <summary>Возвращает строку на текущем языке.</summary>
        public static string T(string en, string ru, string es) => Language switch
        {
            AppLanguage.Russian => ru,
            AppLanguage.Spanish => es,
            _ => en
        };
    }
}
