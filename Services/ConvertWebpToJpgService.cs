using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    public static class ConvertWebpToJpgService
    {
        /// <summary>
        /// Конвертирует .webp файл в .jpg (перекодирование пикселей), удаляет исходный webp.
        /// Возвращает путь к новому .jpg файлу.
        /// </summary>
        public static string ConvertToJpg(string webpPath)
        {
            if (Path.GetExtension(webpPath).ToLowerInvariant() != ".webp")
                throw new InvalidOperationException("File is not a .webp image");

            BitmapFrame original;
            using (var stream = new FileStream(webpPath, FileMode.Open, FileAccess.Read))
            {
                // Требует установленного WebP-кодека Windows — если файл вообще
                // отображался в приложении, значит кодек есть и декодирование сработает.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                original = decoder.Frames[0];
            }

            var newPath = Path.ChangeExtension(webpPath, ".jpg");

            // На случай если файл с таким именем уже существует
            newPath = GetAvailablePath(newPath);

            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(original));

            using (var outStream = new FileStream(newPath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(outStream);
            }

            File.Delete(webpPath);

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