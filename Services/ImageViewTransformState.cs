using System;

namespace PhotoMusicViewer.Services
{
    /// <summary>Eight lossless orientations: rotate first, then reflect horizontally
    /// in display coordinates. Operations compose in the order clicked.</summary>
    internal readonly record struct ImageViewTransformState
    {
        internal int Degrees { get; }
        internal bool Reflected { get; }
        internal bool IsIdentity => Degrees == 0 && !Reflected;
        internal bool SwapsDimensions => Degrees is 90 or 270;
        internal ImageViewTransformState(int degrees, bool reflected = false)
        {
            if (degrees % 90 != 0) throw new ArgumentException("Right-angle rotation required.");
            Degrees = (degrees % 360 + 360) % 360;
            Reflected = reflected;
        }
        internal ImageViewTransformState RotateRight() => new(Degrees + (Reflected ? -90 : 90), Reflected);
        internal ImageViewTransformState ReflectHorizontal() => new(Degrees, !Reflected);
        internal ImageViewTransformState ReflectVertical() => new(Degrees + 180, !Reflected);
        internal int ComposeOrientation(int orientation)
        {
            if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation));
            int[] clockwise = { 0, 6, 7, 8, 5, 2, 3, 4, 1 };
            int[] horizontal = { 0, 2, 1, 4, 3, 6, 5, 8, 7 };
            for (int i = 0; i < Degrees / 90; i++) orientation = clockwise[orientation];
            return Reflected ? horizontal[orientation] : orientation;
        }
    }
}
