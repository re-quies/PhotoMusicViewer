using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    public enum WebpConvertTarget { Jpg, Png }

    /// <summary>Что известно о WebP по заголовкам RIFF, без декодирования пикселей.</summary>
    public sealed record WebpInfo(bool IsAnimated, int AnimationFrames, bool MayHaveAlpha);

    /// <summary>
    /// Конвертация WebP в JPG или PNG.
    ///
    /// Раньше: прозрачность молча терялась (фон становился чёрным), у анимированного
    /// WebP оставался только первый кадр, а исходник без вопроса уходил в корзину
    /// (на дисках без корзины — удалялся безвозвратно).
    ///
    /// Теперь:
    /// - <see cref="Inspect"/> заранее сообщает об анимации и прозрачности, чтобы
    ///   интерфейс спросил пользователя;
    /// - PNG сохраняет прозрачность; JPG накладывает полупрозрачные пиксели на белый фон;
    /// - сервис НИКОГДА не удаляет исходник — это отдельное решение пользователя в интерфейсе.
    /// </summary>
    public static class ConvertWebpToJpgService
    {
        private const int JpegQuality = 95;
        private const int MaxChunks = 100000;

        public static bool CanConvert(string path) =>
            string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Читает только заголовки чанков RIFF (VP8X, VP8L, ALPH, ANMF).
        /// Если заголовки не разобрать, возвращает осторожный ответ: «может быть прозрачность».
        /// </summary>
        public static WebpInfo Inspect(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            return Inspect(stream);
        }

        internal static WebpInfo Inspect(Stream stream)
        {
            var unknown = new WebpInfo(false, 0, true);
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) != 12 ||
                header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F' ||
                header[8] != 'W' || header[9] != 'E' || header[10] != 'B' || header[11] != 'P')
                return unknown;

            bool alpha = false, animationFlag = false;
            int frames = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> payload = stackalloc byte[10];
            for (int i = 0; i < MaxChunks; i++)
            {
                if (stream.Read(chunk) != 8) break;
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4));
                long next = stream.Position + size + (size & 1); // чанки выравниваются по 2 байта
                string fourcc = System.Text.Encoding.ASCII.GetString(chunk.Slice(0, 4));
                switch (fourcc)
                {
                    case "VP8X":
                        if (size >= 10 && stream.Read(payload) == 10)
                        {
                            alpha |= (payload[0] & 0x10) != 0;
                            animationFlag = (payload[0] & 0x02) != 0;
                        }
                        break;
                    case "VP8L":
                        // 1 байт сигнатуры 0x2F, затем 14 бит ширины, 14 бит высоты, 1 бит alpha_is_used
                        if (size >= 5 && stream.Read(payload.Slice(0, 5)) == 5 && payload[0] == 0x2F)
                            alpha |= ((BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1)) >> 28) & 1) != 0;
                        break;
                    case "ALPH":
                        alpha = true;
                        break;
                    case "ANMF":
                        frames++;
                        break;
                }
                if (next > stream.Length) break;
                stream.Position = next;
            }
            bool animated = frames > 1 || (animationFlag && frames == 0);
            // Кадры анимации могут быть прозрачными, их ALPH лежит внутри ANMF: считаем возможной.
            return new WebpInfo(animated, frames, alpha || animated);
        }

        /// <summary>Совместимая точка входа: JPG на белом фоне, исходник не трогается.</summary>
        public static string ConvertToJpg(string webpPath, FileOperationContext? operation = null) =>
            Convert(webpPath, WebpConvertTarget.Jpg, operation);

        /// <summary>
        /// Создаёт .jpg или .png рядом с исходником (существующие файлы не перезаписываются)
        /// и возвращает путь к нему. Исходный .webp остаётся на месте.
        /// </summary>
        public static string Convert(string webpPath, WebpConvertTarget target, FileOperationContext? operation = null)
        {
            return ConvertVerified(webpPath, target, SourceFileSnapshot.Capture(webpPath, operation?.Token ?? default), operation);
        }

        internal static string ConvertVerified(string webpPath, WebpConvertTarget target,
            SourceFileSnapshot expected, FileOperationContext? operation = null)
        {
            var token = operation?.Token ?? default;
            // Keep a read-only share for the entire conversion: on Windows, writers
            // and path replacement are blocked until encoding/verification finish.
            using var sourceLock = new FileStream(webpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            expected.RequireMatch(webpPath, token);
            if (!CanConvert(webpPath))
                throw new InvalidOperationException("File is not a .webp image");

            // SafeImageDecoder проверяет лимиты до загрузки пикселей, закрывает поток до
            // возврата, применяет EXIF-ориентацию и переживает битые метаданные.
            operation?.Checkpoint(FileOperationStage.Decoding);
            var source = SafeImageDecoder.LoadDecoded(webpPath, 0, token: operation?.Token ?? default).Image;

            operation?.Checkpoint(FileOperationStage.Encoding);
            BitmapEncoder encoder;
            string extension;
            if (target == WebpConvertTarget.Png)
            {
                encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                extension = ".png";
            }
            else
            {
                encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
                encoder.Frames.Add(BitmapFrame.Create(FlattenOnWhite(source, out _)));
                extension = ".jpg";
            }

            return SafeFileReplace.WriteCopyWithOperation(
                Path.ChangeExtension(webpPath, extension), stream =>
                {
                    encoder.Save(stream);
                    expected.RequireMatch(webpPath, token);
                }, operation);
        }

        /// <summary>
        /// Накладывает изображение на белый фон и возвращает непрозрачный Bgr24.
        /// Без этого JPEG-энкодер просто отбрасывает альфу, и прозрачные пиксели
        /// (обычно чёрные внутри) становятся чёрным фоном.
        /// </summary>
        internal static BitmapSource FlattenOnWhite(BitmapSource source, out bool hadTransparency)
        {
            hadTransparency = false;
            if (!MayHaveAlpha(source.Format)) return source;

            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            bgra.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                int a = pixels[i + 3];
                if (a == 255) continue;
                hadTransparency = true;
                int background = 255 * (255 - a);
                pixels[i + 0] = (byte)((pixels[i + 0] * a + background + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * a + background + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * a + background + 127) / 255);
                pixels[i + 3] = 255;
            }
            if (!hadTransparency) return source;

            var flat = BitmapSource.Create(width, height, source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            var opaque = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
            opaque.Freeze();
            return opaque;
        }

        private static bool MayHaveAlpha(PixelFormat format) =>
            format != PixelFormats.Bgr24 && format != PixelFormats.Bgr32 && format != PixelFormats.Rgb24 &&
            format != PixelFormats.Rgb48 && format != PixelFormats.Bgr555 && format != PixelFormats.Bgr565 &&
            format != PixelFormats.Bgr101010 && format != PixelFormats.Gray2 && format != PixelFormats.Gray4 &&
            format != PixelFormats.Gray8 && format != PixelFormats.Gray16 && format != PixelFormats.Gray32Float &&
            format != PixelFormats.BlackWhite && format != PixelFormats.Cmyk32 && format != PixelFormats.Rgb128Float;
    }
}
