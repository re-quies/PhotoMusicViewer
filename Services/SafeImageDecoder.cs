using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    internal static class SafeImageDecoder
    {
        // Раньше здесь была общая блокировка «одно декодирование за раз»: миниатюры,
        // соседние фото и полный размер ждали друг друга, остальные ядра простаивали.
        // Теперь параллельно, но в пределах общего бюджета памяти (DecodeMemoryGate):
        // заголовок читается без очереди, пиксели — после получения своей доли.
        internal sealed record Decoded(BitmapSource Image, long FileBytes, int NaturalWidth, int NaturalHeight);

        public static BitmapSource LoadOriented(string path, bool rejectMultiPageTiff = false) =>
            LoadDecoded(path, 0, rejectMultiPageTiff).Image;

        internal static Decoded LoadDecoded(string path, int maxSide,
            bool rejectMultiPageTiff = false, CancellationToken token = default,
            bool preferEmbeddedThumbnail = false, bool detachToBgra32 = false)
        {
            if (maxSide < 0) throw new ArgumentOutOfRangeException(nameof(maxSide));
            token.ThrowIfCancellationRequested();
            // Один дескриптор на проверку и декодирование; без ReadAllBytes и
            // без замены файла между проверкой заголовка и чтением пикселей.
            using var stream = OpenValidatedStream(path, token);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation |
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            EnsureSafeTiffReplacement(decoder, rejectMultiPageTiff);
            if (decoder.Frames.Count == 0 || decoder.Frames.Count > ImageSafetyPolicy.MaxFrames)
                throw new InvalidOperationException("Unsupported image frame count.");
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            // Заголовок проверяется только на правдоподобие; бюджет памяти — по тому
            // размеру, который реально будет выделен (после уменьшения для показа).
            ImageSafetyPolicy.ValidateSourceDimensions(width, height);
            if (frame.Format.BitsPerPixel > 64)
                throw new InvalidOperationException("Image pixel format exceeds the supported memory budget.");
            bool downscale = maxSide > 0 && Math.Max(width, height) > maxSide;
            var (outWidth, outHeight) = ImageSafetyPolicy.ScaledSize(width, height, downscale ? maxSide : 0);
            ImageSafetyPolicy.ValidateWorkingSet(outWidth, outHeight, frame.Format.BitsPerPixel);
            int orientation = ExifOrientationService.GetOrientation(frame);
            token.ThrowIfCancellationRequested();
            bool swapped = orientation is 5 or 6 or 7 or 8;
            int naturalWidth = swapped ? height : width, naturalHeight = swapped ? width : height;

            // Миниатюра: встроенное в EXIF превью, если оно не меньше нужного размера
            // и с теми же пропорциями. Декодировать сам снимок тогда не нужно.
            if (preferEmbeddedThumbnail && downscale &&
                TryEmbeddedThumbnail(frame, width, height, maxSide, orientation) is { } embedded)
                return new Decoded(detachToBgra32 ? ToBgra32(embedded, token) : embedded,
                    stream.Length, naturalWidth, naturalHeight);

            using var memory = DecodeMemoryGate.Shared.Acquire(
                ImageSafetyPolicy.EstimateWorkingBytes(outWidth, outHeight, frame.Format.BitsPerPixel), token);
            token.ThrowIfCancellationRequested();

            BitmapSource pixels;
            if (downscale)
            {
                try
                {
                    pixels = DecodeScaled(stream, width, height, maxSide, BitmapCreateOptions.None);
                }
                catch (Exception ex) when (IsBrokenColorProfile(ex))
                {
                    // Битый ICC-профиль (ArgumentException «Значение не попадает в ожидаемый
                    // диапазон» из ColorContext): пиксели целы, профиль не читается. Показываем
                    // снимок без цветокоррекции по профилю, а не «Не удалось открыть».
                    AppLog.Warn("SafeImageDecoder.ColorProfileFallback", ex, AppLog.Describe(path));
                    token.ThrowIfCancellationRequested();
                    try { pixels = DecodeScaled(stream, width, height, maxSide, BitmapCreateOptions.IgnoreColorProfile); }
                    catch (Exception retry) when (IsBrokenMetadata(retry) || IsBrokenColorProfile(retry))
                    {
                        AppLog.Warn("SafeImageDecoder.MetadataFallback", retry, AppLog.Describe(path));
                        pixels = CopySelectedFrame(frame, maxSide);
                    }
                }
                catch (Exception ex) when (IsBrokenMetadata(ex))
                {
                    // Только проверенный по бюджету первый кадр; не OnLoad всех страниц.
                    AppLog.Warn("SafeImageDecoder.MetadataFallback", ex, AppLog.Describe(path));
                    pixels = CopySelectedFrame(frame, maxSide);
                }
            }
            else pixels = CopySelectedFrame(frame, 0);

            token.ThrowIfCancellationRequested();
            pixels = ExifOrientationService.ApplyOrientation(pixels, orientation);
            ImageSafetyPolicy.ValidateWorkingSet(pixels.PixelWidth, pixels.PixelHeight, pixels.Format.BitsPerPixel);
            // Копия в собственный буфер — внутри доли памяти, пока источник ещё жив.
            if (detachToBgra32) pixels = ToBgra32(pixels, token);
            if (pixels.CanFreeze) pixels.Freeze();
            return new Decoded(pixels, stream.Length, naturalWidth, naturalHeight);
        }

        /// <summary>
        /// Плоский замороженный BGRA32-буфер без цепочки декодер → поворот → масштаб.
        /// Его размер точно известен (ширина × высота × 4), поэтому кэш считает вес
        /// по факту, а не с запасом «16 байт на пиксель».
        /// </summary>
        internal static BitmapSource ToBgra32(BitmapSource source, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(bgra.PixelWidth * 4);
            byte[] buffer = new byte[checked(stride * bgra.PixelHeight)];
            bgra.CopyPixels(buffer, stride, 0);
            token.ThrowIfCancellationRequested();
            var image = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY,
                PixelFormats.Bgra32, null, buffer, stride);
            image.Freeze();
            return image;
        }

        /// <summary>
        /// Встроенная миниатюра JPEG/TIFF (EXIF IFD1). Используется, только если её длинная
        /// сторона не меньше запрошенной (иначе на HiDPI она будет мыльной) и пропорции
        /// совпадают с основным снимком (у некоторых камер превью 160×120 с чёрными полями).
        /// Ориентация у превью та же, что у снимка, поэтому поворот применяется так же.
        /// </summary>
        private static BitmapSource? TryEmbeddedThumbnail(BitmapFrame frame, int width, int height, int maxSide, int orientation)
        {
            try
            {
                if (frame.Thumbnail is not BitmapSource thumb) return null;
                int tw = thumb.PixelWidth, th = thumb.PixelHeight;
                if (tw <= 0 || th <= 0 || Math.Max(tw, th) < maxSide) return null;
                double main = (double)width / height, own = (double)tw / th;
                if (Math.Abs(main - own) / main > 0.02) return null;
                BitmapSource pixels = CopySelectedFrame(thumb, maxSide);
                pixels = ExifOrientationService.ApplyOrientation(pixels, orientation);
                if (pixels.CanFreeze) pixels.Freeze();
                return pixels;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or
                                       COMException or FileFormatException or ArgumentException)
            {
                AppLog.Debug("SafeImageDecoder.EmbeddedThumbnail", ex);
                return null;
            }
        }

        private static BitmapSource CopySelectedFrame(BitmapSource frame, int maxSide)
        {
            BitmapSource source = frame;
            int side = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (maxSide > 0 && side > maxSide)
                source = new TransformedBitmap(frame, new ScaleTransform((double)maxSide / side, (double)maxSide / side));
            var copy = new CachedBitmap(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (copy.CanFreeze) copy.Freeze();
            return copy;
        }

        internal static FileStream OpenValidatedStream(string path, CancellationToken token = default)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan);
            try
            {
                token.ThrowIfCancellationRequested();
                ImageSafetyPolicy.ValidateFileLength(stream.Length);
                if (ImageFormatSniffer.Detect(stream) == ImageFileFormat.Gif)
                    GifAnimator.ValidateGifBeforeDecode(stream, token);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        internal static byte[] ReadOriginalForUpload(string path, CancellationToken token = default)
        {
            using var stream = OpenValidatedStream(path, token);
            // Direct upload additionally capped at 15 MiB, before allocation.
            if (stream.Length > 15L * 1024 * 1024) throw new InvalidOperationException("Direct upload is too large.");
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) throw new FileFormatException("Image contains no frames.");
            // Байты файла отправляются как есть, пиксели здесь не распаковываются.
            ImageSafetyPolicy.ValidateSourceDimensions(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static void EnsureSafeTiffReplacement(BitmapDecoder decoder, bool replacing)
        {
            if (replacing && decoder is TiffBitmapDecoder && decoder.Frames.Count > 1)
                throw new InvalidOperationException(Loc.T(
                    "Replacing a multi-page TIFF is disabled to prevent loss of pages. Save a separate copy instead.",
                    "Замена многостраничного TIFF отключена, чтобы не потерять страницы. Сохраните отдельную копию.",
                    "La sustitución de un TIFF multipágina está desactivada para evitar perder páginas. Guarde una copia separada."));
        }

        private static BitmapSource DecodeScaled(Stream stream, int width, int height, int maxSide, BitmapCreateOptions options)
        {
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = options;
            if (width >= height) bitmap.DecodePixelWidth = maxSide;
            else bitmap.DecodePixelHeight = maxSide;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            return bitmap;
        }

        /// <summary>
        /// Ошибка чтения встроенного цветового профиля. WPF сообщает о ней ArgumentException
        /// (E_INVALIDARG) из ColorContext.GetColorContextsHelper — тип слишком общий, поэтому
        /// узнаём её по месту возникновения, а не по одному лишь типу.
        /// </summary>
        public static bool IsBrokenColorProfile(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is not (ArgumentException or COMException or FileFormatException or NotSupportedException)) continue;
                string? trace = e.StackTrace;
                if (trace != null && trace.Contains("ColorContext", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static bool IsBrokenMetadata(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is FileFormatException) return true;
                if (e is COMException com && (uint)com.HResult is 0x88982F8E or 0x88982F91 or
                    0x88982F8D or 0x88982F63 or 0x88982F52 or 0x88982F42 or 0x88982F90) return true;
            }
            return false;
        }
    }
}
