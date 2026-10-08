using System;
using System.Security.Cryptography;
using System.Text;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Шифрование секретов (ключей API) через DPAPI — CryptProtectData с областью
    /// CurrentUser. Расшифровать результат может только та же учётная запись Windows
    /// на этой же машине: файл настроек, скопированный другому пользователю, попавший
    /// в резервную копию или в синхронизируемую папку, ключей уже не выдаст.
    ///
    /// Отдельная соль (entropy) привязывает шифротекст к этому приложению, чтобы чужая
    /// программа не расшифровала его случайным вызовом CryptUnprotectData без соли.
    ///
    /// Чего это НЕ даёт: защиты от вредоносного кода, запущенного под той же учётной
    /// записью — он может вызвать DPAPI сам. Ровно это и написано в подсказке к галочке
    /// сохранения; обещать больше было бы нечестно.
    /// </summary>
    internal static class SecretProtector
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("PhotoMusicViewer.TranslationKeys.v1");

        /// <summary>
        /// Шифрует строку. Возвращает null, если шифровать нечего или DPAPI недоступен —
        /// записывать ключ открытым текстом «на всякий случай» нельзя, это ровно то,
        /// от чего мы уходим.
        /// </summary>
        public static string? Protect(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            try
            {
                var cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            catch (Exception ex)
            {
                AppLog.Warn("SecretProtector.Protect", ex);
                return null;
            }
        }

        /// <summary>
        /// Расшифровывает строку. Возвращает null для пустого, чужого или испорченного
        /// значения — приложение просто продолжает работать без ключа.
        /// </summary>
        public static string? Unprotect(string? protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue)) return null;

            try
            {
                var plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                // Другой пользователь, другая машина, битый файл
                AppLog.Warn("SecretProtector.Unprotect", ex);
                return null;
            }
        }
    }
}
