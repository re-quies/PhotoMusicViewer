using System;
using System.Net;

namespace PhotoMusicViewer.Services
{
    public enum TranslationProvider { DeepL, Google, Qwen }
    internal static class NetworkEndpointPolicy
    {
        internal static string NormalizeQwenBase(string? input, bool allowLoopbackHttp = false)
        {
            string value = (input ?? "").Trim().Trim('"', '\'').TrimEnd('/');
            if (value.Length == 0) return "";
            if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw InvalidAddress();
            ValidateTransport(uri, allowLoopbackHttp);
            if (uri.Query.Length != 0 || uri.Fragment.Length != 0) throw InvalidAddress();
            string path = uri.AbsolutePath.TrimEnd('/');
            const string tail = "/chat/completions";
            if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) path = path[..^tail.Length];
            if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/compatible-mode/v1";
            return Origin(uri) + path;
        }
        internal static void ValidateTransport(Uri uri, bool allowLoopbackHttp)
        {
            if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(uri.Host)) throw InvalidAddress();
            if (uri.Scheme == Uri.UriSchemeHttps) return;
            if (uri.Scheme != Uri.UriSchemeHttp || !allowLoopbackHttp || !IsLoopbackHost(uri.IdnHost))
                throw new TranslationException(Loc.T(
                    "Only HTTPS is allowed. HTTP is available only for an explicitly enabled local loopback endpoint.",
                    "Разрешён только HTTPS. HTTP допустим только для локального loopback-адреса при явно включённом исключении.",
                    "Solo se permite HTTPS. HTTP requiere una excepción explícita para una dirección loopback local."));
        }
        private static bool IsLoopbackHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }
        internal static string Origin(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw InvalidAddress();
            ValidateTransport(uri, true); // Transport opt-in is separately enforced by caller/settings.
            return Origin(uri);
        }
        private static string Origin(Uri uri) => new UriBuilder(uri.Scheme, uri.IdnHost.ToLowerInvariant(),
            uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
        private static TranslationException InvalidAddress() => new(Loc.T(
            "Invalid API address. Use an absolute HTTPS URL without credentials, query or fragment.",
            "Некорректный адрес API. Используйте абсолютный HTTPS URL без логина, пароля, параметров или фрагмента.",
            "Dirección de API no válida. Use HTTPS sin credenciales, parámetros ni fragmentos."));
    }
}
