using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    public static class RotateAndSaveService
    {
        public static void RotateAndSave(string path, int degrees)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            BitmapEncoder encoder = ext switch
            {
                // QualityLevel 95: без этого JPEG пережимался с качеством 75 при каждом повороте
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

            // Метаданные при пересохранении не переносятся (так задумано ради приватности),
            // поэтому тег EXIF-ориентации пропадёт — "запекаем" его в пиксели до поворота
            var source = ExifOrientationService.ApplyOrientation(
                original, ExifOrientationService.GetOrientation(original));

            var rotated = new TransformedBitmap(source,
                new System.Windows.Media.RotateTransform(NormalizeDegrees(degrees)));

            encoder.Frames.Add(BitmapFrame.Create(rotated));

            var tempPath = path + ".tmp";
            using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(outStream);
            }

            File.Delete(path);
            File.Move(tempPath, path);
        }

        private static double NormalizeDegrees(int degrees)
        {
            int d = degrees % 360;
            return d < 0 ? d + 360 : d;
        }
    }
}
