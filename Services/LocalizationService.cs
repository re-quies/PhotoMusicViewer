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
            Language = Language switch
            {
                AppLanguage.English => AppLanguage.Russian,
                AppLanguage.Russian => AppLanguage.Spanish,
                _ => AppLanguage.English
            };
            LanguageChanged?.Invoke();
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
