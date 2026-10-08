using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Безопасная замена файла на диске.
    ///
    /// Прежний порядок действий (File.Delete + File.Move) оставлял окно, в котором
    /// оригинал уже удалён, а новая версия ещё не встала на его место: сбой питания,
    /// исключение или перехват файла антивирусом в этот момент означали потерю снимка.
    /// Здесь подмена выполняется одной операцией файловой системы, а содержимое перед
    /// этим принудительно сбрасывается на диск.
    /// </summary>
    internal static class SafeFileReplace
    {
        // Тестовая точка отказа; production использует обычный File.Replace.
        internal static Action<string, string, string> ReplaceFile = (replacement, target, backup) =>
            File.Replace(replacement, target, backup, ignoreMetadataErrors: true);
        /// <summary>
        /// Пишет новое содержимое во временный файл рядом с <paramref name="targetPath"/>
        /// и ставит его на место оригинала.
        ///
        /// Гарантия: после вызова по пути <paramref name="targetPath"/> лежит либо новая
        /// версия целиком, либо нетронутый оригинал. При ошибке записи пытаемся
        /// удалить только созданный нами временный файл; отказ очистки журналируется.
        /// </summary>
        /// <param name="targetPath">Файл, который нужно заменить.</param>
        /// <param name="write">Запись нового содержимого в открытый поток.</param>
        public static void WriteThenReplace(string targetPath, Action<Stream> write) => WriteThenReplaceWithOperation(targetPath, write, null);

        public static void WriteThenReplaceWithOperation(string targetPath, Action<Stream> write, FileOperationContext? operation)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));

            // Открытие отделено от записи: отказ CreateNew не даёт права удалять файл.
            operation?.Checkpoint(FileOperationStage.Writing);
            var (tempPath, stream) = ReserveTemporaryFile(targetPath);
            try
            {
                using (stream)
                {
                    write(stream);
                    operation?.Checkpoint(FileOperationStage.Writing);
                    stream.Flush(flushToDisk: true);
                    operation?.Checkpoint(FileOperationStage.Writing);
                }
            }
            catch
            {
                TryDelete(tempPath); // Только успешно созданный нами файл.
                TempArtifactJournal.Release(tempPath);
                throw;
            }

            try
            {
                if (operation == null) Replace(tempPath, targetPath);
                else operation.Commit(() => Replace(tempPath, targetPath));
            }
            catch { TryDelete(tempPath); throw; }
            finally { TempArtifactJournal.Release(tempPath); }
        }

        /// <summary>
        /// Ставит <paramref name="tempPath"/> на место <paramref name="targetPath"/>
        /// без промежуточного состояния, в котором файла нет на диске.
        /// </summary>
        public static void Replace(string tempPath, string targetPath)
        {
            // Заменять нечего - просто переносим новый файл на его место
            if (!File.Exists(targetPath))
            {
                try
                {
                    File.Move(tempPath, targetPath);
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }

                return;
            }

            // Не трогаем пользовательский targetPath + ".bak".
            var backupPath = SettingsArtifacts.NewArtifactPath(targetPath, "bak");
            bool replacementSucceeded = false;
            // Авария посреди ReplaceFile: следующий запуск вернёт оригинал из этой копии.
            TempArtifactJournal.Track(backupPath, targetPath, TempArtifactJournal.Kind.ReplaceBackup);

            try
            {
                // ReplaceFile: подмена и создание резервной копии за одну операцию.
                // Заодно сохраняются атрибуты, права доступа и дата создания оригинала.
                ReplaceFile(tempPath, targetPath, backupPath);
                replacementSucceeded = true;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
            {
                // ReplaceFile поддерживается не всеми файловыми системами и сетевыми
                // дисками. Если оригинал успел исчезнуть - возвращаем его из копии.
                if (!File.Exists(targetPath) && File.Exists(backupPath))
                    File.Move(backupPath, targetPath);

                try
                {
                    // MoveFileEx(MOVEFILE_REPLACE_EXISTING) - тоже одна операция замены
                    File.Move(tempPath, targetPath, overwrite: true);
                    replacementSucceeded = true;
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }
            finally
            {
                // При неудаче сохраняем резервную копию для восстановления.
                if (replacementSucceeded) TryDelete(backupPath);
                // Существующая копия остаётся в журнале, в том числе при двойном
                // отказе Replace/Move. После выхода процесса уборка повторит recovery.
                TempArtifactJournal.Release(backupPath);
            }
        }

        /// <summary>
        /// Свободный путь для временного файла в той же папке: замена работает
        /// только в пределах одного тома.
        /// </summary>
        internal static (string Path, FileStream Stream) ReserveTemporaryFile(string targetPath, Func<int, string>? makeCandidate = null,
            TempArtifactJournal.Kind kind = TempArtifactJournal.Kind.Temp)
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("Path must include a directory.", nameof(targetPath));
            var name = Path.GetFileName(targetPath);
            for (int i = 0; i < 100; i++)
            {
                var candidate = makeCandidate?.Invoke(i) ?? SettingsArtifacts.NewArtifactPath(targetPath, "tmp");
                // Запись в журнал — до создания файла: после аварии его уберёт следующий запуск.
                TempArtifactJournal.Track(candidate, targetPath, kind);
                try
                {
                    return (candidate, new FileStream(candidate, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None));
                }
                catch (IOException ex) when (IsNameCollision(ex)) { TempArtifactJournal.Release(candidate, force: true); }
                catch { TempArtifactJournal.Release(candidate, force: true); throw; }
            }
            throw new IOException("Cannot reserve a temporary file next to the target.");
        }

        private static bool IsNameCollision(IOException ex) => (ex.HResult & 0xFFFF) is 80 or 183 ||
            (!OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) == 17);

        /// <summary>Создаёт копию; ни одна ветка не заменяет существующий файл.</summary>
        public static string WriteCopyWithoutOverwrite(string path, Action<Stream> write) => WriteCopyWithOperation(path, write, null);

        public static string WriteCopyWithOperation(string path, Action<Stream> write, FileOperationContext? operation)
        {
            ArgumentNullException.ThrowIfNull(write);
            path = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int counter = 0; counter < 10000; counter++)
            {
                string candidate = counter == 0 ? path : Path.Combine(dir, $"{name} ({counter}){ext}");
                operation?.Checkpoint(FileOperationStage.Writing);
                FileStream stream;
                try
                {
                    stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException ex) when (IsNameCollision(ex)) { continue; }
                try
                {
                    using (stream)
                    {
                        write(stream);
                        operation?.Checkpoint(FileOperationStage.Writing);
                        if (operation == null) stream.Flush(flushToDisk: true);
                        else operation.Commit(() => stream.Flush(flushToDisk: true));
                    }
                    return candidate;
                }
                catch
                {
                    TryDelete(candidate);
                    throw;
                }
            }
            throw new IOException("Cannot reserve a unique copy filename.");
        }

        private static void TryDelete(string path)
        {
            // Оставшийся временный файл работе не мешает, поэтому ошибку глушим
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException ex) { AppLog.Debug("SafeFileReplace.TryDelete", ex); }
            catch (UnauthorizedAccessException ex) { AppLog.Debug("SafeFileReplace.TryDelete", ex); }
        }
    }
}
