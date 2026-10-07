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
            // Рамка обрезки задана относительно того, что видит пользователь,
            // поэтому берём пиксели с уже применённой EXIF-ориентацией.
            // SafeImageDecoder переживает файлы с битым EXIF: раньше такой снимок
            // валился с «Непредвиденный тип или значение свойства», хотя открывался.
            var source = SafeImageDecoder.LoadOriented(path);

            // Страховка: не выходим за границы изображения
            int x = Math.Clamp(cropRect.X, 0, source.PixelWidth - 1);
            int y = Math.Clamp(cropRect.Y, 0, source.PixelHeight - 1);
            int w = Math.Clamp(cropRect.Width, 1, source.PixelWidth - x);
            int h = Math.Clamp(cropRect.Height, 1, source.PixelHeight - y);

            var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
            if (cropped.CanFreeze) cropped.Freeze();

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
                // Замена через временный файл — как в RotateAndSaveService
                SafeFileReplace.WriteThenReplace(path, stream => encoder.Save(stream));
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
