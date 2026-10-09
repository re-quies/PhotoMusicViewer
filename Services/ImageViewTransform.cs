using System.Windows.Media.Imaging;

namespace PhotoMusicViewer.Services
{
    internal static class ImageViewTransform
    {
        // Reuse the established EXIF pixel transform: the display and save paths
        // share one orientation and the same rotate-then-reflect ordering.
        internal static BitmapSource Apply(BitmapSource source, ImageViewTransformState state) =>
            ExifOrientationService.ApplyOrientation(source, state.ComposeOrientation(1));
    }
}
