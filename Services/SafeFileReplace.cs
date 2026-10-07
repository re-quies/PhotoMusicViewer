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
        /// <summary>
        /// Пишет новое содержимое во временный файл рядом с <paramref name="targetPath"/>
        /// и ставит его на место оригинала.
        ///
        /// Гарантия: после вызова по пути <paramref name="targetPath"/> лежит либо новая
        /// версия целиком, либо нетронутый оригинал. Временный файл при любой ошибке
        /// удаляется.
        /// </summary>
        /// <param name="targetPath">Файл, который нужно заменить.</param>
        /// <param name="write">Запись нового содержимого в открытый поток.</param>
        public static void WriteThenReplace(string targetPath, Action<Stream> write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));

            var tempPath = CreateTempPath(targetPath);

            try
            {
                using var stream = new FileStream(tempPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);

                write(stream);

                // Без этого содержимое может остаться в кэше ОС: при внезапном
                // выключении сразу после замены на диске окажется пустой файл
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                // Оригинал ещё не тронут - убираем за собой недописанный файл
                TryDelete(tempPath);
                throw;
            }

            Replace(tempPath, targetPath);
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

            var backupPath = targetPath + ".bak";
            TryDelete(backupPath); // хвост от прошлой прерванной попытки

            try
            {
                // ReplaceFile: подмена и создание резервной копии за одну операцию.
                // Заодно сохраняются атрибуты, права доступа и дата создания оригинала.
                File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
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
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }
            finally
            {
                // Резервную копию убираем только тогда, когда на месте лежит целый файл
                if (File.Exists(targetPath)) TryDelete(backupPath);
            }
        }

        /// <summary>
        /// Свободный путь для временного файла в той же папке: замена работает
        /// только в пределах одного тома.
        /// </summary>
        private static string CreateTempPath(string targetPath)
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("Path must include a directory.", nameof(targetPath));

            var name = Path.GetFileName(targetPath);
            for (int i = 0; i < 100; i++)
            {
                var candidate = Path.Combine(directory, i == 0 ? name + ".tmp" : name + ".tmp" + i);
                if (!File.Exists(candidate)) return candidate;
            }

            throw new IOException($"Cannot create a temporary file next to {targetPath}.");
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
