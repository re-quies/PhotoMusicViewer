using System;
using System.Reflection;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Сведения «О программе»: номер версии и ссылка на страницу проекта.
    /// Версия задаётся одним местом — &lt;Version&gt; в PhotoMusicViewer.csproj —
    /// и попадает и в свойства EXE, и в окно настроек.
    /// </summary>
    public static class AppInfo
    {
        public const string GitHubUrl = "https://github.com/re-quies/fastcollageforwin";

        private static readonly Lazy<string> _version = new(() => ReadVersion(Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly));

        /// <summary>Номер версии без служебного хвоста сборки («7.0.0», а не «7.0.0+abc123»).</summary>
        public static string Version => _version.Value;

        internal static string ReadVersion(Assembly assembly)
        {
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return NormalizeVersion(informational) ?? NormalizeVersion(assembly.GetName().Version?.ToString(3)) ?? "?";
        }

        internal static string? NormalizeVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string value = raw.Trim();
            int plus = value.IndexOf('+');
            if (plus >= 0) value = value.Substring(0, plus);
            return value.Length == 0 ? null : value;
        }

        /// <summary>
        /// Только абсолютный https-адрес без логина и пароля: кнопка не может открыть
        /// локальный файл, file://, javascript: или адрес с подставленными учётными данными.
        /// </summary>
        public static bool TryGetSafeLink(string? url, out Uri uri)
        {
            uri = null!;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttps || parsed.IsFile || parsed.IsUnc) return false;
            if (!string.IsNullOrEmpty(parsed.UserInfo) || string.IsNullOrEmpty(parsed.Host)) return false;
            uri = parsed;
            return true;
        }
    }
}
