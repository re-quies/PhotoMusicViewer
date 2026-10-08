using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Удаление всех метаданных (EXIF, GPS, данные камеры, ICC и пр.) из файла
    /// JPEG — без потерь; другие форматы перекодируются. Копия по умолчанию.
    /// </summary>
    public static class StripExifService
    {
        public static bool CanStrip(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".tiff" or ".tif";
        }

        public static void StripMetadata(string path, FileOperationContext? operation = null) =>
            SaveStripped(path, copy: false, allowReencode: false, operation);

        public static string SaveStripped(string path, bool copy = true, bool allowReencode = false, FileOperationContext? operation = null)
        {
            var version = FileVersion.Read(path);
            if (JpegLossless.IsJpeg(path) && !allowReencode)
                return ImageSaveWriter.Write(path, copy, "_clean", output => JpegLossless.Transform(path, output, 0, strip: true, operation?.Token ?? default), operation, version);
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
            operation?.Checkpoint(FileOperationStage.Decoding);
            var source = SafeImageDecoder.LoadDecoded(path, 0, rejectMultiPageTiff: true, token: operation?.Token ?? default).Image;

            // BitmapFrame.Create(BitmapSource) не переносит метаданные — именно это нам и нужно
            operation?.Checkpoint(FileOperationStage.Encoding);
            encoder.Frames.Add(BitmapFrame.Create(source));

            // Замена исходника одной операцией файловой системы (без окна,
            // в котором старый файл уже удалён, а новый ещё не записан)
            return ImageSaveWriter.Write(path, copy, "_clean", stream => encoder.Save(stream), operation, version);
        }
    }
}
