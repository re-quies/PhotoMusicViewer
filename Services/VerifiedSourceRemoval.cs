using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    internal enum SourceRemovalResult { Removed, Declined, SourceChanged }

    /// <summary>
    /// Shell deletes paths, not the already-checked file handle. Isolate the source under
    /// a unique journaled name, then verify again. A replacement at the original path
    /// is never passed to Shell. A mismatch/cancel/error restores without overwriting.
    /// </summary>
    internal static class VerifiedSourceRemoval
    {
        internal static Action<string, string> MoveSourceFile = (source, staged) => File.Move(source, staged);

        internal static SourceRemovalResult Recycle(string path, SourceFileSnapshot expected, Func<string, bool> recycle)
        {
            ArgumentNullException.ThrowIfNull(recycle);
            path = Path.GetFullPath(path);
            if (!expected.Matches(path)) return SourceRemovalResult.SourceChanged;
            string staged = SettingsArtifacts.NewArtifactPath(path, "bak");
            TempArtifactJournal.TrackRequired(staged, path, TempArtifactJournal.Kind.SourceRemoval);
            bool moved = false;
            try
            {
                MoveSourceFile(path, staged); // never overwrite an existing staging file
                moved = true;
                // Covers a replacement/change between the first check and the move.
                if (!expected.Matches(staged)) return SourceRemovalResult.SourceChanged;
                bool removed = recycle(staged);
                if (removed && !File.Exists(staged)) return SourceRemovalResult.Removed;
                return SourceRemovalResult.Declined;
            }
            finally
            {
                if (moved && File.Exists(staged))
                {
                    try
                    {
                        // If sync created a new original, do not overwrite it. Retain both files.
                        File.Move(staged, path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException(Loc.T(
                            $"The source was not deleted, but could not be put back. It is preserved at \"{staged}\". The recovery journal was kept; no existing file was overwritten.",
                            $"Исходник не удалён, но вернуть его на место не удалось. Он сохранён: «{staged}». Журнал восстановления оставлен; существующие файлы не перезаписаны.",
                            $"El original no se eliminó, pero no pudo restaurarse. Se conserva en «{staged}». Se mantuvo el diario; no se sobrescribió ningún archivo."), ex);
                    }
                }
                TempArtifactJournal.Release(staged); // keep the entry if any artifact remains
            }
        }
    }
}
