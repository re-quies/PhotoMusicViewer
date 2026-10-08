using System;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Простейшая локализация без реестра. Язык при запуске выбирается в настройках
    /// (AppPreferences: «как в Windows» или конкретный язык, по желанию — последний
    /// выбранный кнопкой EN/RU/ES).
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
            Language = Language switch
            {
                AppLanguage.English => AppLanguage.Russian,
                AppLanguage.Russian => AppLanguage.Spanish,
                _ => AppLanguage.English
            };
            LanguageChanged?.Invoke();
        }

        /// <summary>Включает язык (событие — только если он действительно сменился).</summary>
        public static void SetLanguage(AppLanguage language)
        {
            if (!Enum.IsDefined(language) || Language == language) return;
            Language = language;
            LanguageChanged?.Invoke();
        }

        public static AppLanguage FromStartup(StartupLanguage language) => language switch
        {
            StartupLanguage.Russian => AppLanguage.Russian,
            StartupLanguage.Spanish => AppLanguage.Spanish,
            _ => AppLanguage.English
        };

        public static StartupLanguage ToStartup(AppLanguage language) => language switch
        {
            AppLanguage.Russian => StartupLanguage.Russian,
            AppLanguage.Spanish => StartupLanguage.Spanish,
            _ => StartupLanguage.English
        };

        /// <summary>Язык при запуске по настройкам; «как в Windows» — по языку интерфейса системы.</summary>
        public static AppLanguage ResolveStartup(StartupLanguage language) =>
            FromStartup(AppPreferences.Resolve(language, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName));

        /// <summary>Возвращает строку на текущем языке.</summary>
        public static string T(string en, string ru, string es) => Language switch
        {
            AppLanguage.Russian => ru,
            AppLanguage.Spanish => es,
            _ => en
        };
    }
}
