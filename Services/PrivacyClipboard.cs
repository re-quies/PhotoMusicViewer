using System;
using System.IO;
using System.Windows;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Копирование текста в буфер обмена с флагами приватности Windows:
    /// текст не попадает в историю буфера, не синхронизируется в облако и не
    /// обрабатывается службами анализа содержимого.
    /// Раньше такие флаги ставились только для картинок, а распознанный текст
    /// уходил в буфер открытым через Clipboard.SetText — что было самым чувствительным утечком.
    /// </summary>
    public static class PrivacyClipboard
    {
        private static readonly string[] PrivacyFormats =
        {
            "CanIncludeInClipboardHistory",
            "CanUploadToCloudClipboard",
            "ExcludeClipboardContentFromMonitorProcessing"
        };

        /// <summary>
        /// Кладёт текст в буфер обмена. Возвращает false, если буфер занят другим приложением.
        /// </summary>
        /// <param name="keepAfterExit">
        /// false (по умолчанию) — текст не остаётся в буфере после выхода из приложения.
        /// </param>
        public static bool TrySetText(string text, bool keepAfterExit = false)
        {
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                var data = new DataObject();
                data.SetText(text);

                foreach (var format in PrivacyFormats)
                {
                    // Windows ждёт DWORD = 0; в WPF это передаётся как поток из 4 байт
                    data.SetData(format, new MemoryStream(BitConverter.GetBytes(0)));
                }

                Clipboard.SetDataObject(data, keepAfterExit);
                return true;
            }
            catch (Exception ex)
            {
                // буфер обмена может быть временно заблокирован другим процессом
                AppLog.Warn("PrivacyClipboard.SetText", ex);
                return false;
            }
        }
    }
}
