using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Файл нельзя проиграть как анимацию: внутри не GIF (WebP, PNG или MP4 под
    /// чужим именем) либо GIF повреждён.
    ///
    /// Отдельный тип нужен, чтобы вызывающий код отличил «это не анимация» от
    /// настоящего сбоя и показал файл обычной картинкой вместо ошибки.
    /// </summary>
    internal sealed class GifUnsupportedException : Exception
    {
        public GifUnsupportedException(ImageFileFormat actualFormat, string message, Exception? inner = null)
            : base(message, inner) => ActualFormat = actualFormat;

        /// <summary>Формат, определённый по сигнатуре файла, а не по расширению.</summary>
        public ImageFileFormat ActualFormat { get; }
    }

    /// <summary>Результат проверки заголовков GIF без распаковки LZW.</summary>
    internal readonly record struct GifPreflight(int Width, int Height, int FrameCount, bool Truncated);

    /// <summary>
    /// Потоковый проигрыватель GIF с корректной сборкой кадров.
    ///
    /// WPF-декодер (GifBitmapDecoder) отдаёт кадры такими, как они лежат в файле, —
    /// частичными (только изменившийся прямоугольник) и с прозрачным цветом палитры.
    /// Поэтому каждый кадр собирается на полноразмерном холсте (logical screen) в Pbgra32
    /// с учётом смещения (imgdesc), альфа-смешивания и способа очистки (disposal).
    ///
    /// Раньше все кадры декодировались (BitmapCacheOption.OnLoad) и собирались ДО показа
    /// первого, а их суммарный объём ограничивался 64 МиБ: GIF 640×360 с полными кадрами
    /// упирался в лимит примерно на 72-м кадре, длинные анимации не открывались вовсе,
    /// а допустимые показывались с задержкой и уменьшенными.
    ///
    /// Теперь:
    /// - кадры собираются отдельным фоновым потоком по одному, первый показывается сразу;
    /// - декодер работает с BitmapCacheOption.None — распакованные кадры не копятся;
    /// - если вся анимация в полном размере влезает в FrameCacheBudgetBytes, после первого
    ///   круга кадры берутся из кэша без повторного декодирования;
    /// - иначе кадры собираются заново на каждом круге, а вперёд готовится лишь небольшой
    ///   буфер (LookaheadBudgetBytes). Длина анимации память больше не ограничивает,
    ///   кадры не уменьшаются;
    /// - повреждённый «хвост» файла не мешает: проигрываются читаемые кадры.
    /// </summary>
    public class GifAnimator
    {
        private const int BytesPerPixel = 4;
        private const int DefaultDelayMs = 100;
        private const int LateFramePollMs = 15;
        private const int MaxFrames = 50000;

        /// <summary>Полный кэш собранных кадров (без уменьшения), если анимация в него помещается.</summary>
        private static readonly long FrameCacheBudgetBytes =
            Environment.Is64BitProcess ? 256L * 1024 * 1024 : 96L * 1024 * 1024;

        /// <summary>Сколько готовых кадров держится впереди в потоковом режиме.</summary>
        private const long LookaheadBudgetBytes = 48L * 1024 * 1024;
        private const int MinLookaheadFrames = 2;
        private const int MaxLookaheadFrames = 16;

        /// <summary>Одновременно: холст, точка восстановления, кадр-источник, готовый кадр, буфер.</summary>
        private static readonly long CompositionWorkingBudgetBytes =
            Environment.Is64BitProcess ? 256L * 1024 * 1024 : 128L * 1024 * 1024;

        // Файл декодируется по требованию и в память целиком не читается.
        private const long MaxGifFileBytes = ImageSafetyPolicy.MaxFileBytes;

        private readonly FrameProducer _producer;
        private readonly Image _target;
        private readonly DispatcherTimer _timer = new();
        private readonly BitmapSource _firstFrame;
        private int _currentDelayMs;
        private bool _firstShown;
        private bool _stopped;

        public bool IsPlaying { get; private set; }

        /// <summary>Число кадров по заголовкам файла.</summary>
        public int FrameCount { get; }

        // Размеры оригинального холста.
        public int NaturalWidth { get; }
        public int NaturalHeight { get; }

        private GifAnimator(FrameProducer producer, GifFirstFrame first, Image target)
        {
            _producer = producer;
            _target = target;
            _firstFrame = first.Image;
            _currentDelayMs = first.DelayMs;
            FrameCount = first.FrameCount;
            NaturalWidth = first.Width;
            NaturalHeight = first.Height;

            // сглаживание при масштабировании: с premultiplied-альфой тёмной каймы не будет
            RenderOptions.SetBitmapScalingMode(_target, BitmapScalingMode.HighQuality);
            _timer.Tick += Timer_Tick;
        }

        /// <summary>
        /// Запускает фоновую сборку и возвращает проигрыватель, как только готов первый
        /// кадр (продолжение — в UI-потоке: DispatcherTimer и Image требуют именно его).
        /// Отмена token при листании останавливает и фоновую сборку.
        /// </summary>
        public static async Task<GifAnimator> LoadAsync(string path, Image target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var producer = new FrameProducer(path, token);
            GifFirstFrame first;
            try
            {
                using (token.Register(producer.Cancel))
                    first = await producer.FirstFrame.ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                producer.Cancel();
                throw;
            }
            return new GifAnimator(producer, first, target);
        }

        /// <summary>Полноразмерный первый кадр на логическом холсте для обрезки.
        /// Смещения частичных кадров учитываются так же, как при показе GIF.</summary>
        internal static BitmapSource LoadFirstFrameForEditing(string path) => LoadFirstFrameForEditingWithToken(path, CancellationToken.None);

        internal static BitmapSource LoadFirstFrameForEditingWithToken(string path, CancellationToken token)
        {
            using var stream = OpenGif(path, token, out var preflight);
            try
            {
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) throw new FileFormatException("GIF contains no frames.");
                var canvas = new FrameCanvas(preflight.Width, preflight.Height);
                return canvas.ComposeNext(decoder.Frames[0], token).Image;
            }
            catch (Exception ex) when (IsBrokenFile(ex))
            {
                throw new GifUnsupportedException(ImageFileFormat.Gif,
                    Loc.T("the GIF is damaged", "GIF повреждён", "el GIF está dañado"), ex);
            }
        }

        /// <summary>Открывает файл, проверяет сигнатуру и заголовки; поток в позиции 0.</summary>
        private static FileStream OpenGif(string path, CancellationToken token, out GifPreflight preflight)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.RandomAccess);
            try
            {
                // Расширение .gif не значит, что внутри GIF: мессенджеры и сайты раздают
                // под этим именем WebP, PNG и даже MP4. Вызывающий код по этому исключению
                // покажет файл обычной картинкой вместо сообщения об ошибке.
                var actual = ImageFormatSniffer.Detect(stream);
                if (actual != ImageFileFormat.Gif)
                    throw new GifUnsupportedException(actual,
                        Loc.T("the file is not a GIF", "файл не является GIF", "el archivo no es un GIF"));
                try { preflight = ValidateGifBeforeDecode(stream, token); }
                catch (Exception ex) when (ex is EndOfStreamException or FileFormatException)
                {
                    throw new GifUnsupportedException(ImageFileFormat.Gif,
                        Loc.T("the GIF is damaged", "GIF повреждён", "el GIF está dañado"), ex);
                }
                stream.Position = 0;
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        /// <summary>
        /// true, если сбой относится к разбору файла, а не к работе приложения.
        /// Отмена (OperationCanceledException) сюда намеренно не попадает.
        /// </summary>
        private static bool IsBrokenFile(Exception ex) =>
            ex is FileFormatException or EndOfStreamException or NotSupportedException or COMException
                or ArgumentException or IndexOutOfRangeException or OverflowException;

        // Проверка ДО WIC: размеры и кадры читаются без распаковки LZW.
        // Суммарный объём кадров больше не ограничивается: они не накапливаются.
        // Проверяются только размер холста (рабочая память одного кадра) и структура.
        internal static GifPreflight ValidateGifBeforeDecode(Stream stream, CancellationToken token)
        {
            long originalPosition = stream.Position;
            try
            {
                if (stream.Length > MaxGifFileBytes) ThrowGifMemoryLimit();
                stream.Position = 0;
                using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                string signature = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(6));
                if (signature != "GIF87a" && signature != "GIF89a")
                    throw new FileFormatException("Invalid GIF signature.");
                int width = reader.ReadUInt16();
                int height = reader.ReadUInt16();
                if (width == 0 || height == 0) throw new FileFormatException("Invalid GIF dimensions.");
                byte packed = reader.ReadByte();
                reader.ReadByte(); // background colour index
                reader.ReadByte(); // pixel aspect ratio
                if ((packed & 0x80) != 0) SkipGifBytes(reader, 3 * (1 << ((packed & 7) + 1)));
                ValidateCompositionSize(width, height);

                int frameCount = 0;
                bool truncated = false;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (stream.Position >= stream.Length) { truncated = true; break; } // нет трейлера 0x3B
                        byte marker = reader.ReadByte();
                        if (marker == 0x3B) break;
                        if (marker == 0x21)
                        {
                            reader.ReadByte(); // extension label
                            SkipGifSubBlocks(reader, token);
                            continue;
                        }
                        if (marker != 0x2C) throw new FileFormatException("Invalid GIF block.");
                        int left = reader.ReadUInt16(), top = reader.ReadUInt16();
                        int fw = reader.ReadUInt16(), fh = reader.ReadUInt16();
                        if (fw == 0 || fh == 0 || left + fw > width || top + fh > height)
                            throw new FileFormatException("GIF frame is outside the logical screen.");
                        byte localPacked = reader.ReadByte();
                        if ((localPacked & 0x80) != 0)
                            SkipGifBytes(reader, 3 * (1 << ((localPacked & 7) + 1)));
                        byte codeSize = reader.ReadByte();
                        if (codeSize < 2 || codeSize > 8) throw new FileFormatException("Invalid GIF LZW code size.");
                        SkipGifSubBlocks(reader, token);
                        frameCount = checked(frameCount + 1);
                        if (frameCount > MaxFrames) ThrowGifMemoryLimit();
                    }
                }
                catch (Exception ex) when (frameCount > 0 && ex is EndOfStreamException or FileFormatException)
                {
                    // Недокачанный/повреждённый хвост: целые кадры до него всё равно показываем.
                    AppLog.Debug("GifAnimator.Preflight: повреждённый хвост", ex);
                    truncated = true;
                }
                if (frameCount == 0) throw new FileFormatException("GIF contains no frames.");
                return new GifPreflight(width, height, frameCount, truncated);
            }
            finally { stream.Position = originalPosition; }
        }

        private static void SkipGifSubBlocks(BinaryReader reader, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int length = reader.ReadByte();
                if (length == 0) return;
                SkipGifBytes(reader, length);
            }
        }

        private static void SkipGifBytes(BinaryReader reader, int length)
        {
            var stream = reader.BaseStream;
            if (length > stream.Length - stream.Position)
                throw new FileFormatException("Truncated GIF block.");
            stream.Seek(length, SeekOrigin.Current);
        }

        private static void ValidateCompositionSize(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new FileFormatException("Invalid GIF dimensions.");
            long bytes = checked((long)width * height * BytesPerPixel);
            // Холст, restore point, пиксели исходного кадра, готовый кадр и минимум два в буфере.
            if (checked(bytes * 6) > CompositionWorkingBudgetBytes) ThrowGifMemoryLimit();
        }

        private static void ThrowGifMemoryLimit() => throw new InvalidOperationException(Loc.T(
            "This GIF exceeds the safe memory limit. Reduce its dimensions.",
            "Этот GIF превышает безопасный лимит памяти. Уменьшите размеры изображения.",
            "Este GIF supera el límite seguro de memoria. Reduzca sus dimensiones."));

        private static int LookaheadCapacity(int width, int height)
        {
            long frameBytes = (long)width * height * BytesPerPixel;
            return (int)Math.Clamp(LookaheadBudgetBytes / Math.Max(1, frameBytes), MinLookaheadFrames, MaxLookaheadFrames);
        }

        private sealed record ComposedFrame(BitmapSource Image, int DelayMs);
        private sealed record GifFirstFrame(BitmapSource Image, int DelayMs, int FrameCount, int Width, int Height);

        /// <summary>Состояние холста между кадрами одного круга анимации.</summary>
        private sealed class FrameCanvas
        {
            private readonly int _width, _height, _stride;
            private readonly byte[] _canvas;
            private byte[]? _restorePoint;

            internal FrameCanvas(int width, int height)
            {
                ValidateCompositionSize(width, height);
                _width = width; _height = height;
                _stride = checked(width * BytesPerPixel);
                _canvas = new byte[checked(_stride * height)];   // Pbgra32, полностью прозрачный
            }

            internal long FrameBytes => _canvas.LongLength;

            /// <summary>Новый круг анимации начинается с прозрачного холста.</summary>
            internal void Reset() => Array.Clear(_canvas);

            internal ComposedFrame ComposeNext(BitmapFrame frame, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var meta = frame.Metadata as BitmapMetadata;
                int delay = GetFrameDelay(meta);
                int left = GetInt(meta, "/imgdesc/Left") ?? 0;
                int top = GetInt(meta, "/imgdesc/Top") ?? 0;
                int disposal = GetInt(meta, "/grctlext/Disposal") ?? 0;

                // disposal = 3 ("restore to previous"): запоминаем холст до отрисовки кадра
                if (disposal == 3)
                {
                    _restorePoint ??= new byte[_canvas.Length];
                    Buffer.BlockCopy(_canvas, 0, _restorePoint, 0, _canvas.Length);
                }

                DrawFrameOnCanvas(frame, _canvas, _width, _height, _stride, left, top);

                // BitmapSource.Create копирует пиксели в собственный буфер WIC.
                var composed = BitmapSource.Create(_width, _height, 96, 96,
                    PixelFormats.Pbgra32, null, _canvas, _stride);
                composed.Freeze();

                // подготовка холста к следующему кадру
                if (disposal == 2)
                    ClearRect(_canvas, _width, _height, _stride, left, top, frame.PixelWidth, frame.PixelHeight);
                else if (disposal == 3 && _restorePoint != null)
                    Buffer.BlockCopy(_restorePoint, 0, _canvas, 0, _canvas.Length);

                return new ComposedFrame(composed, delay);
            }
        }

        /// <summary>
        /// Фоновый поток сборки. Владеет файлом и декодером (DispatcherObject привязан
        /// к потоку-создателю), отдаёт замороженные кадры через ограниченную очередь.
        /// </summary>
        private sealed class FrameProducer
        {
            private readonly string _path;
            private readonly CancellationTokenSource _cts;
            private readonly TaskCompletionSource<GifFirstFrame> _first =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private BlockingCollection<ComposedFrame>? _queue;

            internal FrameProducer(string path, CancellationToken external)
            {
                _path = path;
                // Отмена загрузки при листании — страховка: проигрыватель, потерянный без Stop(),
                // не держит поток и файл.
                _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
                var thread = new Thread(Run) { IsBackground = true, Name = "PhotoMusic GIF frames" };
                if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            internal Task<GifFirstFrame> FirstFrame => _first.Task;

            internal void Cancel()
            {
                try { _cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            /// <summary>Следующий готовый кадр без ожидания. false — кадр ещё собирается.</summary>
            internal bool TryTake(out ComposedFrame? frame, out bool finished)
            {
                frame = null;
                var queue = _queue;
                if (queue == null) { finished = false; return false; }
                try
                {
                    bool got = queue.TryTake(out frame);
                    finished = !got && queue.IsCompleted;
                    return got;
                }
                catch (ObjectDisposedException) { finished = true; return false; }
            }

            private void Run()
            {
                var token = _cts.Token;
                BlockingCollection<ComposedFrame>? queue = null;
                try
                {
                    using var stream = OpenGif(_path, token, out var preflight);
                    GifBitmapDecoder decoder;
                    try
                    {
                        decoder = new GifBitmapDecoder(stream,
                            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                    }
                    catch (Exception ex) when (IsBrokenFile(ex))
                    {
                        throw new GifUnsupportedException(ImageFileFormat.Gif,
                            Loc.T("the GIF is damaged", "GIF повреждён", "el GIF está dañado"), ex);
                    }

                    int count = Math.Min(decoder.Frames.Count, preflight.FrameCount);
                    if (count == 0)
                        throw new GifUnsupportedException(ImageFileFormat.Gif,
                            Loc.T("the GIF is damaged", "GIF повреждён", "el GIF está dañado"));

                    var canvas = new FrameCanvas(preflight.Width, preflight.Height);
                    queue = new BlockingCollection<ComposedFrame>(LookaheadCapacity(preflight.Width, preflight.Height));

                    ComposedFrame first;
                    try { first = canvas.ComposeNext(decoder.Frames[0], token); }
                    catch (Exception ex) when (IsBrokenFile(ex))
                    {
                        throw new GifUnsupportedException(ImageFileFormat.Gif,
                            Loc.T("the GIF is damaged", "GIF повреждён", "el GIF está dañado"), ex);
                    }

                    _queue = queue;
                    _first.TrySetResult(new GifFirstFrame(first.Image, first.DelayMs, count,
                        preflight.Width, preflight.Height));
                    if (count == 1) return; // статичный GIF

                    Produce(decoder, canvas, first, count, queue, token);
                }
                catch (OperationCanceledException)
                {
                    _first.TrySetCanceled(token);
                }
                catch (Exception ex)
                {
                    if (!_first.TrySetException(ex))
                        AppLog.Warn("GifAnimator.Producer", ex, AppLog.Describe(_path));
                }
                finally
                {
                    try { queue?.CompleteAdding(); }
                    catch (ObjectDisposedException) { }
                    _cts.Dispose();
                }
            }

            private static void Produce(GifBitmapDecoder decoder, FrameCanvas canvas, ComposedFrame first,
                int count, BlockingCollection<ComposedFrame> queue, CancellationToken token)
            {
                // Первый круг: собираем и, пока помещается, складываем в кэш.
                var cache = new List<ComposedFrame>(Math.Min(count, 1024)) { first };
                long cachedBytes = canvas.FrameBytes;
                bool caching = cachedBytes <= FrameCacheBudgetBytes;
                if (!caching) cache.Clear();

                int playable = count;
                for (int i = 1; i < count; i++)
                {
                    ComposedFrame frame;
                    try { frame = canvas.ComposeNext(decoder.Frames[i], token); }
                    catch (Exception ex) when (IsBrokenFile(ex))
                    {
                        // Повреждённый кадр: проигрываем всё, что до него.
                        AppLog.Warn("GifAnimator.DamagedFrame", ex, $"кадр {i} из {count}");
                        playable = i;
                        break;
                    }
                    if (caching)
                    {
                        cachedBytes += canvas.FrameBytes;
                        if (cachedBytes <= FrameCacheBudgetBytes) cache.Add(frame);
                        else { caching = false; cache.Clear(); cache.TrimExcess(); }
                    }
                    queue.Add(frame, token);
                }

                if (playable <= 1) return; // от анимации остался один кадр — он уже на экране

                if (caching)
                {
                    // Вся анимация в кэше: дальше без декодирования, только раздаём кадры.
                    while (true)
                        foreach (var frame in cache) queue.Add(frame, token);
                }

                // Потоковый режим: каждый круг собирается заново, вперёд — лишь буфер очереди.
                while (true)
                {
                    canvas.Reset();
                    for (int i = 0; i < playable; i++)
                        queue.Add(canvas.ComposeNext(decoder.Frames[i], token), token);
                }
            }
        }

        /// <summary>Накладывает кадр на холст с учётом прозрачности (source-over).</summary>
        private static void DrawFrameOnCanvas(BitmapSource frame, byte[] canvas,
            int canvasWidth, int canvasHeight, int canvasStride, int left, int top)
        {
            // приводим кадр (обычно индексный, с прозрачным цветом палитры) к Bgra32
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = frame;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();

            int fw = converted.PixelWidth;
            int fh = converted.PixelHeight;
            int srcStride = checked(fw * BytesPerPixel);
            var src = new byte[checked(srcStride * fh)];
            converted.CopyPixels(src, srcStride, 0);

            for (int y = 0; y < fh; y++)
            {
                int dstY = top + y;
                if (dstY < 0 || dstY >= canvasHeight) continue;

                int srcRow = y * srcStride;
                int dstRow = dstY * canvasStride;

                for (int x = 0; x < fw; x++)
                {
                    int dstX = left + x;
                    if (dstX < 0 || dstX >= canvasWidth) continue;

                    int s = srcRow + x * BytesPerPixel;
                    int d = dstRow + dstX * BytesPerPixel;

                    byte sa = src[s + 3];

                    // ключевой момент: полностью прозрачные пиксели не трогаем,
                    // иначе их чёрный RGB проступает как точки/пятна
                    if (sa == 0) continue;

                    // переводим источник в premultiplied (Pbgra32)
                    int sb = src[s + 0] * sa / 255;
                    int sg = src[s + 1] * sa / 255;
                    int sr = src[s + 2] * sa / 255;

                    if (sa == 255)
                    {
                        canvas[d + 0] = (byte)sb;
                        canvas[d + 1] = (byte)sg;
                        canvas[d + 2] = (byte)sr;
                        canvas[d + 3] = 255;
                    }
                    else
                    {
                        int inv = 255 - sa;
                        canvas[d + 0] = (byte)(sb + canvas[d + 0] * inv / 255);
                        canvas[d + 1] = (byte)(sg + canvas[d + 1] * inv / 255);
                        canvas[d + 2] = (byte)(sr + canvas[d + 2] * inv / 255);
                        canvas[d + 3] = (byte)(sa + canvas[d + 3] * inv / 255);
                    }
                }
            }
        }

        private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight,
            int canvasStride, int left, int top, int rectWidth, int rectHeight)
        {
            for (int y = 0; y < rectHeight; y++)
            {
                int dstY = top + y;
                if (dstY < 0 || dstY >= canvasHeight) continue;

                for (int x = 0; x < rectWidth; x++)
                {
                    int dstX = left + x;
                    if (dstX < 0 || dstX >= canvasWidth) continue;

                    int d = dstY * canvasStride + dstX * BytesPerPixel;
                    canvas[d + 0] = 0;
                    canvas[d + 1] = 0;
                    canvas[d + 2] = 0;
                    canvas[d + 3] = 0;
                }
            }
        }

        private static int GetFrameDelay(BitmapMetadata? meta)
        {
            int? hundredths = GetInt(meta, "/grctlext/Delay");
            if (hundredths == null) return DefaultDelayMs;

            int ms = hundredths.Value * 10;

            // как в браузерах: слишком маленькая задержка трактуется как 100 мс
            return ms < 20 ? DefaultDelayMs : ms;
        }

        /// <summary>Безопасно читает числовое значение метаданных (ushort/byte/short/int).</summary>
        private static int? GetInt(BitmapMetadata? meta, string query)
        {
            if (meta == null) return null;
            try
            {
                object? value = meta.GetQuery(query);
                return value switch
                {
                    ushort u => u,
                    byte b => b,
                    short s => s,
                    int i => i,
                    uint ui => (int)ui,
                    _ => null
                };
            }
            catch (Exception ex)
            {
                // часть GIF не содержит нужных блоков метаданных
                AppLog.Debug("GifAnimator.ReadMetadata", ex);
                return null;
            }
        }

        public void Start()
        {
            if (_stopped) return;
            if (!_firstShown)
            {
                _target.Source = _firstFrame;
                _firstShown = true;
            }
            IsPlaying = true;

            if (FrameCount <= 1) return;   // статичный GIF — таймер не нужен

            _timer.Interval = TimeSpan.FromMilliseconds(_currentDelayMs);
            _timer.Start();
        }

        public void Pause()
        {
            _timer.Stop();
            IsPlaying = false;
        }

        /// <summary>Окончательная остановка: фоновая сборка прекращается, файл закрывается.</summary>
        public void Stop()
        {
            _timer.Stop();
            IsPlaying = false;
            _stopped = true;
            _producer.Cancel();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_producer.TryTake(out var frame, out bool finished) && frame != null)
            {
                _target.Source = frame.Image;
                _currentDelayMs = frame.DelayMs;
                _timer.Interval = TimeSpan.FromMilliseconds(frame.DelayMs);
                return;
            }
            if (finished)
            {
                // Сборка завершилась (ошибка файла): остаёмся на последнем кадре.
                _timer.Stop();
                IsPlaying = false;
                return;
            }
            // Следующий кадр ещё собирается — коротко подождём, текущий остаётся на экране.
            _timer.Interval = TimeSpan.FromMilliseconds(LateFramePollMs);
        }
    }
}
