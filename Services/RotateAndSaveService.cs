using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    public static class RotateAndSaveService
    {
        // Compatibility API: explicit original replacement, with persistent recovery backup.
        public static void RotateAndSave(string path, int degrees, FileOperationContext? operation = null) =>
            SaveRotation(path, degrees, copy: false, allowReencode: false, operation);

        public static string SaveRotation(string path, int degrees, bool copy = true, bool allowReencode = false, FileOperationContext? operation = null) =>
            SaveViewTransform(path, new ImageViewTransformState(degrees), copy, allowReencode, operation);

        internal static string SaveViewTransform(string path, ImageViewTransformState transform, bool copy = true, bool allowReencode = false, FileOperationContext? operation = null)
        {
            string suffix = transform.Reflected ? "_mirrored" : "_rotated";
            var version = FileVersion.Read(path);
            if (JpegLossless.IsJpeg(path) && !allowReencode)
                return ImageSaveWriter.Write(path, copy, suffix, output => JpegLossless.Transform(path, output, transform, strip: false, operation?.Token ?? default), operation, version);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            BitmapEncoder encoder = ext switch
            {
                ".jpg" or ".jpeg" or ".jfif" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".png" => new PngBitmapEncoder(), ".bmp" => new BmpBitmapEncoder(),
                ".tiff" or ".tif" => new TiffBitmapEncoder(),
                _ => throw new NotSupportedException($"No encoder available for {ext}")
            };
            operation?.Checkpoint(FileOperationStage.Decoding);
            var source = SafeImageDecoder.LoadDecoded(path, 0, rejectMultiPageTiff: true, token: operation?.Token ?? default).Image;
            var rotated = ImageViewTransform.Apply(source, transform);
            if (rotated.CanFreeze) rotated.Freeze();
            operation?.Checkpoint(FileOperationStage.Encoding);
            encoder.Frames.Add(BitmapFrame.Create(rotated));
            return ImageSaveWriter.Write(path, copy, suffix, stream => encoder.Save(stream), operation, version);
        }

    }
}
