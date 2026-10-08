using System;
namespace PhotoMusicViewer.Services
{
    internal static class RecentShortcutPolicy
    {
        // Unresolved, virtual, corrupted or unsupported shortcuts are never deleted by name.
        internal static bool Matches(string? resolvedTarget, string openedFullPath) =>
            !string.IsNullOrWhiteSpace(resolvedTarget) && !string.IsNullOrWhiteSpace(openedFullPath)
            && string.Equals(resolvedTarget, openedFullPath, StringComparison.OrdinalIgnoreCase);
    }
}
