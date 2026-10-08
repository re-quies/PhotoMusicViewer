using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    /// <summary>Формат файла, определённый по сигнатуре — первым байтам, а не по расширению.</summary>
    internal enum ImageFileFormat
    {
        Unknown,
        Gif,
        Png,
        Jpeg,
        WebP,
        Bmp,
        Tiff,
        Heif,
        Avif,
        Ico,
        Psd,
        Svg,
        Mp4,
        WebM,
        Avi,
        Archive
    }

    /// <summary>
    /// Определяет настоящий формат файла по первым байтам.
    ///
    /// Зачем нужен: расширение ничего не гарантирует. Мессенджеры, соцсети и
    /// «скачиватели гифок» массово раздают под именем *.gif либо WebP, либо PNG,
    /// либо вовсе MP4 — анимация там есть, а GIF внутри нет.
    ///
    /// WIC подбирает декодер по содержимому файла, а не по имени. Поэтому явно
    /// созданный GifBitmapDecoder на таком файле не запускается, а падает:
    ///     System.IO.FileFormatException: «Кодек не может использовать указанный тип потока»
    /// (BitmapDecoder сверяет CLSID найденного кодека с ожидаемым, и они не совпадают).
    ///
    /// Проверка сигнатуры до создания декодера позволяет заранее понять, что
    /// анимации не будет, и показать файл обычной картинкой вместо ошибки.
    /// </summary>
    internal static class ImageFormatSniffer
    {
        /// <summary>Сигнатуры всех известных здесь форматов укладываются в 16 байт.</summary>
        private const int HeaderSize = 16;

        /// <summary>
        /// Потолок для файлов с неопознанной сигнатурой: попробовать их обычным
        /// декодером имеет смысл (вдруг в системе стоит сторонний кодек WIC),
        /// но читать ради этого в память гигабайтный файл — нет.
        /// </summary>
        private const long MaxUnknownProbeBytes = 64L * 1024 * 1024;

        /// <summary>Читает сигнатуру файла. Недоступный файл — это просто «не знаю».</summary>
        public static ImageFileFormat Detect(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, HeaderSize, FileOptions.SequentialScan);
                return Detect(stream);
            }
            catch (Exception ex)
            {
                AppLog.Debug("ImageFormatSniffer.DetectFile", ex);
                return ImageFileFormat.Unknown;
            }
        }

        /// <summary>
        /// Читает сигнатуру из потока и возвращает его на исходную позицию —
        /// поток остаётся пригодным для последующего декодирования.
        /// </summary>
        public static ImageFileFormat Detect(Stream stream)
        {
            if (stream is not { CanRead: true, CanSeek: true }) return ImageFileFormat.Unknown;

            long position = stream.Position;
            try
            {
                stream.Seek(0, SeekOrigin.Begin);

                Span<byte> head = stackalloc byte[HeaderSize];
                int read = 0;
                while (read < head.Length)
                {
                    int chunk = stream.Read(head[read..]);
                    if (chunk <= 0) break;   // файл короче сигнатуры
                    read += chunk;
                }

                return Detect(head[..read]);
            }
            catch (Exception ex)
            {
                AppLog.Debug("ImageFormatSniffer.DetectStream", ex);
                return ImageFileFormat.Unknown;
            }
            finally
            {
                try { stream.Seek(position, SeekOrigin.Begin); }
                catch (Exception ex) { AppLog.Debug("ImageFormatSniffer.Rewind", ex); }
            }
        }

        /// <summary>Разбор сигнатуры. Вынесен отдельно, чтобы его можно было проверить без файла.</summary>
        public static ImageFileFormat Detect(ReadOnlySpan<byte> head)
        {
            if (head.Length < 4) return ImageFileFormat.Unknown;

            if (Matches(head, 0, "GIF8")) return ImageFileFormat.Gif;            // GIF87a и GIF89a
            if (head[0] == 0x89 && Matches(head, 1, "PNG")) return ImageFileFormat.Png;
            if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ImageFileFormat.Jpeg;
            if (Matches(head, 0, "BM")) return ImageFileFormat.Bmp;
            if (Matches(head, 0, "8BPS")) return ImageFileFormat.Psd;
            if (Matches(head, 0, "II") && head[2] == 0x2A && head[3] == 0x00) return ImageFileFormat.Tiff;
            if (Matches(head, 0, "MM") && head[2] == 0x00 && head[3] == 0x2A) return ImageFileFormat.Tiff;
            if (head[0] == 0x00 && head[1] == 0x00 && head[2] == 0x01 && head[3] == 0x00) return ImageFileFormat.Ico;
            if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return ImageFileFormat.WebM;
            if (Matches(head, 0, "PK") && head[2] == 0x03 && head[3] == 0x04) return ImageFileFormat.Archive;

            if (Matches(head, 0, "RIFF"))
            {
                if (Matches(head, 8, "WEBP")) return ImageFileFormat.WebP;
                if (Matches(head, 8, "AVI ")) return ImageFileFormat.Avi;
            }

            // ISO BMFF — один контейнер и у картинок (HEIC, AVIF), и у видео (MP4, MOV):
            // различаем по бренду, он идёт сразу за «ftyp»
            if (Matches(head, 4, "ftyp"))
            {
                if (Matches(head, 8, "avif") || Matches(head, 8, "avis")) return ImageFileFormat.Avif;

                if (Matches(head, 8, "heic") || Matches(head, 8, "heix") ||
                    Matches(head, 8, "heim") || Matches(head, 8, "heis") ||
                    Matches(head, 8, "hevc") || Matches(head, 8, "hevx") ||
                    Matches(head, 8, "mif1") || Matches(head, 8, "msf1")) return ImageFileFormat.Heif;

                return ImageFileFormat.Mp4;   // isom, mp41/42, qt, M4V… — всё это видео
            }

            if (Matches(head, 0, "<svg") || Matches(head, 0, "<?xml")) return ImageFileFormat.Svg;

            return ImageFileFormat.Unknown;
        }

        /// <summary>Название формата для сообщения пользователю.</summary>
        public static string Describe(ImageFileFormat format) => format switch
        {
            ImageFileFormat.Gif => "GIF",
            ImageFileFormat.Png => "PNG",
            ImageFileFormat.Jpeg => "JPEG",
            ImageFileFormat.WebP => "WebP",
            ImageFileFormat.Bmp => "BMP",
            ImageFileFormat.Tiff => "TIFF",
            ImageFileFormat.Heif => "HEIC",
            ImageFileFormat.Avif => "AVIF",
            ImageFileFormat.Ico => "ICO",
            ImageFileFormat.Psd => "PSD",
            ImageFileFormat.Svg => "SVG",
            ImageFileFormat.Mp4 => Loc.T("MP4 video", "видео MP4", "vídeo MP4"),
            ImageFileFormat.WebM => Loc.T("WebM video", "видео WebM", "vídeo WebM"),
            ImageFileFormat.Avi => Loc.T("AVI video", "видео AVI", "vídeo AVI"),
            ImageFileFormat.Archive => Loc.T("an archive", "архив", "un archivo comprimido"),
            _ => Loc.T("an unknown format", "неизвестный формат", "un formato desconocido")
        };

        /// <summary>
        /// Стоит ли пробовать показать файл обычным декодером WPF.
        /// Видео, архив и SVG он не покажет, а читать их целиком в память незачем.
        /// </summary>
        public static bool CanTryStaticImage(ImageFileFormat format, long sizeBytes) => format switch
        {
            ImageFileFormat.Mp4 or ImageFileFormat.WebM or ImageFileFormat.Avi
                or ImageFileFormat.Archive or ImageFileFormat.Svg => false,
            ImageFileFormat.Unknown => sizeBytes <= MaxUnknownProbeBytes,
            _ => true
        };

        /// <summary>
        /// Пояснение для сообщения об ошибке, когда содержимое не совпадает с расширением.
        /// Пустая строка — расширение честное либо формат не опознан.
        /// </summary>
        public static string DescribeMismatch(string path)
        {
            var actual = Detect(path);
            if (actual == ImageFileFormat.Unknown) return "";

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ExtensionFits(actual, ext)) return "";

            string name = Describe(actual);
            return Loc.T($" \u2014 the file is actually {name}",
                         $" \u2014 на самом деле это {name}",
                         $" \u2014 en realidad es {name}");
        }

        private static bool ExtensionFits(ImageFileFormat format, string ext) => format switch
        {
            ImageFileFormat.Gif => ext == ".gif",
            ImageFileFormat.Png => ext is ".png" or ".apng",
            ImageFileFormat.Jpeg => ext is ".jpg" or ".jpeg" or ".jfif",
            ImageFileFormat.WebP => ext == ".webp",
            ImageFileFormat.Bmp => ext is ".bmp" or ".dib",
            // RAW-снимки (CR2, NEF, ARW, DNG…) — это тоже контейнер TIFF,
            // чужим расширением их считать нельзя
            ImageFileFormat.Tiff => ext is ".tif" or ".tiff" or ".cr2" or ".nef" or ".arw"
                or ".dng" or ".orf" or ".rw2" or ".pef" or ".srw",
            ImageFileFormat.Heif => ext is ".heic" or ".heif" or ".hif",
            ImageFileFormat.Avif => ext == ".avif",
            ImageFileFormat.Ico => ext is ".ico" or ".cur",
            ImageFileFormat.Psd => ext == ".psd",
            ImageFileFormat.Svg => ext == ".svg",
            _ => false
        };

        private static bool Matches(ReadOnlySpan<byte> data, int offset, string ascii)
        {
            if (offset < 0 || offset + ascii.Length > data.Length) return false;

            for (int i = 0; i < ascii.Length; i++)
                if (data[offset + i] != (byte)ascii[i]) return false;

            return true;
        }
    }
}
