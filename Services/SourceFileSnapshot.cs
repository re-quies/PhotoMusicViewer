using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace PhotoMusicViewer.Services
{
    /// <summary>Identity/version plus SHA-256: timestamps alone do not prove unchanged content.</summary>
    internal sealed record SourceFileSnapshot(FileVersion Version, string Hash)
    {
        internal static SourceFileSnapshot Capture(string path, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan);
            ImageSafetyPolicy.ValidateFileLength(file.Length);
            var before = FileVersion.Read(file, path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            int read;
            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            if (FileVersion.Read(file, path) != before || FileVersion.Read(path) != before)
                throw new IOException("Source changed while it was being checked.");
            return new SourceFileSnapshot(before, Convert.ToHexString(hash.GetHashAndReset()));
        }

        internal bool Matches(string path, CancellationToken token = default)
        {
            try { return Capture(path, token) == this; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                AppLog.Debug("SourceFileSnapshot.Check", ex);
                return false; // missing, busy or unreadable: never grant permission to delete
            }
        }

        internal void RequireMatch(string path, CancellationToken token = default)
        {
            if (!Matches(path, token))
                throw new IOException(Loc.T(
                    "The original WebP changed or is unavailable. No converted copy was saved; the original was not deleted.",
                    "Исходный WebP изменился или недоступен. Конвертированная копия не сохранена; исходник не удалён.",
                    "El WebP original cambió o no está disponible. No se guardó una copia; no se eliminó el original."));
        }
    }
}
