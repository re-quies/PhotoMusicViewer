using System;
using System.IO;
using System.Text;

namespace PhotoMusicViewer.Services
{
    /// <summary>Формат файла, определённый по содержимому (а не по расширению).</summary>
    public enum SniffedFormat
    {
        Unknown,
        Gif, Png, Jpeg, WebP, Bmp, Tiff, Heic, Avif, Ico, Psd,
        Svg, Mp4, WebM, Avi, Zip
    }

    /// <summary>
    /// Определяет настоящий формат файла по первым байтам. Расширению верить нельзя:
    /// «гифки» из интернета и мессенджеров нередко оказываются WebP, PNG или даже MP4.
    ///
    /// Чтение из потока возвращает позицию назад, поэтому тот же FileStream сразу
    /// можно отдавать в декодер — второго открытия файла не нужно.
    ///
    /// Особые случаи:
    ///  * ISO BMFF (блок «ftyp») — общий контейнер и для картинок (HEIC, AVIF), и для видео
    ///    (MP4, MOV); они различаются по «бренду» после ftyp (и по списку совместимых брендов).
    ///  * RAW-снимки (CR2, NEF, ARW, DNG) — это TIFF-контейнер, они определяются как Tiff.
    ///    Определитель вызывается только там, где файл заведомо должен быть другим форматом,
    ///    так что «чужим расширением» RAW не считается.
    /// </summary>
    public static class ImageFormatSniffer
    {
        // Для большинства форматов хватает 16 байт; остальное нужно SVG (ищем тег <svg)
        // и списку совместимых брендов HEIC/AVIF
        private const int HeaderLength = 512;

        /// <summary>
        /// Читает начало потока и возвращает его на прежнее место.
        /// Поток должен поддерживать перемотку (FileStream и MemoryStream подходят).
        /// </summary>
        public static SniffedFormat Detect(Stream stream)
        {
            if (!stream.CanSeek)
                throw new ArgumentException("The stream must be seekable.", nameof(stream));

            long start = stream.Position;
            var buffer = new byte[HeaderLength];
            int read = 0;
            try
            {
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }
            finally
            {
                stream.Position = start;
            }

            return Detect(new ReadOnlySpan<byte>(buffer, 0, read));
        }

        /// <summary>Определяет формат по уже прочитанному началу файла.</summary>
        public static SniffedFormat Detect(ReadOnlySpan<byte> h)
        {
            if (h.Length < 4) return SniffedFormat.Unknown;

            if (Match(h, 0, "GIF87a") || Match(h, 0, "GIF89a")) return SniffedFormat.Gif;

            if (h.Length >= 8 && h[0] == 0x89 && Match(h, 1, "PNG") &&
                h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A)
                return SniffedFormat.Png;

            if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return SniffedFormat.Jpeg;

            // RIFF-контейнер: внутри WebP или AVI
            if (Match(h, 0, "RIFF") && h.Length >= 12)
            {
                if (Match(h, 8, "WEBP")) return SniffedFormat.WebP;
                if (Match(h, 8, "AVI ")) return SniffedFormat.Avi;
            }

            // ISO BMFF: HEIC / AVIF / MP4 / MOV
            if (h.Length >= 12 && Match(h, 4, "ftyp")) return DetectIsoBmff(h);

            // ZIP (в том числе пустой архив и разбитый на тома)
            if (h[0] == 0x50 && h[1] == 0x4B &&
                ((h[2] == 0x03 && h[3] == 0x04) ||
                 (h[2] == 0x05 && h[3] == 0x06) ||
                 (h[2] == 0x07 && h[3] == 0x08)))
                return SniffedFormat.Zip;

            // EBML: WebM / Matroska
            if (h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) return SniffedFormat.WebM;

            if (Match(h, 0, "8BPS")) return SniffedFormat.Psd;

            // TIFF (классический и BigTIFF), обе порядка байт
            if ((h[0] == 0x49 && h[1] == 0x49 && (h[2] == 0x2A || h[2] == 0x2B) && h[3] == 0x00) ||
                (h[0] == 0x4D && h[1] == 0x4D && h[2] == 0x00 && (h[3] == 0x2A || h[3] == 0x2B)))
                return SniffedFormat.Tiff;

            // ICO / CUR: 00 00 01|02 00, затем число изображений (не ноль)
            if (h.Length >= 6 && h[0] == 0x00 && h[1] == 0x00 && (h[2] == 0x01 || h[2] == 0x02) &&
                h[3] == 0x00 && (h[4] != 0x00 || h[5] != 0x00))
                return SniffedFormat.Ico;

            // BMP: «BM» мало, поэтому проверяем ещё и зарезервированные поля (байты 6..9 = 0)
            if (h.Length >= 14 && h[0] == 0x42 && h[1] == 0x4D &&
                h[6] == 0 && h[7] == 0 && h[8] == 0 && h[9] == 0)
                return SniffedFormat.Bmp;

            if (LooksLikeSvg(h)) return SniffedFormat.Svg;

            return SniffedFormat.Unknown;
        }

        /// <summary>
        /// Стоит ли пробовать показать файл как обычную картинку. Видео, архив, SVG и PSD
        /// WPF всё равно не откроет, а DecodeFullImage сначала читает файл целиком в память:
        /// на видео в несколько гигабайт это было бы хуже самой ошибки.
        /// Неизвестный формат пробуем — это может быть редкая картинка (TGA, DDS и т.п.).
        /// </summary>
        public static bool CanTryStaticImage(SniffedFormat format) => format switch
        {
            SniffedFormat.Svg or SniffedFormat.Mp4 or SniffedFormat.WebM or
            SniffedFormat.Avi or SniffedFormat.Zip or SniffedFormat.Psd => false,
            _ => true
        };

        /// <summary>Название формата для сообщения пользователю («видео MP4», «архив ZIP»…).</summary>
        public static string Describe(SniffedFormat format) => format switch
        {
            SniffedFormat.Mp4 => Loc.T("MP4/MOV video", "видео MP4/MOV", "vídeo MP4/MOV"),
            SniffedFormat.WebM => Loc.T("WebM/MKV video", "видео WebM/MKV", "vídeo WebM/MKV"),
            SniffedFormat.Avi => Loc.T("AVI video", "видео AVI", "vídeo AVI"),
            SniffedFormat.Zip => Loc.T("ZIP archive", "архив ZIP", "archivo ZIP"),
            SniffedFormat.Svg => Loc.T("SVG vector image", "векторная картинка SVG", "imagen vectorial SVG"),
            SniffedFormat.Psd => Loc.T("Photoshop image (PSD)", "изображение Photoshop (PSD)", "imagen de Photoshop (PSD)"),
            SniffedFormat.Heic => string.Format(Loc.T("{0} image", "изображение {0}", "imagen {0}"), "HEIC/HEIF"),
            SniffedFormat.Unknown => Loc.T("an unknown format", "неизвестный формат", "un formato desconocido"),
            _ => string.Format(Loc.T("{0} image", "изображение {0}", "imagen {0}"), ShortName(format))
        };

        private static string ShortName(SniffedFormat format) => format switch
        {
            SniffedFormat.Gif => "GIF",
            SniffedFormat.Png => "PNG",
            SniffedFormat.Jpeg => "JPEG",
            SniffedFormat.WebP => "WebP",
            SniffedFormat.Bmp => "BMP",
            SniffedFormat.Tiff => "TIFF",
            SniffedFormat.Avif => "AVIF",
            SniffedFormat.Ico => "ICO",
            _ => format.ToString()
        };

        // ------------------------------------------------------------- внутреннее

        /// <summary>Различает картинки (HEIC/AVIF) и видео (MP4/MOV) по «бренду» после ftyp.</summary>
        private static SniffedFormat DetectIsoBmff(ReadOnlySpan<byte> h)
        {
            string brand = Encoding.ASCII.GetString(h.Slice(8, 4));

            switch (brand)
            {
                case "avif":
                case "avis":
                    return SniffedFormat.Avif;

                case "heic":
                case "heix":
                case "hevc":
                case "hevx":
                case "heim":
                case "heis":
                case "hevm":
                case "hevs":
                    return SniffedFormat.Heic;

                case "mif1":
                case "msf1":
                    // Общий «картиночный» бренд: AVIF прячется в списке совместимых брендов
                    return CompatibleBrandsContain(h, "avif", "avis")
                        ? SniffedFormat.Avif
                        : SniffedFormat.Heic;

                default:
                    return SniffedFormat.Mp4; // isom, mp41, mp42, qt, M4V, 3gp... — видео/аудио
            }
        }

        /// <summary>Список совместимых брендов: после major (8..11) и minor (12..15) идут по 4 байта.</summary>
        private static bool CompatibleBrandsContain(ReadOnlySpan<byte> h, string first, string second)
        {
            // размер блока ftyp — первые 4 байта, big-endian
            long boxSize = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
            int end = (int)Math.Min(boxSize <= 0 ? h.Length : boxSize, h.Length);

            for (int offset = 16; offset + 4 <= end; offset += 4)
            {
                if (Match(h, offset, first) || Match(h, offset, second)) return true;
            }
            return false;
        }

        private static bool LooksLikeSvg(ReadOnlySpan<byte> h)
        {
            int i = 0;

            // UTF-8 BOM
            if (h.Length >= 3 && h[0] == 0xEF && h[1] == 0xBB && h[2] == 0xBF) i = 3;

            // пробелы и переводы строк в начале
            while (i < h.Length && (h[i] == 0x20 || h[i] == 0x09 || h[i] == 0x0A || h[i] == 0x0D)) i++;

            if (i >= h.Length || h[i] != (byte)'<') return false;

            // <svg ...> или <?xml ...?> с тегом <svg где-то дальше (после DOCTYPE и комментариев)
            return Encoding.ASCII.GetString(h.Slice(i)).Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Сравнивает байты начиная с offset с ASCII-строкой.</summary>
        private static bool Match(ReadOnlySpan<byte> h, int offset, string ascii)
        {
            if (h.Length < offset + ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
            {
                if (h[offset + i] != (byte)ascii[i]) return false;
            }
            return true;
        }
    }
}
