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

            // Метаданные при пересохранении не переносятся (так задумано ради приватности),
            // поэтому тег EXIF-ориентации пропадёт — «запекаем» его в пиксели до поворота.
            // SafeImageDecoder дополнительно защищает от файлов с битым EXIF.
            var source = SafeImageDecoder.LoadOriented(path);

            var rotated = new TransformedBitmap(source,
                new System.Windows.Media.RotateTransform(NormalizeDegrees(degrees)));
            if (rotated.CanFreeze) rotated.Freeze();

            encoder.Frames.Add(BitmapFrame.Create(rotated));

            // Запись во временный файл рядом с оригиналом и замена одной операцией
            // файловой системы: при любом сбое на месте останется целый файл
            SafeFileReplace.WriteThenReplace(path, stream => encoder.Save(stream));
        }

        private static double NormalizeDegrees(int degrees)
        {
            int d = degrees % 360;
            return d < 0 ? d + 360 : d;
        }
    }
}
