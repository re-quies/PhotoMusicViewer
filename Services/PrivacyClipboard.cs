using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Единая точка записи в буфер обмена — и для текста, и для картинок.
    ///
    /// Всё, что кладёт приложение, помечается флагами приватности Windows: не попадает
    /// в историю буфера (Win+V), не синхронизируется в облако и не обрабатывается
    /// службами анализа содержимого.
    ///
    /// Правило одно для всех типов данных: скопированное доступно для вставки, пока
    /// приложение открыто, и убирается из буфера при выходе. Раньше текст исчезал
    /// после выхода, а картинка (Clipboard.SetDataObject(data, true)) оставалась.
    /// Если пользователь после нас скопировал что-то в другой программе, при выходе
    /// буфер не трогаем — очищается только наше содержимое.
    /// </summary>
    public static class PrivacyClipboard
    {
        private static readonly string[] PrivacyFormats =
        {
            "CanIncludeInClipboardHistory",
            "CanUploadToCloudClipboard",
            "ExcludeClipboardContentFromMonitorProcessing"
        };

        // Последний объект, который мы положили в буфер. Обращения — только из UI-потока.
        private static DataObject? _owned;

        /// <summary>
        /// Кладёт текст в буфер обмена. Возвращает false, если буфер занят другим приложением.
        /// </summary>
        public static bool TrySetText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                var data = new DataObject();
                data.SetText(text);
                Publish(data);
                return true;
            }
            catch (Exception ex)
            {
                // буфер обмена может быть временно заблокирован другим процессом
                AppLog.Warn("PrivacyClipboard.SetText", ex);
                return false;
            }
        }

        /// <summary>
        /// Кладёт картинку в буфер обмена. Бросает исключение, если буфер занят:
        /// вызывающий код сам решает, повторять ли попытку.
        /// </summary>
        public static void SetImage(BitmapSource image)
        {
            ArgumentNullException.ThrowIfNull(image);
            var data = new DataObject();
            data.SetImage(image);
            Publish(data);
        }

        /// <summary>
        /// Убирает из буфера то, что положило приложение, если оно всё ещё там.
        /// Вызывается при выходе. Никогда не бросает исключений.
        /// </summary>
        public static void ClearOwnedContent()
        {
            var owned = _owned;
            _owned = null;
            if (owned == null) return;

            try
            {
                if (Clipboard.IsCurrent(owned)) Clipboard.Clear();
            }
            catch (Exception ex)
            {
                AppLog.Debug("PrivacyClipboard.ClearOwnedContent", ex);
            }
        }

        internal static void AddPrivacyFlags(DataObject data)
        {
            foreach (var format in PrivacyFormats)
            {
                // Windows ждёт DWORD = 0; в WPF это передаётся как поток из 4 байт
                data.SetData(format, new MemoryStream(BitConverter.GetBytes(0)));
            }
        }

        private static void Publish(DataObject data)
        {
            AddPrivacyFlags(data);
            // copy: false — данные отдаются по запросу, пока приложение работает,
            // и не копируются в буфер насовсем (OleFlushClipboard не вызывается).
            // Clipboard.IsCurrent работает только в этом режиме.
            Clipboard.SetDataObject(data, false);
            _owned = data;
        }
    }
}
