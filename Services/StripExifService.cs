using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Удаление всех метаданных (EXIF, GPS, данные камеры, ICC и пр.) из файла
    /// путём перекодирования только пикселей с атомарной заменой исходника.
    /// </summary>
    public static class StripExifService
    {
        public static bool CanStrip(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".tiff" or ".tif";
        }

        public static void StripMetadata(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            BitmapEncoder encoder = ext switch
            {
                ".jpg" or ".jpeg" or ".jfif" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".png" => new PngBitmapEncoder(),
                ".bmp" => new BmpBitmapEncoder(),
                ".tiff" or ".tif" => new TiffBitmapEncoder(),
                _ => throw new NotSupportedException($"No encoder available for {ext}")
            };

            // Сначала «запекаем» EXIF-ориентацию в пиксели, иначе после удаления
            // тега фото визуально ляжет на бок. Битые метаданные тут особенно
            // вероятны — именно такие файлы чаще всего и чистят.
            var source = SafeImageDecoder.LoadOriented(path);

            // BitmapFrame.Create(BitmapSource) не переносит метаданные — именно это нам и нужно
            encoder.Frames.Add(BitmapFrame.Create(source));

            // Замена исходника одной операцией файловой системы (без окна,
            // в котором старый файл уже удалён, а новый ещё не записан)
            SafeFileReplace.WriteThenReplace(path, stream => encoder.Save(stream));
        }
    }
}
