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

            BitmapFrame original;
            byte[] fileBytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(fileBytes))
            {
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                original = decoder.Frames[0];
            }

            // Сначала "запекаем" EXIF-ориентацию в пиксели,
            // иначе после удаления тега фото визуально ляжет на бок
            var source = ExifOrientationService.ApplyOrientation(
                original, ExifOrientationService.GetOrientation(original));

            // BitmapFrame.Create(BitmapSource) не переносит метаданные — именно это нам и нужно
            encoder.Frames.Add(BitmapFrame.Create(source));

            var tempPath = path + ".tmp";
            using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(outStream);
            }

            File.Delete(path);
            File.Move(tempPath, path);
        }
    }
}
