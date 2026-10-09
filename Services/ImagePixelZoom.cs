using System;

namespace PhotoMusicViewer.Services
{
    // Physical screen pixels / original oriented image pixels. Independent of
    // bitmap metadata DPI and of the resolution of a temporary decoded preview.
    internal static class ImagePixelZoom
    {
        internal const double MaxPixelScale = 100; // 10,000 percent
        private static bool Positive(double value) => double.IsFinite(value) && value > 0;

        internal static double RenderedWidth(double elementWidth, double elementHeight,
            double sourceWidthDip, double sourceHeightDip)
        {
            if (!Positive(elementWidth) || !Positive(elementHeight) ||
                !Positive(sourceWidthDip) || !Positive(sourceHeightDip)) return 0;
            return sourceWidthDip * Math.Min(elementWidth / sourceWidthDip, elementHeight / sourceHeightDip);
        }

        internal static double FitPixelScale(double renderedWidthDip, int originalWidth, double screenDpiScaleX)
        {
            if (!Positive(renderedWidthDip) || originalWidth <= 0 || !Positive(screenDpiScaleX)) return 0;
            double scale = renderedWidthDip / originalWidth * screenDpiScaleX;
            return Positive(scale) ? scale : 0;
        }

        internal static double OneToOneRelativeScale(double fitPixelScale)
        {
            if (!Positive(fitPixelScale)) return 0;
            double scale = 1 / fitPixelScale;
            return Positive(scale) ? scale : 0;
        }

        internal static double ClampWheelScale(double candidate, double current, double fitPixelScale)
        {
            // Resizing/monitor changes preserve the existing transform. If that
            // makes the current physical zoom exceed the ceiling, wheel-up must
            // not shrink it; wheel-down still makes the usual 10% step.
            // A deliberate 1:1 click may be below the usual fitted-relative
            // minimum. Preserve smooth wheel-up and do not shrink further there.
            return Math.Clamp(candidate, Math.Min(0.2, current), Math.Max(current, MaxRelativeScale(fitPixelScale)));
        }

        internal static double MaxRelativeScale(double fitPixelScale)
        {
            // Never shrink the fitted/reset view, even for an exceptional tiny
            // image whose fitted size already exceeds 10,000 percent.
            return Positive(fitPixelScale) ? Math.Max(1, MaxPixelScale / fitPixelScale) : MaxPixelScale;
        }
    }
}
