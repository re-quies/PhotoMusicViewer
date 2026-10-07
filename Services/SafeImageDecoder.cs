using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Чтение файла для пересохранения (обрезка, поворот, удаление метаданных).
    ///
    /// Зачем нужен отдельный слой:
    /// BitmapCacheOption.OnLoad заставляет WIC разобрать не только пиксели, но и
    /// все метаданные файла. У части снимков EXIF записан с нарушением
    /// спецификации (классика — тег Compression у встроенной миниатюры хранится
    /// как LONG вместо SHORT; так делают старые сборки libjpeg/gd, мессенджеры,
    /// некоторые сканеры и «оптимизаторы» картинок). В этом случае декодер бросает
    ///     System.IO.FileFormatException: «Непредвиденный тип или значение свойства»
    ///     -> COMException 0x88982F8E (WINCODEC_ERR_PROPERTYUNEXPECTEDTYPE)
    /// хотя сами пиксели читаются нормально — поэтому фото открывается и
    /// показывается, а обрезка/поворот/очистка метаданных падают.
    ///
    /// Решение: при таком сбое перечитываем файл без кэширования метаданных
    /// (BitmapCacheOption.None) и копируем в память только пиксели.
    /// </summary>
    internal static class SafeImageDecoder
    {
        /// <summary>
        /// Возвращает пиксели файла с уже применённой EXIF-ориентацией — то есть
        /// ровно то, что пользователь видит на экране. Результат заморожен.
        /// </summary>
        public static BitmapSource LoadOriented(string path)
        {
            byte[] fileBytes = File.ReadAllBytes(path);

            // Обычный путь: OnLoad кэширует и пиксели, и метаданные
            try
            {
                using var ms = new MemoryStream(fileBytes);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                return Orient(frame, ExifOrientationService.GetOrientation(frame));
            }
            catch (Exception ex) when (IsBrokenMetadata(ex))
            {
                // Метаданные битые — не считаем файл нечитаемым, пробуем без них
                AppLog.Warn("SafeImageDecoder.MetadataFallback", ex, AppLog.Describe(path));
            }

            // Запасной путь: метаданные не разбираются вообще
            using (var ms = new MemoryStream(fileBytes))
            {
                var fallbackOptions = BitmapCreateOptions.PreservePixelFormat |
                                      BitmapCreateOptions.IgnoreColorProfile;
                var decoder = BitmapDecoder.Create(ms, fallbackOptions, BitmapCacheOption.None);
                var frame = decoder.Frames[0];

                // Важно: без кэша кадр читает данные лениво, поэтому пиксели
                // копируем в память, пока поток ещё открыт
                var pixels = new CachedBitmap(frame, fallbackOptions, BitmapCacheOption.OnLoad);
                if (pixels.CanFreeze) pixels.Freeze();

                // GetOrientation сам глушит ошибки: если EXIF нечитаем, вернётся 1
                return Orient(pixels, ExifOrientationService.GetOrientation(frame));
            }
        }

        /// <summary>
        /// Декодирует картинку для показа (с уменьшением внутри декодера, чтобы лишние
        /// пиксели не выделялись в памяти). decodePixelWidth / decodePixelHeight: задайте
        /// только одну сторону, вторая посчитается сама; 0 — не ограничивать.
        ///
        /// Почему не просто BitmapImage: при BitmapCacheOption.OnLoad WPF сразу читает
        /// цветовые профили файла (BitmapFrameDecode.ColorContexts). Если ICC-профиль
        /// в файле повреждён, вылетает
        ///     ArgumentException: «Значение не попадает в ожидаемый диапазон»
        ///     в ColorContext.GetColorContextsHelper
        /// хотя сами пиксели в порядке. Поэтому порядок такой:
        ///   1) обычное чтение (цвета как в файле);
        ///   2) то же, но с BitmapCreateOptions.IgnoreColorProfile;
        ///   3) BitmapDecoder без кэша метаданных + масштабирование вручную.
        /// Результат заморожен и годится для передачи между потоками.
        /// </summary>
        public static BitmapSource DecodeScaled(byte[] fileBytes, int decodePixelWidth, int decodePixelHeight)
        {
            try
            {
                return CreateBitmapImage(fileBytes, decodePixelWidth, decodePixelHeight,
                    BitmapCreateOptions.None);
            }
            catch (Exception ex) when (IsBrokenMetadata(ex))
            {
                // Профиль или метаданные битые — пиксели при этом обычно читаются нормально
                AppLog.Warn("SafeImageDecoder.ColorProfileFallback", ex);
            }

            try
            {
                return CreateBitmapImage(fileBytes, decodePixelWidth, decodePixelHeight,
                    BitmapCreateOptions.IgnoreColorProfile);
            }
            catch (Exception ex) when (IsBrokenMetadata(ex))
            {
                AppLog.Warn("SafeImageDecoder.DecoderFallback", ex);
            }

            // Последний шанс: декодер без кэша, пиксели копируем в память сами
            using var ms = new MemoryStream(fileBytes);
            var options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            var decoder = BitmapDecoder.Create(ms, options, BitmapCacheOption.None);
            BitmapSource frame = decoder.Frames[0];

            double scale = 1.0;
            if (decodePixelWidth > 0 && frame.PixelWidth > decodePixelWidth)
                scale = (double)decodePixelWidth / frame.PixelWidth;
            else if (decodePixelHeight > 0 && frame.PixelHeight > decodePixelHeight)
                scale = (double)decodePixelHeight / frame.PixelHeight;

            if (scale < 1.0)
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));

            // Пока поток открыт, данные нужно вычитать — иначе кадр читал бы его лениво
            var pixels = new CachedBitmap(frame, options, BitmapCacheOption.OnLoad);
            if (pixels.CanFreeze) pixels.Freeze();
            return pixels;
        }

        private static BitmapImage CreateBitmapImage(byte[] fileBytes, int decodePixelWidth,
            int decodePixelHeight, BitmapCreateOptions createOptions)
        {
            using var ms = new MemoryStream(fileBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = createOptions;
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            else if (decodePixelHeight > 0) bmp.DecodePixelHeight = decodePixelHeight;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static BitmapSource Orient(BitmapSource source, int orientation)
        {
            var oriented = ExifOrientationService.ApplyOrientation(source, orientation);
            if (oriented.CanFreeze) oriented.Freeze();
            return oriented;
        }

        /// <summary>
        /// true, если сбой относится к разбору метаданных, а не к самим пикселям.
        /// Только в этом случае имеет смысл повторное чтение.
        /// </summary>
        public static bool IsBrokenMetadata(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                // WPF заворачивает ошибки WIC именно в FileFormatException
                if (e is FileFormatException) return true;

                // Повреждённый ICC-профиль: ArgumentException из ColorContext.GetColorContextsHelper
                if (e is ArgumentException &&
                    (e.StackTrace?.Contains("ColorContext", StringComparison.Ordinal) ?? false))
                    return true;

                if (e is COMException com)
                {
                    switch ((uint)com.HResult)
                    {
                        case 0x88982F8E: // PROPERTYUNEXPECTEDTYPE — «непредвиденный тип свойства»
                        case 0x88982F91: // UNEXPECTEDMETADATATYPE
                        case 0x88982F8D: // DUPLICATEMETADATAPRESENT
                        case 0x88982F63: // BADMETADATAHEADER
                        case 0x88982F52: // TOOMUCHMETADATA
                        case 0x88982F42: // PROPERTYSIZE
                        case 0x88982F90: // INVALIDQUERYREQUEST
                            return true;
                    }
                }
            }

            return false;
        }
    }
}
