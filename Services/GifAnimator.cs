using System;
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
    /// Проигрыватель GIF с корректной сборкой кадров.
    ///
    /// Важно: WPF-декодер (GifBitmapDecoder) отдаёт кадры такими, как они лежат в файле, —
    /// то есть частичными (только изменившийся прямоугольник) и с прозрачным цветом палитры.
    /// Если просто показывать decoder.Frames[i], появляются чёрные точки/пятна и "мусор":
    /// прозрачные пиксели индексной палитры превращаются в чёрные, а области вне
    /// прямоугольника кадра остаются пустыми.
    ///
    /// Поэтому здесь каждый кадр заранее собирается на полноразмерном холсте (logical screen)
    /// в формате Pbgra32 с учётом смещения кадра (imgdesc), альфа-смешивания и способа
    /// очистки предыдущего кадра (disposal method).
    ///
    /// Быстродействие: сборка идёт в фоновом потоке (см. LoadAsync), поэтому интерфейс
    /// не замирает на больших GIF. Память ограничена потолком FrameMemoryBudgetBytes:
    /// если все кадры в полном размере в него не влезают, готовые кадры уменьшаются.
    /// </summary>
    public class GifAnimator
    {
        /// <summary>
        /// Анимацию из файла собрать нельзя. Вместо «сырого» FileFormatException наружу
        /// уходит это исключение с полем ActualFormat, чтобы вызывающий код понимал, что делать:
        ///  * ActualFormat != Gif — файл назван .gif, но внутри другой формат (WebP, PNG, JPEG,
        ///    MP4...). GifBitmapDecoder принимает только настоящий GIF и на остальных падает
        ///    с «Кодек не может использовать указанный тип потока»;
        ///  * ActualFormat == Gif — это GIF, но повреждённый или оборванный при закачке.
        /// Отмена при быстром листании (OperationCanceledException) сюда не заворачивается.
        /// </summary>
        public sealed class GifUnsupportedException : Exception
        {
            /// <summary>Формат, который на самом деле лежит в файле (определён по содержимому).</summary>
            public SniffedFormat ActualFormat { get; }

            public GifUnsupportedException(SniffedFormat actualFormat, string message, Exception? inner = null)
                : base(message, inner)
            {
                ActualFormat = actualFormat;
            }
        }

        private const int BytesPerPixel = 4;
        private const int DefaultDelayMs = 100;

        // Потолок памяти на все собранные кадры одного GIF.
        // Раньше кадры всегда собирались в полном размере: 200 кадров 800x600
        // занимали ~384 МБ несжатых пикселей. Теперь при превышении потолка
        // кадры пропорционально уменьшаются.
        private const long FrameMemoryBudgetBytes = 192L * 1024 * 1024;

        private readonly BitmapSource[] _frames;
        private readonly int[] _delaysMs;
        private readonly Image _target;
        private readonly DispatcherTimer _timer = new();
        private int _currentFrame;

        public bool IsPlaying { get; private set; }

        public int FrameCount => _frames.Length;

        private GifAnimator(ComposedGif composed, Image target)
        {
            _target = target;
            _frames = composed.Frames;
            _delaysMs = composed.DelaysMs;

            // сглаживание при масштабировании: с premultiplied-альфой тёмной каймы не будет
            RenderOptions.SetBitmapScalingMode(_target, BitmapScalingMode.HighQuality);

            _timer.Tick += Timer_Tick;
        }

        /// <summary>
        /// Читает файл и собирает кадры в фоновом потоке, затем возвращает готовый
        /// проигрыватель уже в UI-потоке (DispatcherTimer и Image требуют именно его).
        /// Отмена через token нужна при быстром листании: недоделанная сборка
        /// прекращается и не тратит процессор впустую.
        /// </summary>
        public static async Task<GifAnimator> LoadAsync(string path, Image target, CancellationToken token)
        {
            var composed = await Task.Run(() => Compose(path, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            return new GifAnimator(composed, target);
        }

        private sealed class ComposedGif
        {
            public BitmapSource[] Frames = Array.Empty<BitmapSource>();
            public int[] DelaysMs = Array.Empty<int>();
        }

        private static ComposedGif Compose(string path, CancellationToken token)
        {
            // Читаем потоком: BitmapCacheOption.OnLoad всё равно закэширует кадры в памяти,
            // а лишней копии всех байтов файла (File.ReadAllBytes) при этом не возникает
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan);

            // Расширение .gif не гарантирует GIF внутри: сначала смотрим содержимое.
            // Detect возвращает позицию потока назад, поэтому декодер читает файл с нуля
            var actual = ImageFormatSniffer.Detect(stream);
            if (actual != SniffedFormat.Gif)
            {
                throw new GifUnsupportedException(actual,
                    $"The file has a .gif extension but its content is {actual}.");
            }

            try
            {
                var decoder = new GifBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                ComposeFrames(decoder, token, out var frames, out var delaysMs);
                return new ComposedGif { Frames = frames, DelaysMs = delaysMs };
            }
            catch (Exception ex) when (IsBrokenFile(ex))
            {
                // Настоящий GIF, но оборванный/повреждённый: пусть вызывающий код покажет
                // хотя бы первый читаемый кадр как обычную картинку
                throw new GifUnsupportedException(SniffedFormat.Gif,
                    "The GIF file is damaged or truncated.", ex);
            }
        }

        /// <summary>
        /// Сбой разбора самого файла (а не ошибка в нашем коде и не отмена): так WPF/WIC
        /// сообщают о битых или оборванных кадрах. OperationCanceledException сюда
        /// намеренно не входит — это штатный сценарий при быстром листании.
        /// IOException (файл занят, диск недоступен) тоже не входит: это не «битый файл».
        /// </summary>
        private static bool IsBrokenFile(Exception ex) =>
            ex is FileFormatException ||
            ex is NotSupportedException ||
            ex is COMException ||
            ex is ArgumentException ||
            ex is IndexOutOfRangeException ||
            ex is OverflowException;

        /// <summary>Собирает все кадры GIF в готовые к показу изображения.</summary>
        private static void ComposeFrames(GifBitmapDecoder decoder, CancellationToken token,
            out BitmapSource[] frames, out int[] delaysMs)
        {
            int count = decoder.Frames.Count;
            frames = new BitmapSource[count];
            delaysMs = new int[count];

            if (count == 0) return;

            var screenMetadata = decoder.Metadata as BitmapMetadata;
            int width = GetInt(screenMetadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
            int height = GetInt(screenMetadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;

            // на всякий случай: холст должен вмещать любой кадр
            foreach (var f in decoder.Frames)
            {
                if (f.PixelWidth > width) width = f.PixelWidth;
                if (f.PixelHeight > height) height = f.PixelHeight;
            }

            if (width <= 0 || height <= 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var raw = decoder.Frames[i];
                    raw.Freeze();
                    frames[i] = raw;
                    delaysMs[i] = DefaultDelayMs;
                }
                return;
            }

            // Во сколько раз уменьшить готовые кадры, чтобы уложиться в потолок памяти
            double frameScale = GetFrameScale(width, height, count);

            int stride = width * BytesPerPixel;
            var canvas = new byte[stride * height];   // Pbgra32, полностью прозрачный
            byte[]? restorePoint = null;

            for (int i = 0; i < count; i++)
            {
                // Быстрое листание: если картинку уже сменили, дальше собирать нечего
                token.ThrowIfCancellationRequested();

                var frame = decoder.Frames[i];
                var meta = frame.Metadata as BitmapMetadata;

                delaysMs[i] = GetFrameDelay(meta);

                int left = GetInt(meta, "/imgdesc/Left") ?? 0;
                int top = GetInt(meta, "/imgdesc/Top") ?? 0;
                int disposal = GetInt(meta, "/grctlext/Disposal") ?? 0;

                // disposal = 3 ("restore to previous"): запоминаем холст до отрисовки кадра
                if (disposal == 3) restorePoint = (byte[])canvas.Clone();

                DrawFrameOnCanvas(frame, canvas, width, height, stride, left, top);

                var composed = BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Pbgra32, null, (byte[])canvas.Clone(), stride);
                composed.Freeze();
                frames[i] = frameScale < 1.0 ? Downscale(composed, frameScale) : composed;

                // подготовка холста к следующему кадру
                if (disposal == 2)
                {
                    // "restore to background": область кадра становится прозрачной
                    ClearRect(canvas, width, height, stride,
                        left, top, frame.PixelWidth, frame.PixelHeight);
                }
                else if (disposal == 3 && restorePoint != null)
                {
                    Array.Copy(restorePoint, canvas, canvas.Length);
                }
            }
        }

        /// <summary>
        /// Коэффициент уменьшения кадров: 1.0, если все кадры в полном размере
        /// укладываются в потолок памяти. Ниже 0.25 не опускаемся — анимация
        /// должна оставаться узнаваемой.
        /// </summary>
        private static double GetFrameScale(int width, int height, int count)
        {
            long needed = (long)width * height * BytesPerPixel * count;
            if (needed <= FrameMemoryBudgetBytes) return 1.0;

            double scale = Math.Sqrt(FrameMemoryBudgetBytes / (double)needed);
            return Math.Max(0.25, scale);
        }

        /// <summary>
        /// Уменьшает собранный кадр в отдельный буфер. Копируем пиксели, а не оставляем
        /// TransformedBitmap: иначе полноразмерный кадр остался бы в памяти как источник.
        /// </summary>
        private static BitmapSource Downscale(BitmapSource source, double scale)
        {
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));

            int w = transformed.PixelWidth;
            int h = transformed.PixelHeight;
            if (w <= 0 || h <= 0) return source;

            int stride = w * BytesPerPixel;
            var pixels = new byte[stride * h];
            transformed.CopyPixels(pixels, stride, 0);

            var flat = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            flat.Freeze();
            return flat;
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
            int srcStride = fw * BytesPerPixel;
            var src = new byte[srcStride * fh];
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
            if (_frames.Length == 0) return;

            _target.Source = _frames[_currentFrame];
            IsPlaying = true;

            if (_frames.Length == 1) return;   // статичный GIF — таймер не нужен

            _timer.Interval = TimeSpan.FromMilliseconds(_delaysMs[_currentFrame]);
            _timer.Start();
        }

        public void Pause()
        {
            _timer.Stop();
            IsPlaying = false;
        }

        public void Stop()
        {
            _timer.Stop();
            IsPlaying = false;
            _currentFrame = 0;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            _currentFrame = (_currentFrame + 1) % _frames.Length;
            _target.Source = _frames[_currentFrame];
            _timer.Interval = TimeSpan.FromMilliseconds(_delaysMs[_currentFrame]);
        }
    }
}
