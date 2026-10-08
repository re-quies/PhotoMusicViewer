using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Запись результата правки (поворот, очистка метаданных, обрезка). По умолчанию — копия.
    /// При явной замене оригинал целиком остаётся рядом: «имя.ext.pmv-original-ДАТА-ХЕШ.bak».
    ///
    /// - ХЕШ — начало SHA-256 сохранённого файла. По нему «Вернуть оригинал» работает и после
    ///   перезапуска: копия подходит, только если файл с тех пор не менялся. Список путей
    ///   нигде не хранится — копии находятся по имени рядом с фото.
    /// - Если замена сорвалась после того, как Windows уже убрала оригинал в резервную копию,
    ///   оригинал сразу возвращается на место (раньше он оставался «спрятанным» в .bak).
    /// - После «Вернуть оригинал» ставшие ненужными копии возвращаются вызывающему, чтобы
    ///   переместить их в корзину; «Удалить резервную копию» делает то же без восстановления.
    /// </summary>
    internal static class ImageSaveWriter
    {
        internal const string OriginalMarker = ".pmv-original-";
        internal const string EditedMarker = ".pmv-edited-";

        private sealed record Backup(string Path, string SavedHash, string OriginalHash);
        /// <summary>Резервная копия на диске. SavedHashPrefix == null — копия старой версии (имя с GUID) или «отменённая правка».</summary>
        internal sealed record DiskBackup(string Path, string Owner, string? SavedHashPrefix, bool Edited, DateTime Created);
        internal sealed record RestoreResult(IReadOnlyList<string> RedundantBackups);

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Backup> Backups = new(StringComparer.OrdinalIgnoreCase);
        // Папка → имя файла → число резервных копий оригинала (для кнопок; заполняется при открытии папки).
        private static readonly Dictionary<string, Dictionary<string, int>> Index = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex NewName = new(@"^(\d{8}-\d{6})-([0-9a-f]{16})(-\d+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex LegacyName = new(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex EditedName = new(@"^\d{8}-\d{6}(-\d+)?$", RegexOptions.CultureInvariant);

        /// <summary>Подмена файла (тесты подставляют сбой). Аргументы: замена, оригинал, резервная копия.</summary>
        internal static Action<string, string, string> ReplaceFile = (replacement, original, backup) =>
            File.Replace(replacement, original, backup, ignoreMetadataErrors: true);

        /// <summary>Как после перезапуска программы: сведения сеанса забыты, на диске всё осталось (для тестов).</summary>
        internal static void ForgetSession() { lock (Gate) { Backups.Clear(); Index.Clear(); } }

        internal static bool CanRestore(string path) { lock (Gate) return Backups.ContainsKey(System.IO.Path.GetFullPath(path)); }

        /// <summary>Есть ли у файла резервная копия оригинала (в этом сеансе или найденная в папке).</summary>
        internal static bool HasBackup(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            lock (Gate)
            {
                if (Backups.ContainsKey(path)) return true;
                return Index.TryGetValue(System.IO.Path.GetDirectoryName(path)!, out var names)
                    && names.TryGetValue(System.IO.Path.GetFileName(path), out int count) && count > 0;
            }
        }

        internal static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }

        internal static string Write(string path, bool copy, string suffix, Action<Stream> write, FileOperationContext? operation, FileVersion sourceVersion) =>
            Write(path, copy, suffix, write, operation, sourceVersion, restoring: false, out _);

        private static string Write(string path, bool copy, string suffix, Action<Stream> write, FileOperationContext? operation, FileVersion sourceVersion,
            bool restoring, out string? backupPath)
        {
            path = System.IO.Path.GetFullPath(path);
            backupPath = null;
            if (copy)
            {
                string candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileNameWithoutExtension(path) + suffix + System.IO.Path.GetExtension(path));
                return SafeFileReplace.WriteCopyWithOperation(candidate, stream =>
                {
                    write(stream);
                    if (FileVersion.Read(path) != sourceVersion) throw new IOException("Source changed during editing. No copy was saved.");
                }, operation);
            }
            operation?.Checkpoint(FileOperationStage.Writing);
            var (temp, output) = SafeFileReplace.ReserveTemporaryFile(path);
            string? backup = null, reservedBackup = null; bool committed = false;
            try
            {
                using (output) { write(output); operation?.Checkpoint(FileOperationStage.Writing); output.Flush(true); }
                string savedHash = Hash(temp);
                if (FileVersion.Read(path) != sourceVersion) throw new IOException("Source changed during editing. Original was not replaced.");
                // Atomically reserve our backup name before File.Replace is allowed to overwrite it.
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string stem = restoring ? path + EditedMarker + stamp : path + OriginalMarker + stamp + "-" + savedHash[..16].ToLowerInvariant();
                var reserved = SafeFileReplace.ReserveTemporaryFile(path, i => stem + (i == 0 ? "" : "-" + i.ToString(CultureInfo.InvariantCulture)) + ".bak",
                    TempArtifactJournal.Kind.ImageBackup);
                backup = reservedBackup = reserved.Path; reserved.Stream.Dispose();
                void Commit()
                {
                    // No Move-overwrite fallback: it cannot retain the exact replaced file atomically.
                    try { ReplaceFile(temp, path, backup!); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        throw RecoverFailedReplace(path, ref backup, ex);
                    }
                    committed = true;
                    if (restoring) return;
                    // Publish undo before any subsequent UI refresh; hashing backup failure does not erase it.
                    bool hasEarlier;
                    lock (Gate)
                    {
                        hasEarlier = Backups.TryGetValue(path, out var earlier);
                        Backups[path] = hasEarlier ? earlier! with { SavedHash = savedHash } : new Backup(backup!, savedHash, "");
                        IndexAdd(path, +1);
                    }
                    if (!hasEarlier)
                    {
                        try { string originalHash = Hash(backup!); lock (Gate) Backups[path] = new Backup(backup!, savedHash, originalHash); }
                        catch (Exception ex) { AppLog.Warn("ImageSaveWriter.BackupHash", ex); }
                    }
                }
                if (operation == null) Commit(); else operation.Commit(Commit);
                backupPath = backup;
                return path;
            }
            finally
            {
                if (!committed)
                {
                    try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("ImageSaveWriter.Temp", ex); }
                    // Пустая заготовка имени — наша; копию с данными НЕ удаляем никогда.
                    if (backup != null)
                        try { if (File.Exists(backup) && new FileInfo(backup).Length == 0) File.Delete(backup); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("ImageSaveWriter.Backup", ex); }
                }
                // Не теряем recovery-запись, если оригинал всё ещё отсутствует или недоступен.
                // Непустая .bak при двойном отказе должна пережить перезапуск вместе с журналом.
                TempArtifactJournal.Release(temp);
                if (reservedBackup != null) TempArtifactJournal.ReleaseImageBackup(reservedBackup, path);
            }
        }

        /// <summary>
        /// ReplaceFile может сорваться, уже переименовав оригинал в резервную копию
        /// (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: антивирус, OneDrive, сетевой диск).
        /// Тогда по исходному пути файла нет — возвращаем оригинал на место.
        /// </summary>
        private static IOException RecoverFailedReplace(string path, ref string? backup, Exception error)
        {
            string name = System.IO.Path.GetFileName(path);
            AppLog.Warn("ImageSaveWriter.Replace", error, AppLog.Describe(path));
            try
            {
                if (!File.Exists(path) && backup != null && File.Exists(backup) && new FileInfo(backup).Length > 0)
                {
                    try
                    {
                        File.Move(backup, path);
                        backup = null;
                        return new IOException(Loc.T(
                            $"Could not replace the file ({error.Message}). The original was put back; nothing was changed.",
                            $"Не удалось заменить файл ({error.Message}). Оригинал возвращён на место, ничего не изменено.",
                            $"No se pudo reemplazar el archivo ({error.Message}). El original se restauró; no cambió nada."), error);
                    }
                    catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Warn("ImageSaveWriter.PutBack", moveError, AppLog.Describe(path));
                        string kept = System.IO.Path.GetFileName(backup) ?? "";
                        return new IOException(Loc.T(
                            $"Could not replace the file ({error.Message}), and the original could not be put back automatically. It is safe as \"{kept}\" in the same folder — rename it back to \"{name}\".",
                            $"Не удалось заменить файл ({error.Message}), и оригинал не удалось вернуть автоматически. Он цел: «{kept}» в той же папке — переименуйте его обратно в «{name}».",
                            $"No se pudo reemplazar el archivo ({error.Message}) ni restaurar el original. Está a salvo como «{kept}»; renómbrelo a «{name}»."), error);
                    }
                }
            }
            catch (Exception probe) when (probe is IOException or UnauthorizedAccessException) { AppLog.Warn("ImageSaveWriter.Probe", probe); }
            return new IOException(Loc.T(
                $"Could not replace the file ({error.Message}). The original was not changed.",
                $"Не удалось заменить файл ({error.Message}). Оригинал не изменён.",
                $"No se pudo reemplazar el archivo ({error.Message}). El original no cambió."), error);
        }

        // ---------------- Поиск резервных копий ----------------

        /// <summary>Разбирает имя «файл.ext.pmv-original-…bak» / «файл.ext.pmv-edited-…bak».</summary>
        internal static DiskBackup? ParseBackupName(string backupPath)
        {
            string fileName = System.IO.Path.GetFileName(backupPath);
            if (!fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var (marker, edited) in new[] { (OriginalMarker, false), (EditedMarker, true) })
            {
                int at = fileName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at <= 0) continue;
                string owner = fileName[..at];
                string middle = fileName[(at + marker.Length)..^4];
                DateTime Created()
                {
                    try { return File.GetCreationTimeUtc(backupPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
                }
                if (edited)
                    return EditedName.IsMatch(middle) ? new DiskBackup(backupPath, owner, null, true, Created()) : null;
                var match = NewName.Match(middle);
                if (match.Success)
                {
                    var time = DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t.ToUniversalTime() : Created();
                    return new DiskBackup(backupPath, owner, match.Groups[2].Value.ToUpperInvariant(), false, time);
                }
                return LegacyName.IsMatch(middle) ? new DiskBackup(backupPath, owner, null, false, Created()) : null;
            }
            return null;
        }

        /// <summary>Все резервные копии в папке (наши имена; чужие *.bak не трогаются).</summary>
        internal static List<DiskBackup> FindFolderBackups(string folder)
        {
            var list = new List<DiskBackup>();
            foreach (var pattern in new[] { "*" + OriginalMarker + "*.bak", "*" + EditedMarker + "*.bak" })
                foreach (var file in Directory.EnumerateFiles(folder, pattern))
                    if (ParseBackupName(file) is { } backup && list.All(b => !string.Equals(b.Path, backup.Path, StringComparison.OrdinalIgnoreCase)))
                        list.Add(backup);
            return list;
        }

        /// <summary>Резервные копии одного файла.</summary>
        internal static List<DiskBackup> FindBackups(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            string name = System.IO.Path.GetFileName(path);
            return FindFolderBackups(System.IO.Path.GetDirectoryName(path)!)
                .Where(b => string.Equals(b.Owner, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Запоминает, у каких файлов папки есть копии оригинала. Вызывается в фоне при открытии папки.</summary>
        internal static void IndexFolder(string folder)
        {
            folder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
            TempArtifactJournal.SweepFolder(folder); // остатки аварийного завершения (старше часа)
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var backup in FindFolderBackups(folder))
                    if (!backup.Edited) names[backup.Owner] = names.GetValueOrDefault(backup.Owner) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("ImageSaveWriter.IndexFolder", ex); }
            lock (Gate)
            {
                if (Index.Count > 32 && !Index.ContainsKey(folder)) Index.Clear();
                Index[folder] = names;
            }
        }

        private static void IndexAdd(string ownerPath, int delta)
        {
            // Вызывается под Gate.
            string dir = System.IO.Path.GetDirectoryName(ownerPath)!, name = System.IO.Path.GetFileName(ownerPath);
            if (!Index.TryGetValue(dir, out var names)) return;
            int count = names.GetValueOrDefault(name) + delta;
            if (count > 0) names[name] = count; else names.Remove(name);
        }

        /// <summary>Файл или его резервная копия переименованы (массовое переименование).</summary>
        internal static void NotifyRenamed(string oldPath, string newPath)
        {
            oldPath = System.IO.Path.GetFullPath(oldPath); newPath = System.IO.Path.GetFullPath(newPath);
            lock (Gate)
            {
                if (Backups.Remove(oldPath, out var record)) Backups[newPath] = record;
                foreach (var key in Backups.Keys.ToList())
                    if (string.Equals(Backups[key].Path, oldPath, StringComparison.OrdinalIgnoreCase))
                        Backups[key] = Backups[key] with { Path = newPath };
                if (ParseBackupName(oldPath) is { Edited: false } before && ParseBackupName(newPath) is { } after)
                {
                    string dir = System.IO.Path.GetDirectoryName(oldPath)!;
                    IndexAdd(System.IO.Path.Combine(dir, before.Owner), -1);
                    IndexAdd(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(newPath)!, after.Owner), +1);
                }
            }
        }

        /// <summary>Резервные копии убраны (в корзину): кнопки для них больше не показываются.</summary>
        internal static void ForgetBackups(IEnumerable<string> removed)
        {
            lock (Gate)
                foreach (var raw in removed)
                {
                    string backupPath = System.IO.Path.GetFullPath(raw);
                    foreach (var key in Backups.Where(p => string.Equals(p.Value.Path, backupPath, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
                        Backups.Remove(key);
                    if (ParseBackupName(backupPath) is { Edited: false } parsed)
                        IndexAdd(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(backupPath)!, parsed.Owner), -1);
                }
        }

        // ---------------- Восстановление ----------------

        /// <summary>
        /// Возвращает самый первый оригинал. Цепочка: текущий файл → копия, сохранённая
        /// вместе с ним (хеш в имени), → её содержимое → более ранняя копия… Если файл
        /// изменили извне (хеш не совпадает ни с одной копией), автоматически ничего не
        /// восстанавливается. Возвращает копии, которые после этого не нужны.
        /// </summary>
        internal static RestoreResult Restore(string path, FileOperationContext? operation = null)
        {
            path = System.IO.Path.GetFullPath(path);
            operation?.Checkpoint(FileOperationStage.Preparing);
            FileVersion version = FileVersion.Read(path);
            string current = Hash(path);
            Backup? session; lock (Gate) Backups.TryGetValue(path, out session);
            var disk = FindBackups(path).Where(b => !b.Edited).ToList();

            var chain = new List<string>(); string target = current;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                operation?.Checkpoint(FileOperationStage.Preparing);
                var next = disk.Where(b => b.SavedHashPrefix != null && !visited.Contains(b.Path) && target.StartsWith(b.SavedHashPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(b => b.Created).FirstOrDefault();
                if (next == null) break;
                visited.Add(next.Path); chain.Add(next.Path); target = Hash(next.Path);
            }
            // Копия этого сеанса без хеша в имени (на случай старых записей).
            if (chain.Count == 0 && session != null && session.SavedHash == current && File.Exists(session.Path))
            { chain.Add(session.Path); target = Hash(session.Path); }

            if (chain.Count == 0)
            {
                if (session != null || disk.Any(b => b.SavedHashPrefix != null))
                    throw new IOException(Loc.T(
                        "The edited file has changed since it was saved. Automatic restore is refused; restore manually from its .pmv-original backup — no file was overwritten.",
                        "Файл изменился после сохранения правки. Автоматическое восстановление отклонено; верните оригинал вручную из .pmv-original-….bak — ничего не перезаписано.",
                        "El archivo cambió después de guardarse. Restauración automática rechazada; restaure manualmente desde .pmv-original; no se sobrescribió nada."));
                if (disk.Count > 0)
                    throw new IOException(Loc.T(
                        $"The backup \"{System.IO.Path.GetFileName(disk[0].Path)}\" was made by an earlier version and can't be checked automatically. Restore it manually: rename it back to \"{System.IO.Path.GetFileName(path)}\".",
                        $"Резервная копия «{System.IO.Path.GetFileName(disk[0].Path)}» сделана прежней версией программы, её нельзя проверить автоматически. Верните вручную: переименуйте её обратно в «{System.IO.Path.GetFileName(path)}».",
                        $"La copia «{System.IO.Path.GetFileName(disk[0].Path)}» es de una versión anterior y no se puede comprobar. Renómbrela a «{System.IO.Path.GetFileName(path)}»."));
                throw new InvalidOperationException(Loc.T("No backup of the original was found next to the file.", "Рядом с файлом нет резервной копии оригинала.", "No hay copia del original junto al archivo."));
            }
            string source = chain[^1], originalHash = target;
            if (session != null && string.Equals(session.Path, source, StringComparison.OrdinalIgnoreCase) && session.OriginalHash.Length > 0 && session.OriginalHash != originalHash)
                throw new IOException("Original backup changed externally; automatic restore refused.");

            Write(path, false, "", output => { using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); stream.CopyTo(output); },
                operation, version, restoring: true, out string? edited);
            lock (Gate) Backups.Remove(path);
            if (!string.Equals(Hash(path), originalHash, StringComparison.Ordinal))
            {
                // Не должно случиться; копии не трогаем — ничего не будет предложено к удалению.
                AppLog.Warn("ImageSaveWriter.RestoreVerify", null, AppLog.Describe(path));
                return new RestoreResult(Array.Empty<string>());
            }
            var redundant = new List<string>(chain);
            if (edited != null) redundant.Add(edited);
            return new RestoreResult(redundant);
        }
    }
}
