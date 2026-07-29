using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Чтение и применение EXIF-ориентации (тег 274). Файл не изменяется —
    /// поворот/отражение применяются только к картинке в памяти.
    /// </summary>
    public static class ExifOrientationService
    {
        public static int GetOrientation(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata metadata)
                {
                    // JPEG хранит тег в /app1/ifd/, TIFF — в /ifd/
                    foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    {
                        if (metadata.ContainsQuery(query) &&
                            metadata.GetQuery(query) is ushort value && value >= 1 && value <= 8)
                        {
                            return value;
                        }
                    }
                }
            }
            catch
            {
                // формат не поддерживает такие запросы (например PNG/BMP) — считаем ориентацию нормальной
            }
            return 1;
        }

        public static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
        {
            if (orientation <= 1 || orientation > 8) return source;

            int rotateDegrees = orientation switch
            {
                3 or 4 => 180,
                5 or 6 => 90,
                7 or 8 => 270,
                _ => 0
            };
            bool flipHorizontal = orientation is 2 or 4 or 5 or 7;

            BitmapSource result = source;

            if (rotateDegrees != 0)
                result = new TransformedBitmap(result, new RotateTransform(rotateDegrees));

            if (flipHorizontal)
                result = new TransformedBitmap(result, new ScaleTransform(-1, 1));

            if (result.CanFreeze) result.Freeze();
            return result;
        }
    }
}
