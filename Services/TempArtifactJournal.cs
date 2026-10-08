using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Уборка временных файлов после аварии.
    ///
    /// При записи рядом с фото появляются «имя.jpg.pmv-ГУИД.tmp» (новое содержимое) и
    /// заготовка «имя.jpg.pmv-original-….bak». При обычных ошибках они удаляются сразу,
    /// но если программу закрыли через диспетчер задач или пропало питание, они оставались
    /// навсегда. Теперь:
    ///
    /// 1. Перед созданием такого файла в %LOCALAPPDATA%\PhotoMusicViewer\pending пишется
    ///    крошечная запись (путь зашифрован DPAPI, как ключи перевода), после работы она
    ///    удаляется. Записи пустой папки pending = ничего не осталось.
    /// 2. При следующем запуске записи процессов, которые уже не работают, разбираются:
    ///    временный файл удаляется, если оригинал на месте; пустая заготовка .bak
    ///    удаляется; если авария случилась посреди замены и оригинал остался только в .bak,
    ///    он возвращается на место. Удаляются только файлы с нашими именами рядом с целью.
    /// 3. При открытии папки (для файлов прежних версий, без записей) удаляются наши
    ///    *.pmv-ГУИД.tmp и пустые заготовки .bak старше часа, если оригинал на месте.
    /// 4. При запуске убираются %TEMP%\pmv-jpeg-*.tmp старше часа (буфер jpegtran).
    /// </summary>
    internal static class TempArtifactJournal
    {
        internal enum Kind { Temp, ImageBackup, ReplaceBackup, SourceRemoval }

        /// <summary>Шифрование путей в записях. Приложение подставляет DPAPI; null — запись не делается.</summary>
        internal static Func<string, string?> Protect = s => s;
        internal static Func<string, string?> Unprotect = s => s;
        internal static string? DirectoryOverride;
        internal static string? TempDirectoryOverride;
        internal static readonly TimeSpan StaleAge = TimeSpan.FromHours(1);

        private static readonly ConcurrentDictionary<string, string> Entries = new(StringComparer.OrdinalIgnoreCase); // файл → запись
        private static readonly Lazy<string> ProcessTag = new(() =>
        {
            int pid = Environment.ProcessId;
            long ticks = 0;
            try { using var self = Process.GetCurrentProcess(); ticks = self.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
            return pid + "-" + ticks;
        });
        private static readonly Regex TempName = new(@"^(?<target>.+)\.pmv-[0-9a-f]{32}\.(?<ext>tmp|bak)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex EntryName = new(@"^(?<pid>\d+)-(?<ticks>\d+)-[0-9a-f]{32}\.pending$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string JournalDirectory => DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoMusicViewer", "pending");

        // ---------------- Запись ----------------

        /// <summary>Запоминает файл до его создания. Сбой записи не мешает сохранению фото.</summary>
        internal static string CurrentProcessTag => ProcessTag.Value;

        internal static void Track(string artifact, string target, Kind kind) =>
            Write(ProcessTag.Value, kind, target, artifact);

        internal static string? Write(string processTag, Kind kind, string target, string artifact)
        {
            try
            {
                using var targetGuard = DirectoryPathGuard.Acquire(Path.GetDirectoryName(Path.GetFullPath(target))!);
                using var artifactGuard = DirectoryPathGuard.Acquire(Path.GetDirectoryName(Path.GetFullPath(artifact))!);
                string? body = Protect(string.Join("\n", kind.ToString(), targetGuard.FilePath(target), artifactGuard.FilePath(artifact)));
                if (body == null) return null;
                using var journalGuard = DirectoryPathGuard.AcquireOrCreate(JournalDirectory);
                journalGuard.Validate();
                string entry = Path.Combine(JournalDirectory, processTag + "-" + Guid.NewGuid().ToString("N") + ".pending");
                using (var stream = new FileStream(entry, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(body));
                    stream.Flush(flushToDisk: true);
                }
                Entries[Path.GetFullPath(artifact)] = entry;
                return entry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("TempArtifactJournal.Track", ex);
                return null;
            }
        }

        /// <summary>
        /// Работа с файлом закончена. force = false: запись снимается, только если файла уже
        /// нет (не удалось удалить — пусть уберёт следующий запуск).
        /// </summary>
        internal static void Release(string? artifact, bool force = false)
        {
            if (artifact == null) return;
            string full = Path.GetFullPath(artifact);
            if (!force)
            {
                // File.Exists returns false on access errors as well as missing files.
                // Unknown state must preserve the recovery entry, not silently discard it.
                try { File.GetAttributes(full); return; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { AppLog.Warn("TempArtifactJournal.ReleaseUnknown", ex); return; }
            }
            if (!Entries.TryRemove(full, out var entry)) return;
            try { TryDelete(entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("TempArtifactJournal.Release", ex); }
        }

        /// <summary>Не снимает recovery-запись, пока нет доступного исходника.
        /// File.Exists скрывает отказ доступа, поэтому состояние проверяется открытием.</summary>
        internal static void ReleaseImageBackup(string artifact, string target)
        {
            try
            {
                var info = new FileInfo(artifact);
                long length = info.Length;
                if (length == 0) return; // заготовка не убралась: повторить уборку позже
                using var source = new FileStream(target, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                Release(artifact, force: true); // обычный backup при доступном оригинале
            }
            catch (FileNotFoundException) { Release(artifact); }
            catch (DirectoryNotFoundException) { Release(artifact); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("TempArtifactJournal.KeepRecovery", ex);
            }
        }

        /// <summary>Удаление исходника запрещено, если защищённый журнал недоступен.</summary>
        internal static void TrackRequired(string artifact, string target, Kind kind)
        {
            if (Write(ProcessTag.Value, kind, target, artifact) == null)
                throw new IOException(Loc.T(
                    "The recovery journal is unavailable. The original was kept.",
                    "Журнал восстановления недоступен. Исходник оставлен на месте.",
                    "El diario de recuperación no está disponible. Se conservó el original."));
        }

        // ---------------- Уборка при запуске ----------------

        internal sealed record CleanupResult(int Deleted, int Restored, int Kept);

        /// <summary>Разбирает записи завершившихся процессов. Вызывается в фоне при запуске.</summary>
        internal static CleanupResult CleanupAbandoned()
        {
            int deleted = 0, restored = 0, kept = 0;
            string dir = JournalDirectory;
            if (!Directory.Exists(dir)) return new CleanupResult(0, 0, 0);
            using var journalGuard = DirectoryPathGuard.Acquire(dir);
            var work = new List<(string Entry, Kind Kind, string Target, string Artifact)>();
            foreach (var entry in Directory.EnumerateFiles(dir, "*.pending"))
            {
                var match = EntryName.Match(Path.GetFileName(entry));
                if (!match.Success) continue;
                if (IsAlive(int.Parse(match.Groups["pid"].Value), long.Parse(match.Groups["ticks"].Value))) continue;
                string? body = null;
                try
                {
                    journalGuard.Validate();
                    DirectoryPathGuard.RequireRegularFile(entry);
                    body = Unprotect(File.ReadAllText(entry, Encoding.UTF8));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("TempArtifactJournal.Read", ex); continue; }
                var lines = body?.Split('\n');
                if (lines is not { Length: 3 } || !Enum.TryParse(lines[0], out Kind kind)) { TryDelete(entry); continue; }
                work.Add((entry, kind, lines[1], lines[2]));
            }
            // Сначала резервные копии (вернуть оригинал), затем временные файлы.
            foreach (var item in work.OrderBy(w => w.Kind == Kind.Temp ? 1 : 0))
            {
                try
                {
                    switch (Recover(item.Kind, item.Target, item.Artifact))
                    {
                        case Outcome.Deleted: deleted++; break;
                        case Outcome.Restored: restored++; break;
                        case Outcome.Kept: kept++; break;
                    }
                    TryDelete(item.Entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Занят (антивирус, OneDrive) — попробуем при следующем запуске.
                    AppLog.Warn("TempArtifactJournal.Cleanup", ex, AppLog.Describe(item.Artifact));
                }
            }
            deleted += CleanupSpool();
            if (deleted + restored + kept > 0)
                AppLog.Warn("TempArtifactJournal.Cleanup", null, $"after an abnormal exit: deleted {deleted}, restored {restored}, kept {kept}");
            return new CleanupResult(deleted, restored, kept);
        }

        private enum Outcome { Nothing, Deleted, Restored, Kept }

        /// <summary>Только наши имена рядом с целью; ссылки и чужие файлы не трогаются.</summary>
        internal static bool IsOwnArtifact(Kind kind, string target, string artifact)
        {
            target = Path.GetFullPath(target); artifact = Path.GetFullPath(artifact);
            if (!string.Equals(Path.GetDirectoryName(target), Path.GetDirectoryName(artifact), StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(artifact), owner = Path.GetFileName(target);
            if (string.Equals(name, owner, StringComparison.OrdinalIgnoreCase)) return false;
            if (kind == Kind.ImageBackup)
                return ImageSaveWriter.ParseBackupName(artifact) is { } backup && string.Equals(backup.Owner, owner, StringComparison.OrdinalIgnoreCase);
            var match = TempName.Match(name);
            return match.Success && string.Equals(match.Groups["target"].Value, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(match.Groups["ext"].Value, kind == Kind.Temp ? "tmp" : "bak", StringComparison.OrdinalIgnoreCase);
        }

        private static Outcome Recover(Kind kind, string target, string artifact)
        {
            if (!IsOwnArtifact(kind, target, artifact)) return Outcome.Nothing;
            using var directoryGuard = DirectoryPathGuard.Acquire(Path.GetDirectoryName(Path.GetFullPath(artifact))!);
            directoryGuard.Validate();
            target = directoryGuard.FilePath(target);
            artifact = directoryGuard.FilePath(artifact);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(artifact); }
            catch (FileNotFoundException) { return Outcome.Nothing; }
            catch (DirectoryNotFoundException) { return Outcome.Nothing; }
            // Other errors propagate: the caller keeps the pending entry for retry.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery artifact is linked; keep the entry and refuse cleanup.");
            long length = new FileInfo(artifact).Length;
            bool targetExists;
            try
            {
                var targetAttributes = File.GetAttributes(target);
                if ((targetAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recovery target is linked; keep the backup and entry.");
                if ((targetAttributes & FileAttributes.Directory) != 0)
                    throw new IOException("Recovery target is occupied by a directory; keep the backup and retry later.");
                targetExists = true;
            }
            catch (FileNotFoundException) { targetExists = false; }
            catch (DirectoryNotFoundException) { targetExists = false; }
            directoryGuard.Validate();
            DirectoryPathGuard.RequireRegularFile(artifact);
            DirectoryPathGuard.RequireRegularFile(target, allowMissing: true);
            switch (kind)
            {
                case Kind.ImageBackup:
                    if (length == 0) { File.Delete(artifact); return Outcome.Deleted; }
                    // Авария посреди замены: оригинал уже стал копией, а новый файл не встал.
                    if (!targetExists) { File.Move(artifact, target); return Outcome.Restored; }
                    return Outcome.Kept; // обычная резервная копия — для «Вернуть оригинал»
                case Kind.SourceRemoval:
                    // Это исходник, а не расходный temp. При конфликте никогда не удаляем.
                    if (!targetExists) { File.Move(artifact, target); return Outcome.Restored; }
                    AppLog.Warn("TempArtifactJournal.KeptSource", null, AppLog.Describe(artifact));
                    return Outcome.Kept;
                case Kind.ReplaceBackup:
                    if (!targetExists && length > 0) { File.Move(artifact, target); return Outcome.Restored; }
                    File.Delete(artifact); return Outcome.Deleted;
                default:
                    // Временный файл — новое, возможно недописанное содержимое. Без оригинала не удаляем.
                    if (!targetExists) { AppLog.Warn("TempArtifactJournal.OrphanTemp", null, AppLog.Describe(artifact)); return Outcome.Kept; }
                    File.Delete(artifact); return Outcome.Deleted;
            }
        }

        private static bool IsAlive(int pid, long startTicks)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return startTicks == 0 || process.StartTime.ToUniversalTime().Ticks == startTicks;
            }
            catch (ArgumentException) { return false; }                     // процесса нет
            catch (InvalidOperationException) { return false; }              // уже завершился
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException) { return true; } // не знаем — не трогаем
        }

        /// <summary>%TEMP%\pmv-jpeg-*.tmp — буфер jpegtran; обычно удаляется системой при закрытии.</summary>
        private static int CleanupSpool()
        {
            int deleted = 0;
            try
            {
                string temp = TempDirectoryOverride ?? Path.GetTempPath();
                using var tempGuard = DirectoryPathGuard.Acquire(temp);
                foreach (var file in Directory.EnumerateFiles(temp, "pmv-jpeg-*.tmp"))
                    if (Regex.IsMatch(Path.GetFileName(file), @"^pmv-jpeg-[0-9a-f]{32}\.tmp$", RegexOptions.IgnoreCase) && IsStale(file) && TryDelete(file)) deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("TempArtifactJournal.Spool", ex); }
            return deleted;
        }

        // ---------------- Уборка при открытии папки ----------------

        /// <summary>
        /// Файлы прежних версий (или если запись не удалось сделать): наши *.pmv-ГУИД.tmp и
        /// пустые заготовки .pmv-original/.pmv-edited старше часа, если оригинал на месте.
        /// Файлы, с которыми этот процесс сейчас работает, пропускаются.
        /// </summary>
        internal static int SweepFolder(string folder)
        {
            int deleted = 0;
            try
            {
                using var folderGuard = DirectoryPathGuard.Acquire(folder);
                foreach (var file in Directory.EnumerateFiles(folder, "*.pmv-*"))
                {
                    string name = Path.GetFileName(file);
                    string full = Path.GetFullPath(file);
                    if (Entries.ContainsKey(full) || !IsStale(full)) continue;
                    string? owner = null;
                    var match = TempName.Match(name);
                    if (match.Success && match.Groups["ext"].Value.Equals("tmp", StringComparison.OrdinalIgnoreCase)) owner = match.Groups["target"].Value;
                    else if (ImageSaveWriter.ParseBackupName(full) is { } backup && new FileInfo(full).Length == 0) owner = backup.Owner;
                    if (owner == null || !File.Exists(Path.Combine(folder, owner))) continue;
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
                    folderGuard.Validate();
                    DirectoryPathGuard.RequireRegularFile(Path.Combine(folder, owner));
                    if (TryDelete(full)) deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("TempArtifactJournal.SweepFolder", ex); }
            if (deleted > 0) AppLog.Info("TempArtifactJournal.SweepFolder", null, $"removed {deleted} leftover temporary file(s)");
            return deleted;
        }

        private static bool IsStale(string file)
        {
            try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleAge; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private static bool TryDelete(string file)
        {
            try
            {
                using var guard = DirectoryPathGuard.Acquire(Path.GetDirectoryName(Path.GetFullPath(file))!);
                DirectoryPathGuard.RequireRegularFile(file, allowMissing: true);
                guard.Validate();
                File.Delete(guard.FilePath(file)); return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("TempArtifactJournal.Delete", ex); return false; }
        }
    }
}
