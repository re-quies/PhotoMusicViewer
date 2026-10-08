using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Дисковый кэш миниатюр: повторное открытие сетки не декодирует снимки заново.
    ///
    /// <b>По умолчанию выключен</b> — это след на диске о том, какие фото вы смотрели.
    /// Включается галочкой в настройках (как журнал: файл-метка в %LOCALAPPDATA%\PhotoMusicViewer).
    /// Что лежит на диске:
    /// - %LOCALAPPDATA%\PhotoMusicViewer\thumbs — маленькие JPEG/PNG (сторона до 640 px);
    /// - имена файлов — SHA-256 от (случайная соль установки, путь, размер, время изменения,
    ///   сторона): путей и имён в открытом виде нет, а без файла соли имя не проверить
    ///   перебором известных путей;
    /// - изменённый файл получает новый ключ, старая запись не используется;
    /// - размер ограничен (<see cref="MaxBytes"/>), старые записи удаляются первыми;
    /// - кнопка «Очистить» и выключение галочки удаляют папку целиком.
    /// Все методы безопасны для вызова из рабочих потоков и не бросают исключений наружу.
    /// </summary>
    internal static class ThumbnailDiskCache
    {
        internal const long MaxBytes = 256L * 1024 * 1024;
        internal const int MaxSide = 640;
        private const string MarkerName = "thumbnail-disk-cache";
        private static readonly byte[] Magic = "PMT1"u8.ToArray();
        private static readonly object Gate = new();
        private static bool? _enabled;
        private static byte[]? _salt;
        private static long _approxBytes = -1;

        /// <summary>Корень настроек; переопределяется тестами.</summary>
        internal static string BaseDirectory { get; set; } = BuildBaseDirectory();
        internal static string CacheDirectory => BaseDirectory.Length == 0 ? "" : Path.Combine(BaseDirectory, "thumbs");

        public static bool Enabled
        {
            get
            {
                lock (Gate)
                {
                    _enabled ??= BaseDirectory.Length > 0 && SafeExists(Path.Combine(BaseDirectory, MarkerName));
                    return _enabled.Value;
                }
            }
        }

        /// <summary>Включает/выключает кэш. Выключение удаляет все записи. false — не удалось сохранить.</summary>
        public static bool SetEnabled(bool on)
        {
            lock (Gate)
            {
                _enabled = on;
                try
                {
                    if (BaseDirectory.Length == 0) return false;
                    string marker = Path.Combine(BaseDirectory, MarkerName);
                    if (on)
                    {
                        Directory.CreateDirectory(BaseDirectory);
                        File.WriteAllBytes(marker, Array.Empty<byte>());
                    }
                    else
                    {
                        if (File.Exists(marker)) File.Delete(marker);
                        ClearCore();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("ThumbnailDiskCache.SetEnabled", ex);
                    return false;
                }
            }
        }

        /// <summary>Удаляет все миниатюры и соль. true — папки больше нет.</summary>
        public static bool Clear()
        {
            lock (Gate)
            {
                try { ClearCore(); return true; }
                catch (Exception ex) { AppLog.Warn("ThumbnailDiskCache.Clear", ex); return false; }
            }
        }

        private static void ClearCore()
        {
            _salt = null;
            _approxBytes = -1;
            string dir = CacheDirectory;
            if (dir.Length == 0 || !Directory.Exists(dir)) return;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to clean a linked thumbnail directory.");
            Directory.Delete(dir, recursive: true);
        }

        /// <summary>Текущий объём на диске (для подписи в настройках).</summary>
        public static long SizeOnDisk()
        {
            try
            {
                string dir = CacheDirectory;
                if (dir.Length == 0 || !Directory.Exists(dir)) return 0;
                return new DirectoryInfo(dir).EnumerateFiles("*.pmt", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) { AppLog.Debug("ThumbnailDiskCache.SizeOnDisk", ex); return 0; }
        }

        internal readonly record struct Entry(BitmapSource Image, int NaturalWidth, int NaturalHeight, long FileBytes);

        /// <summary>Готовая миниатюра или null (выключено, нет записи, файл изменился, запись битая).</summary>
        public static Entry? TryLoad(string path, int side, CancellationToken token = default)
        {
            if (!Enabled || side <= 0 || side > MaxSide) return null;
            string? file = EntryPath(path, side, create: false);
            if (file == null) return null;
            try
            {
                if (!File.Exists(file)) return null;
                byte[] data = File.ReadAllBytes(file);
                token.ThrowIfCancellationRequested();
                if (data.Length < 24 || !data.AsSpan(0, 4).SequenceEqual(Magic)) { TryDelete(file); return null; }
                int naturalWidth = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                int naturalHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
                long fileBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(12));
                if (naturalWidth <= 0 || naturalHeight <= 0) { TryDelete(file); return null; }
                using var stream = new MemoryStream(data, 24, data.Length - 24, writable: false);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > MaxSide || frame.PixelHeight > MaxSide) { TryDelete(file); return null; }
                var image = SafeImageDecoder.ToBgra32(frame, token);
                return new Entry(image, naturalWidth, naturalHeight, fileBytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Debug("ThumbnailDiskCache.TryLoad", ex);
                TryDelete(file);
                return null;
            }
        }

        /// <summary>Сохраняет миниатюру (атомарно: временный файл → переименование).</summary>
        public static void Store(string path, int side, BitmapSource image, int naturalWidth, int naturalHeight, long fileBytes)
        {
            if (!Enabled || side <= 0 || side > MaxSide || image.PixelWidth > MaxSide || image.PixelHeight > MaxSide) return;
            string? file = EntryPath(path, side, create: true);
            if (file == null) return;
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                BitmapEncoder encoder = HasTransparency(image)
                    ? new PngBitmapEncoder()
                    : new JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Span<byte> header = stackalloc byte[24];
                    Magic.CopyTo(header);
                    BinaryPrimitives.WriteInt32LittleEndian(header[4..], naturalWidth);
                    BinaryPrimitives.WriteInt32LittleEndian(header[8..], naturalHeight);
                    BinaryPrimitives.WriteInt64LittleEndian(header[12..], fileBytes);
                    output.Write(header);
                    encoder.Save(output);
                }
                long written = new FileInfo(temp).Length;
                File.Move(temp, file, overwrite: true);
                Account(written);
            }
            catch (Exception ex)
            {
                AppLog.Debug("ThumbnailDiskCache.Store", ex);
                TryDelete(temp);
            }
        }

        private static bool HasTransparency(BitmapSource image)
        {
            if (!image.Format.Equals(PixelFormats.Bgra32) && !image.Format.Equals(PixelFormats.Pbgra32)) return false;
            int stride = image.PixelWidth * 4;
            byte[] pixels = new byte[stride * image.PixelHeight];
            image.CopyPixels(pixels, stride, 0);
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) return true;
            return false;
        }

        /// <summary>Учитывает новую запись и при превышении лимита удаляет самые старые.</summary>
        private static void Account(long written)
        {
            bool prune;
            lock (Gate)
            {
                if (_approxBytes < 0) _approxBytes = SizeOnDisk();
                else _approxBytes += written;
                prune = _approxBytes > MaxBytes;
            }
            if (!prune) return;
            try
            {
                var files = new DirectoryInfo(CacheDirectory).EnumerateFiles("*.pmt", SearchOption.AllDirectories)
                    .OrderBy(f => f.LastWriteTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= MaxBytes * 8 / 10) break;
                    long length = f.Length;
                    try { f.Delete(); total -= length; } catch (Exception ex) { AppLog.Debug("ThumbnailDiskCache.Prune", ex); }
                }
                lock (Gate) _approxBytes = total;
            }
            catch (Exception ex) { AppLog.Debug("ThumbnailDiskCache.Prune", ex); }
        }

        private static string? EntryPath(string path, int side, bool create)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                byte[]? salt = Salt(create);
                if (salt == null) return null;
                string full = Path.GetFullPath(path);
                if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
                string material = $"{full}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{side}";
                byte[] hash = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(material));
                string name = Convert.ToHexString(hash).ToLowerInvariant();
                string dir = Path.Combine(CacheDirectory, name[..2]);
                if (create) Directory.CreateDirectory(dir);
                return Path.Combine(dir, name + ".pmt");
            }
            catch (Exception ex)
            {
                AppLog.Debug("ThumbnailDiskCache.EntryPath", ex);
                return null;
            }
        }

        /// <summary>Случайная соль установки (32 байта). Без неё имена записей нельзя
        /// сопоставить с путями перебором.</summary>
        private static byte[]? Salt(bool create)
        {
            lock (Gate)
            {
                if (_salt != null) return _salt;
                string dir = CacheDirectory;
                if (dir.Length == 0) return null;
                string file = Path.Combine(dir, "salt");
                if (File.Exists(file))
                {
                    byte[] existing = File.ReadAllBytes(file);
                    if (existing.Length == 32) return _salt = existing;
                }
                if (!create) return null;
                Directory.CreateDirectory(dir);
                byte[] fresh = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(file, fresh);
                return _salt = fresh;
            }
        }

        private static void TryDelete(string? file)
        {
            if (file == null) return;
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { AppLog.Debug("ThumbnailDiskCache.Delete", ex); }
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch (Exception ex) { AppLog.Debug("ThumbnailDiskCache.Exists", ex); return false; }
        }

        private static string BuildBaseDirectory()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return local.Length == 0 ? "" : Path.Combine(local, "PhotoMusicViewer");
            }
            catch (Exception ex) { AppLog.Debug("ThumbnailDiskCache.BaseDirectory", ex); return ""; }
        }
    }
}
