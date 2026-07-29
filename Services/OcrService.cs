using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Распознавание текста на изображениях через встроенный OCR Windows.
    /// Картинка прогоняется через каждый установленный языковой движок
    /// (язык профиля + английский + испанский), результат каждого возвращается отдельно.
    /// </summary>
    public static class OcrService
    {
        /// <summary>Один вариант распознавания: название языка и прочитанный им текст.</summary>
        public sealed record OcrVariant(string Language, string Code, string Text);

        public static async Task<List<OcrVariant>> RecognizeTextAsync(string imagePath)
        {
            var engines = CreateEngines();
            if (engines.Count == 0)
                throw new InvalidOperationException(
                    "Windows has no OCR language packs installed. " +
                    "Add a language in Windows Settings -> Time & Language.");

            var softwareBitmap = LoadAsSoftwareBitmap(imagePath, (int)OcrEngine.MaxImageDimension);
            try
            {
                var variants = new List<OcrVariant>();

                foreach (var engine in engines)
                {
                    var result = await engine.RecognizeAsync(softwareBitmap);

                    var sb = new StringBuilder();
                    foreach (var line in result.Lines)
                        sb.AppendLine(line.Text);

                    var lang = engine.RecognizerLanguage;
                    var code = lang.LanguageTag.Split('-')[0].ToUpperInvariant();
                    variants.Add(new OcrVariant(lang.DisplayName, code, sb.ToString().Trim()));
                }

                return variants;
            }
            finally
            {
                softwareBitmap.Dispose();
            }
        }

        private static List<OcrEngine> CreateEngines()
        {
            var engines = new List<OcrEngine>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var profile = OcrEngine.TryCreateFromUserProfileLanguages();
            if (profile != null && seen.Add(profile.RecognizerLanguage.LanguageTag))
                engines.Add(profile);

            // Английский движок лучше читает ссылки и латиницу,
            // испанский - текст с ñ и ударениями; добавляем те из них, что установлены
            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            {
                var tag = language.LanguageTag;
                if (!tag.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                    !tag.StartsWith("es", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(tag)) continue;

                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine != null) engines.Add(engine);
            }

            // Ничего не нашли - берём первый доступный язык
            if (engines.Count == 0)
            {
                foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                {
                    var engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine != null) { engines.Add(engine); break; }
                }
            }

            return engines;
        }

        private static SoftwareBitmap LoadAsSoftwareBitmap(string path, int maxDimension)
        {
            // Декодируем через WPF - он понимает те же форматы, что приложение умеет показывать,
            // и не имеет проблем с русскими буквами в пути к файлу.
            BitmapSource source;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }

            // OCR-движок Windows не принимает слишком большие изображения - уменьшаем при необходимости
            int maxSide = Math.Max(source.PixelWidth, source.PixelHeight);
            if (maxSide > maxDimension)
            {
                double scale = (double)maxDimension / maxSide;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
            else if (maxSide < 2000)
            {
                // Мелкий текст (ссылки, подписи, скриншоты) распознаётся заметно хуже -
                // укрупняем небольшие изображения перед распознаванием
                double scale = Math.Min(2.0, (double)maxDimension / maxSide);
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            return SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        }
    }
}
