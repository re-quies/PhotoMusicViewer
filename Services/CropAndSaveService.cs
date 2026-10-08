using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>Как обрезать JPEG.</summary>
    public enum CropMethod
    {
        /// <summary>Без перекодирования (jpegtran): ни потерь качества, ни потерь метаданных;
        /// левый и верхний край сдвигаются к сетке JPEG (8 или 16 пикселей).</summary>
        Lossless,
        /// <summary>Точно по рамке, с перекодированием (качество 95); метаданные переносятся.</summary>
        Reencode
    }

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

        /// <summary>Совместимая точка входа: перекодирование, версия файла — на момент вызова.</summary>
        public static string CropAndSave(string path, Int32Rect cropRect, bool replaceOriginal, FileOperationContext? operation = null) =>
            Save(path, cropRect, replaceOriginal, CropMethod.Reencode, null, operation);

        /// <summary>
        /// Обрезает изображение по прямоугольнику (в пикселях с уже применённой
        /// EXIF-ориентацией — как на экране) и сохраняет.
        ///
        /// replaceOriginal = true — замена через ImageSaveWriter, как у поворота: рядом
        /// остаётся точная копия оригинала *.pmv-original-….bak, в этом сеансе работает
        /// кнопка «Вернуть оригинал». Раньше обрезка удаляла копию сразу после записи.
        /// replaceOriginal = false — копия «имя (cropped).ext».
        ///
        /// expectedVersion — версия файла на момент начала обрезки. Если файл с тех пор
        /// изменился (OneDrive, другой редактор), ничего не сохраняется: рамка относилась
        /// к другому снимку. Та же проверка повторяется прямо перед записью.
        ///
        /// Для форматов без энкодера (webp/gif/heic/raw) копия сохраняется как PNG.
        /// Возвращает путь к сохранённому файлу.
        /// </summary>
        internal static string Save(string path, Int32Rect cropRect, bool replaceOriginal, CropMethod method,
            FileVersion? expectedVersion, FileOperationContext? operation = null)
        {
            path = Path.GetFullPath(path);
            var version = FileVersion.Read(path);
            if (expectedVersion is { } expected && expected != version)
                throw new IOException(Loc.T(
                    "The file changed after cropping started (for example, synced by OneDrive). Nothing was saved — open it again and repeat the crop.",
                    "Файл изменился после начала обрезки (например, его обновил OneDrive). Ничего не сохранено — откройте его заново и повторите обрезку.",
                    "El archivo cambió después de empezar el recorte (p. ej., OneDrive). No se guardó nada: ábralo de nuevo y repita el recorte."));
            var token = operation?.Token ?? default;

            if (JpegLossless.IsJpeg(path) && method == CropMethod.Lossless)
            {
                operation?.Checkpoint(FileOperationStage.Encoding);
                var rect = new JpegLossless.PixelRect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
                return ImageSaveWriter.Write(path, copy: !replaceOriginal, " (cropped)",
                    output => JpegLossless.Crop(path, output, rect, token), operation, version);
            }

            // Рамка обрезки задана относительно того, что видит пользователь,
            // поэтому берём пиксели с уже применённой EXIF-ориентацией.
            // SafeImageDecoder переживает файлы с битым EXIF: раньше такой снимок
            // валился с «Непредвиденный тип или значение свойства», хотя открывался.
            operation?.Checkpoint(FileOperationStage.Decoding);
            var source = ImageFormatSniffer.Detect(path) == ImageFileFormat.Gif
                ? GifAnimator.LoadFirstFrameForEditingWithToken(path, token)
                : SafeImageDecoder.LoadDecoded(path, 0, rejectMultiPageTiff: replaceOriginal, token: token).Image;

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
            bool jpeg = false;

            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                case ".jfif":
                    encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                    jpeg = true;
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

            operation?.Checkpoint(FileOperationStage.Encoding);
            encoder.Frames.Add(BitmapFrame.Create(cropped));

            Action<Stream> write = jpeg
                ? output => WriteJpegWithMetadata(path, encoder, output, w, h, token)
                : output => encoder.Save(output);

            if (saveExt == ext)
                return ImageSaveWriter.Write(path, copy: !replaceOriginal, " (cropped)", write, operation, version);

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            return SafeFileReplace.WriteCopyWithOperation(Path.Combine(dir, name + " (cropped)" + saveExt), stream =>
            {
                write(stream);
                if (FileVersion.Read(path) != version) throw new IOException("Source changed during editing. No copy was saved.");
            }, operation);
        }

        /// <summary>
        /// Перекодированный JPEG + метаданные оригинала (дата съёмки, GPS, камера, ICC, XMP, IPTC).
        /// Если перенос не удался, файл всё равно сохраняется, но без метаданных — с записью в журнал.
        /// </summary>
        private static void WriteJpegWithMetadata(string path, BitmapEncoder encoder, Stream output, int width, int height, System.Threading.CancellationToken token)
        {
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            long start = output.Position;
            try
            {
                using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                encoded.Position = 0;
                JpegMetadataTransplant.Merge(original, encoded, output, width, height, token);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException && ex is not OperationCanceledException)
            {
                AppLog.Warn("CropAndSaveService.Metadata", ex, AppLog.Describe(path));
                output.Position = start; output.SetLength(start);
                encoded.Position = 0; encoded.CopyTo(output);
            }
        }

        // Совместимая точка входа для прежних регрессионных тестов.
        internal static string WriteCopyWithoutOverwrite(string path, Action<Stream> write) =>
            SafeFileReplace.WriteCopyWithoutOverwrite(path, write);
    }
}
