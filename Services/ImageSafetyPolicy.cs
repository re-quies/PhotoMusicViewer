using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Лимиты одной операции, не гарантия общего RSS WIC/WPF/GC.
    ///
    /// Проверки разделены на две части:
    /// 1. <see cref="ValidateSourceDimensions"/> — заголовок исходника. Это только защита
    ///    от «бомб» (подделанных размеров), а не бюджет памяти: при уменьшенном показе
    ///    WIC выдаёт пиксели потоком через масштабатор и полный кадр в памяти не держит.
    /// 2. <see cref="ValidateWorkingSet"/> — то, что реально попадёт в память: выходной
    ///    размер (после DecodePixelWidth/Height) с учётом формата пикселей.
    ///
    /// Раньше бюджет памяти проверялся по исходнику ещё до уменьшения, с оценкой
    /// 16 байт/пиксель при 512 МиБ, и всё больше ~33,5 Мп не открывалось вовсе:
    /// снимки 48–200 Мп со смартфонов, 45–61 Мп с камер, сканы A4 в 600 dpi, панорамы.
    /// </summary>
    internal static class ImageSafetyPolicy
    {
        internal const long MaxFileBytes = 128L * 1024 * 1024;

        /// <summary>Сторона исходника. 65535 — предел JPEG/GIF/WebP; панорамы шире 20000 px теперь открываются.</summary>
        internal const int MaxSourceDimension = 65535;

        /// <summary>
        /// Пиксели исходника (512 Мп). Покрывает 200-Мп смартфоны и pixel-shift камер
        /// (до ~400 Мп), но отсекает файлы, заявляющие миллиарды пикселей.
        /// </summary>
        internal const long MaxSourcePixels = 512L * 1024 * 1024;

        /// <summary>Бюджет полноразмерных пиксельных буферов одной операции.</summary>
        internal static readonly long MaxWorkingBytes =
            Environment.Is64BitProcess ? 2L * 1024 * 1024 * 1024 : 512L * 1024 * 1024;

        /// <summary>
        /// Сколько полноразмерных копий одновременно живёт в худшем случае:
        /// декодированный кадр + преобразование формата/поворот + буфер энкодера.
        /// </summary>
        internal const int WorkingCopies = 3;

        internal const int MaxFrames = 256;

        // Совместимость со старыми вызовами/тестами: худший случай 64 бит на пиксель.
        internal const int EstimatedWorkingBytesPerPixel = 8 * WorkingCopies;

        internal static void ValidateFileLength(long bytes)
        {
            if (bytes <= 0) throw new FileFormatException("Image file is empty.");
            if (bytes > MaxFileBytes) ThrowLimit();
        }

        /// <summary>Проверка заголовка исходника — до любого выделения пикселей.</summary>
        internal static void ValidateSourceDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new FileFormatException("Cannot determine safe image dimensions.");
            if (width > MaxSourceDimension || height > MaxSourceDimension ||
                (long)width * height > MaxSourcePixels)
                ThrowLimit();
        }

        /// <summary>
        /// Проверка того, что действительно ляжет в память: выходной размер и формат.
        /// bitsPerPixel ≤ 0 означает «неизвестно» — берётся худший случай 64 бит.
        /// </summary>
        internal static void ValidateWorkingSet(int width, int height, int bitsPerPixel)
        {
            ValidateSourceDimensions(width, height);
            long bytesPerPixel = Math.Max(4, ((bitsPerPixel <= 0 ? 64 : bitsPerPixel) + 7) / 8);
            long oneCopy = (long)width * height * bytesPerPixel;
            // Управляемые буферы (CopyPixels) индексируются int: одна копия должна в него влезать.
            if (oneCopy > int.MaxValue || oneCopy * WorkingCopies > MaxWorkingBytes) ThrowLimit();
        }

        /// <summary>Оценка памяти одного декодирования (все рабочие копии) — для общего
        /// бюджета параллельных декодирований <see cref="DecodeMemoryGate"/>.</summary>
        internal static long EstimateWorkingBytes(int width, int height, int bitsPerPixel)
        {
            long bytesPerPixel = Math.Max(4, ((bitsPerPixel <= 0 ? 64 : bitsPerPixel) + 7) / 8);
            return Math.Max(1, (long)Math.Max(1, width) * Math.Max(1, height) * bytesPerPixel * WorkingCopies);
        }

        /// <summary>Старая точка входа: полный буфер неизвестного формата (худший случай).</summary>
        internal static void ValidateDimensions(int width, int height) => ValidateWorkingSet(width, height, 64);

        /// <summary>Размер после уменьшения большей стороны до maxSide (как делает DecodePixelWidth/Height).</summary>
        internal static (int Width, int Height) ScaledSize(int width, int height, int maxSide)
        {
            if (maxSide <= 0 || Math.Max(width, height) <= maxSide) return (width, height);
            if (width >= height)
                return (maxSide, Math.Max(1, (int)Math.Round((double)height * maxSide / width)));
            return (Math.Max(1, (int)Math.Round((double)width * maxSide / height)), maxSide);
        }

        private static void ThrowLimit() => throw new InvalidOperationException(Loc.T(
            "This image exceeds the safe size or memory limit. Reduce its dimensions or file size first.",
            "Изображение превышает безопасный лимит размера или памяти. Сначала уменьшите размеры или объём файла.",
            "La imagen supera el límite seguro de tamaño o memoria. Reduzca sus dimensiones o el tamaño del archivo."));
    }
}
