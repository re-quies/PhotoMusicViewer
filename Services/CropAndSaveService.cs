using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    public static class CropAndSaveService
    {
        /// <summary>
        /// true, если формат файла можно перекодировать «на месте» (заменить исходник).
        /// </summary>
        public static bool CanEncodeInPlace(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".tiff" or ".tif";
        }

        /// <summary>
        /// Обрезает изображение по прямоугольнику (в пикселях с уже применённой
        /// EXIF-ориентацией — как на экране) и сохраняет.
        /// replaceOriginal = true  — атомарно заменяет исходный файл;
        /// replaceOriginal = false — сохраняет копию "имя (cropped).ext".
        /// Для форматов без энкодера (webp/gif/heic/raw) копия сохраняется как PNG.
        /// Возвращает путь к сохранённому файлу.
        /// </summary>
        public static string CropAndSave(string path, Int32Rect cropRect, bool replaceOriginal)
        {
            BitmapFrame original;
            byte[] fileBytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(fileBytes))
            {
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                original = decoder.Frames[0];
            }

            // Рамка обрезки задаётся относительно того, что видит пользователь на экране,
            // поэтому перед обрезкой применяем ту же EXIF-ориентацию, что и при показе
            var source = ExifOrientationService.ApplyOrientation(
                original, ExifOrientationService.GetOrientation(original));

            // Страховка: не выходим за границы изображения
            int x = Math.Clamp(cropRect.X, 0, source.PixelWidth - 1);
            int y = Math.Clamp(cropRect.Y, 0, source.PixelHeight - 1);
            int w = Math.Clamp(cropRect.Width, 1, source.PixelWidth - x);
            int h = Math.Clamp(cropRect.Height, 1, source.PixelHeight - y);

            var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));

            var ext = Path.GetExtension(path).ToLowerInvariant();
            string saveExt = ext;
            BitmapEncoder encoder;

            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                case ".jfif":
                    encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                    break;
                case ".png":
                    encoder = new PngBitmapEncoder();
                    break;
                case ".bmp":
                    encoder = new BmpBitmapEncoder();
                    break;
                case ".tiff":
                case ".tif":
                    encoder = new TiffBitmapEncoder();
                    break;
                default:
                    if (replaceOriginal)
                        throw new NotSupportedException($"No encoder available for {ext}");
                    // Форматы без энкодера сохраняем копией в PNG (без потерь, с альфой)
                    encoder = new PngBitmapEncoder();
                    saveExt = ".png";
                    break;
            }

            encoder.Frames.Add(BitmapFrame.Create(cropped));

            if (replaceOriginal)
            {
                // Атомарная замена через временный файл — как в RotateAndSaveService
                var tempPath = path + ".tmp";
                using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(outStream);
                }

                File.Delete(path);
                File.Move(tempPath, path);
                return path;
            }

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var newPath = GetAvailablePath(Path.Combine(dir, name + " (cropped)" + saveExt));

            using (var stream = new FileStream(newPath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            return newPath;
        }

        private static string GetAvailablePath(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int counter = 1;

            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
                counter++;
            } while (File.Exists(candidate));

            return candidate;
        }
    }
}
