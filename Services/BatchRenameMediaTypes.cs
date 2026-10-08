using System;
using System.Collections.Generic;
using System.Linq;
namespace PhotoMusicViewer.Services
{
    [Flags]
    public enum RenameMediaCategories { None = 0, Photos = 1, Videos = 2, Music = 4 }
    /// <summary>Расширения для переименования, не список декодеров/воспроизведения.</summary>
    internal static class BatchRenameMediaTypes
    {
        internal static readonly string[] Photos = {
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tiff", ".tif", ".gif", ".heic", ".heif", ".hif",
            ".avif", ".apng", ".jxl", ".jxr", ".wdp", ".hdp", ".tga", ".psd", ".psb", ".svg", ".ico",
            ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".rwl",
            ".pef", ".dng", ".srw", ".x3f", ".3fr", ".fff", ".iiq", ".mos", ".mrw", ".erf", ".kdc", ".dcr", ".raw" };
        internal static readonly string[] Videos = {
            ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpeg", ".mpg", ".mpe", ".m2v",
            ".mts", ".m2ts", ".vob", ".ogv", ".3gp", ".3g2", ".flv", ".f4v", ".asf", ".divx" };
        internal static readonly string[] Music = {
            ".mp3", ".flac", ".wav", ".ogg", ".opus", ".aac", ".m4a", ".m4b", ".m4r", ".wma", ".aif", ".aiff", ".aifc",
            ".alac", ".ape", ".wv", ".mka", ".mp2", ".mpa", ".oga", ".spx", ".ac3", ".eac3", ".dts", ".amr", ".au", ".snd" };
        internal static readonly string[] All = Photos.Concat(Videos).Concat(Music).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        internal static string[] Selected(RenameMediaCategories categories)
        {
            const RenameMediaCategories valid = RenameMediaCategories.Photos | RenameMediaCategories.Videos | RenameMediaCategories.Music;
            if (categories == RenameMediaCategories.None || (categories & ~valid) != 0)
                throw new ArgumentOutOfRangeException(nameof(categories), "Select at least one supported media category.");
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (categories.HasFlag(RenameMediaCategories.Photos)) result.UnionWith(Photos);
            if (categories.HasFlag(RenameMediaCategories.Videos)) result.UnionWith(Videos);
            if (categories.HasFlag(RenameMediaCategories.Music)) result.UnionWith(Music);
            return result.ToArray();
        }
        internal static string[] Excluded(RenameMediaCategories categories)
        {
            var selected = Selected(categories).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return All.Where(ext => !selected.Contains(ext)).ToArray();
        }
    }
}
