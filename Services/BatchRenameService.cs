using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhotoMusicViewer.Services
{
    public sealed class BatchRenamePlanItem
    {
        public string OldPath { get; init; } = "";
        public string NewPath { get; init; } = "";
        /// <summary>Номер группы: снимок и его спутники переименовываются вместе.</summary>
        public int Group { get; init; }
        /// <summary>Scope and actual directory captured during planning, rechecked during execution.</summary>
        public string ScopeRoot { get; init; } = "";
        public bool RecursiveScope { get; init; }
        public string ActualDirectory { get; init; } = "";
        /// <summary>Спутник (RAW, .xmp, .aae, .MOV, резервная копия), а не сам снимок из просмотра.</summary>
        public bool Companion { get; init; }
    }

    public sealed class BatchRenameResult
    {
        public int RenamedCount { get; set; }
        public bool Cancelled { get; set; }
        public string? ErrorMessage { get; set; }
        public Dictionary<string, string> OldToNew { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Переименование в этой папке запрещено (корень диска, системная папка).</summary>
    public sealed class BatchRenameScopeException : InvalidOperationException
    {
        public BatchRenameScopeException(string message) : base(message) { }
    }

    /// <summary>
    /// Массовое переименование файлов в порядковые номера (001.jpg, 002.png, ...).
    ///
    /// Безопасность — главный приоритет:
    /// 1. Сначала строится полный план (старое имя -> новое имя) без единого касания диска.
    /// 2. Номера, уже занятые существующими числовыми именами, пропускаются.
    /// 3. Целевые имена уникальны по построению + проверяются на существование
    ///    дважды: при построении плана и непосредственно перед каждым переименованием.
    /// 4. File.Move никогда не перезаписывает существующий файл (бросает IOException) —
    ///    даже при гонке с другим процессом потеря данных невозможна.
    /// 5. При любой ошибке процесс останавливается: уже переименованные файлы остаются
    ///    под новыми именами, остальные — под старыми. Ничего не теряется и не затирается.
    /// 6. Снимок и его спутники с тем же именем (IMG_0001.JPG + .CR2 + .xmp + .AAE + .MOV,
    ///    IMG_0001.CR2.xmp, резервные копии IMG_0001.JPG.pmv-original-….bak) получают
    ///    один номер и переименовываются вместе: если один файл группы не удалось
    ///    переименовать, уже переименованные файлы этой группы возвращаются обратно.
    /// 7. Корень диска и системные папки не обрабатываются; при обходе подпапок
    ///    пропускаются скрытые, системные и ссылки на папки (junction, symlink).
    /// </summary>
    public static class BatchRenameService
    {
        /// <summary>Файлы с тем же именем, что и снимок: RAW, файлы настроек редакторов, Live Photo, голосовые заметки.</summary>
        internal static readonly HashSet<string> CompanionExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // RAW
            ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".rwl",
            ".pef", ".dng", ".srw", ".x3f", ".3fr", ".fff", ".iiq", ".mos", ".mrw", ".erf", ".kdc", ".dcr", ".raw",
            // файлы-спутники редакторов и телефонов
            ".xmp", ".aae", ".thm", ".pp3", ".dop", ".on1", ".arp",
            // Live Photo (iPhone), видео и звук к снимку
            ".mov", ".mp4", ".wav",
        };

        /// <summary>Спутники вида «IMG_0001.CR2.xmp» (darktable, RawTherapee, DxO).</summary>
        private static readonly HashSet<string> DoubleExtensionSidecars = new(StringComparer.OrdinalIgnoreCase)
        { ".xmp", ".pp3", ".dop", ".on1", ".arp" };

        private static readonly HashSet<string> OwnerExtensions = new(CompanionExtensions.Concat(new[]
        { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tif", ".tiff", ".gif", ".heic", ".heif", ".hif" }), StringComparer.OrdinalIgnoreCase);

        private static readonly string[] BackupMarkers = { ".pmv-original-", ".pmv-edited-" };

        // ---------------- Область действия ----------------

        /// <summary>Папки, где переименование запрещено (вместе со всем содержимым).</summary>
        internal static IReadOnlyList<string> DefaultProtectedFolders()
        {
            var list = new List<string>();
            foreach (var folder in new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
            })
            {
                try { string path = Environment.GetFolderPath(folder); if (!string.IsNullOrEmpty(path)) list.Add(path); }
                catch (PlatformNotSupportedException) { }
            }
            return list;
        }

        private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        private static bool IsSameOrInside(string path, string folder) =>
            string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Причина, по которой папку нельзя переименовывать, или null.
        /// Корень диска (C:\, D:\, \\сервер\общая папка) — нельзя ни с подпапками, ни без:
        /// там лежат папки всего диска. Системные папки и AppData — нельзя (кроме временной
        /// папки — туда распаковываются архивы). С подпапками — нельзя и из папки, внутри
        /// которой лежит системная (C:\Users, профиль пользователя).
        /// </summary>
        public static string? ScopeProblem(string folder, bool recursive, IReadOnlyList<string>? protectedFolders = null)
        {
            if (ScopeProblemCore(folder, recursive, protectedFolders) is { } lexicalProblem) return lexicalProblem;
            try
            {
                using var lease = DirectoryPathGuard.Acquire(folder);
                var actualProtected = new List<string>();
                foreach (string path in protectedFolders ?? DefaultProtectedFolders())
                    if (!string.IsNullOrWhiteSpace(path))
                        actualProtected.Add(Directory.Exists(path) ? DirectoryPathGuard.ResolveActual(path) : Normalize(path));
                string actualTemp = Directory.Exists(Path.GetTempPath()) ? DirectoryPathGuard.ResolveActual(Path.GetTempPath()) : Normalize(Path.GetTempPath());
                return ScopeProblemCore(lease.ActualDirectory, recursive, actualProtected, actualTemp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Loc.T("The folder or one of its parents is linked or cannot be verified. Renaming is refused.",
                    "Папка или один из её родителей является ссылкой либо не может быть проверен. Переименование запрещено.",
                    "La carpeta o uno de sus padres es un enlace o no se puede verificar. Se rechaza el cambio de nombre.");
            }
        }

        private static string? ScopeProblemCore(string folder, bool recursive, IReadOnlyList<string>? protectedFolders, string? tempDirectory = null)
        {
            string full = Normalize(folder);
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(Normalize(root), full, StringComparison.OrdinalIgnoreCase))
                return Loc.T("This is the root of a drive. Open the folder with the photos and rename there.",
                             "Это корень диска. Откройте папку с фотографиями и переименуйте там.",
                             "Es la raíz de una unidad. Abra la carpeta con las fotos y renombre allí.");
            string temp = tempDirectory ?? Normalize(Path.GetTempPath());
            // Временная папка лежит в AppData\Local, но в неё распаковывают архивы с фото.
            bool isTemp = string.Equals(full, temp, StringComparison.OrdinalIgnoreCase);
            bool inTemp = IsSameOrInside(full, temp) && (!isTemp || !recursive);
            foreach (var raw in protectedFolders ?? DefaultProtectedFolders())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string guarded = Normalize(raw);
                if (IsSameOrInside(full, guarded) && !(inTemp && IsSameOrInside(temp, guarded)))
                    return Loc.T("This is a system or program folder. Renaming files here can break Windows or apps.",
                                 "Это системная папка или папка программ. Переименование здесь может сломать Windows или программы.",
                                 "Es una carpeta del sistema o de programas. Renombrar aquí puede dañar Windows o aplicaciones.");
                if (recursive && IsSameOrInside(guarded, full))
                    return Loc.T("This folder contains system or program folders (for example, AppData). Turn off \"Include subfolders\" or open a narrower folder.",
                                 "Внутри этой папки есть системные папки или папки программ (например, AppData). Снимите «Включая подпапки» или откройте папку поуже.",
                                 "Esta carpeta contiene carpetas del sistema o de programas (p. ej., AppData). Desactive «Incluir subcarpetas» o abra una carpeta más concreta.");
            }
            return null;
        }

        /// <summary>Подпапку не обходим: ссылка, скрытая, системная или служебная.</summary>
        private static bool SkipDirectory(string path, bool isRoot)
        {
            var attributes = File.GetAttributes(path);
            // Не следуем ни junction, ни символьным ссылкам, включая корень.
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (isRoot) return false;
            string name = Path.GetFileName(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                || name.StartsWith('.') || name.StartsWith('$')
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- Группы: снимок + спутники ----------------

        /// <summary>
        /// Имя, по которому файлы объединяются в группу, и «хвост» после него.
        /// IMG_1.JPG → (IMG_1, .JPG); IMG_1.CR2.xmp → (IMG_1, .CR2.xmp);
        /// IMG_1.JPG.pmv-original-….bak → (IMG_1, .JPG.pmv-original-….bak).
        /// Owner — полное имя файла, к которому привязан спутник с двойным расширением.
        /// </summary>
        internal static (string Key, string Tail, string? Owner) SplitName(string fileName)
        {
            foreach (var marker in BackupMarkers)
            {
                int at = fileName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at > 0 && fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    string owner = fileName[..at];
                    string key = Path.GetFileNameWithoutExtension(owner);
                    return (key, fileName[key.Length..], owner);
                }
            }
            string ext = Path.GetExtension(fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (DoubleExtensionSidecars.Contains(ext) && OwnerExtensions.Contains(Path.GetExtension(stem)))
            {
                string key = Path.GetFileNameWithoutExtension(stem);
                return (key, fileName[key.Length..], stem);
            }
            return (stem, ext, null);
        }

        public static List<BatchRenamePlanItem> BuildPlan(
            string folder,
            bool recursive,
            long startNumber,
            int zeroPadding,
            IReadOnlyCollection<string> extensions, FileOperationContext? operation = null,
            Func<List<string>, List<string>>? orderInFolder = null)
        {
            if (ScopeProblem(folder, recursive) is { } problem) throw new BatchRenameScopeException(problem);

            // Все файлы в области действия (любых расширений) — нужны для учёта занятых номеров
            var allFiles = new List<string>();
            operation?.Checkpoint(FileOperationStage.Planning);
            CollectFiles(folder, recursive, allFiles, operation);
            bool IsPhoto(string f) => extensions.Contains(Path.GetExtension(f).ToLowerInvariant());

            // Кандидаты: только поддерживаемые фото/GIF с ещё не числовыми именами.
            // Порядок нумерации = порядок просмотра: папки идут по очереди (сначала
            // текущая, затем вложенные в естественном порядке), а файлы внутри папки —
            // в выбранной в просмотрщике сортировке (orderInFolder).
            var order = orderInFolder ?? DefaultOrder;
            var groups = new List<List<(string Path, string Tail, bool Companion)>>();
            foreach (var folderFiles in allFiles.GroupBy(f => Path.GetDirectoryName(f) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                operation?.Token.ThrowIfCancellationRequested();
                using var folderGuard = DirectoryPathGuard.Acquire(folderFiles.Key);
                if (ScopeProblem(folderFiles.Key, false) is { } folderProblem) throw new BatchRenameScopeException(folderProblem);
                var files = folderFiles.ToList();
                var names = new HashSet<string>(files.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
                var byKey = new Dictionary<string, List<(string Path, string Tail, bool Photo)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var (key, tail, owner) = SplitName(Path.GetFileName(f));
                    if (key.Length == 0 || IsNumericName(key)) continue;
                    bool photo = owner == null && IsPhoto(f);
                    bool member = photo
                        || owner == null && CompanionExtensions.Contains(Path.GetExtension(f))
                        || owner != null && names.Contains(owner);
                    if (!member) continue;
                    if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new();
                    // На файловых системах с учётом регистра «a.JPG» и «a.jpg» дали бы одно имя.
                    if (list.Any(m => string.Equals(m.Tail, tail, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add((f, tail, photo));
                }

                var eligible = byKey.Values.SelectMany(g => g).Where(m => m.Photo).Select(m => m.Path).ToList();
                if (eligible.Count == 0) continue;
                var ordered = order(new List<string>(eligible));
                // Сортировщик обязан вернуть те же файлы: иначе можно переименовать
                // лишнее или потерять номер. Ошибка вызывающего — не повод трогать диск.
                if (ordered.Count != eligible.Count ||
                    !new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase).SetEquals(eligible))
                    throw new InvalidOperationException("Batch rename order must contain exactly the planned files.");

                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var photoPath in ordered)
                {
                    var key = SplitName(Path.GetFileName(photoPath)).Key;
                    if (!emitted.Add(key)) continue; // второй снимок той же группы (IMG_1.JPG + IMG_1.HEIC)
                    var members = byKey[key];
                    var group = new List<(string, string, bool)>();
                    // Снимки — в порядке просмотра, затем спутники по имени.
                    foreach (var p in ordered.Where(o => members.Any(m => m.Photo && string.Equals(m.Path, o, StringComparison.OrdinalIgnoreCase))))
                        group.Add((p, members.First(m => string.Equals(m.Path, p, StringComparison.OrdinalIgnoreCase)).Tail, false));
                    foreach (var m in members.Where(m => !m.Photo).OrderBy(m => Path.GetFileName(m.Path), NaturalStringComparer.FileName))
                        group.Add((m.Path, m.Tail, true));
                    groups.Add(group);
                }
            }

            // Занятые номера — числовые имена любых существующих файлов в области действия
            var usedNumbers = new HashSet<long>();
            foreach (var f in allFiles)
            {
                operation?.Token.ThrowIfCancellationRequested();
                var n = SplitName(Path.GetFileName(f)).Key;
                if (IsNumericName(n) && long.TryParse(n, out var num))
                    usedNumbers.Add(num);
            }

            var plan = new List<BatchRenamePlanItem>();
            var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long current = startNumber;

            for (int g = 0; g < groups.Count; g++)
            {
                operation?.Token.ThrowIfCancellationRequested();
                var group = groups[g];
                var dir = Path.GetDirectoryName(group[0].Path)!;
                using var planGuard = DirectoryPathGuard.Acquire(dir);
                if (ScopeProblem(dir, false) is { } finalScopeProblem) throw new BatchRenameScopeException(finalScopeProblem);
                List<string> targets;
                while (true)
                {
                    while (usedNumbers.Contains(current)) current = checked(current + 1);
                    string number = current.ToString().PadLeft(zeroPadding, '0');
                    targets = group.Select(m => Path.Combine(dir, number + m.Tail)).ToList();
                    // Страховка: имена свободны и на диске, и в уже запланированных переименованиях
                    if (targets.All(t => !File.Exists(t) && !Directory.Exists(t) && !plannedTargets.Contains(t))) break;
                    current = checked(current + 1);
                }

                usedNumbers.Add(current);
                for (int i = 0; i < group.Count; i++)
                {
                    plannedTargets.Add(targets[i]);
                    plan.Add(new BatchRenamePlanItem { OldPath = group[i].Path, NewPath = targets[i], Group = g, Companion = group[i].Companion,
                        ScopeRoot = Normalize(folder), RecursiveScope = recursive, ActualDirectory = planGuard.ActualDirectory });
                }
                if (g < groups.Count - 1) current = checked(current + 1);
            }

            return plan;
        }

        public static BatchRenameResult Execute(List<BatchRenamePlanItem> plan, FileOperationContext? operation = null)
        {
            var result = new BatchRenameResult();

            // Группы идут подряд; отмена — только между группами, не внутри пары RAW+JPG.
            for (int start = 0; start < plan.Count;)
            {
                int end = start;
                while (end < plan.Count && plan[end].Group == plan[start].Group) end++;
                var group = plan.GetRange(start, end - start);
                start = end;
                try
                {
                    operation?.Checkpoint(FileOperationStage.Renaming, result.RenamedCount, plan.Count);
                    // Revalidate before the first disk mutation; hold Windows ancestor
                    // handles for the entire group and its rollback.
                    string groupDirectory = Path.GetDirectoryName(Path.GetFullPath(group[0].OldPath))!;
                    using var groupGuard = DirectoryPathGuard.Acquire(groupDirectory);
                    ValidateGroupPaths(group, groupGuard);

                    // Снимок исчез между планированием и выполнением — группу не трогаем.
                    if (group.Where(i => !i.Companion).All(i => !File.Exists(i.OldPath)))
                        continue;

                    var existing = group.FirstOrDefault(i => File.Exists(i.NewPath) || Directory.Exists(i.NewPath));
                    if (existing != null)
                    {
                        // Кто-то успел создать файл с целевым именем — останавливаемся, ничего не трогая
                        result.ErrorMessage = Loc.T(
                            $"Stopped: target name already exists ({Path.GetFileName(existing.NewPath)}). No files were overwritten or lost.",
                            $"Остановлено: имя уже занято ({Path.GetFileName(existing.NewPath)}). Ничего не перезаписано и не потеряно.",
                            $"Detenido: el nombre ya existe ({Path.GetFileName(existing.NewPath)}). No se sobrescribió ni perdió nada.");
                        break;
                    }

                    List<BatchRenamePlanItem> MoveGroup()
                    {
                        var done = new List<BatchRenamePlanItem>();
                        try
                        {
                            foreach (var item in group)
                            {
                                groupGuard.Validate();
                                ValidateGroupPaths(group, groupGuard);
                                string source = groupGuard.FilePath(item.OldPath), destination = groupGuard.FilePath(item.NewPath);
                                DirectoryPathGuard.RequireRegularFile(source, allowMissing: true);
                                DirectoryPathGuard.RequireRegularFile(destination, allowMissing: true);
                                if (!File.Exists(source)) continue; // спутник удалили — не повод останавливаться
                                // Canonical physical paths, no overwrite, while all parents are pinned.
                                File.Move(source, destination);
                                done.Add(item);
                            }
                            return done;
                        }
                        catch (Exception ex)
                        {
                            throw new GroupRenameException(ex, RollBack(done));
                        }
                    }
                    var moved = operation == null ? MoveGroup() : operation.Commit(MoveGroup);
                    foreach (var item in moved)
                    {
                        result.OldToNew[item.OldPath] = item.NewPath;
                        ImageSaveWriter.NotifyRenamed(item.OldPath, item.NewPath);
                    }
                    result.RenamedCount += moved.Count;
                }
                catch (OperationCanceledException) { result.Cancelled = true; break; }
                catch (GroupRenameException ex)
                {
                    var failed = group[0];
                    AppLog.Warn("BatchRenameService.Rename", ex.InnerException, AppLog.Describe(failed.OldPath));
                    result.ErrorMessage = Loc.T(
                        $"Stopped at \"{Path.GetFileName(failed.OldPath)}\": {ex.InnerException!.Message} ",
                        $"Остановлено на «{Path.GetFileName(failed.OldPath)}»: {ex.InnerException!.Message} ",
                        $"Detenido en «{Path.GetFileName(failed.OldPath)}»: {ex.InnerException!.Message} ") +
                        (ex.NotRolledBack.Count == 0
                            ? Loc.T("This photo and its companion files keep their old names; earlier files keep their new names. Nothing was overwritten or lost.",
                                    "Этот снимок и его файлы-спутники остались под старыми именами, предыдущие — под новыми. Ничего не перезаписано и не потеряно.",
                                    "Esta foto y sus archivos asociados conservan sus nombres; los anteriores, los nuevos. No se perdió nada.")
                            : Loc.T("Could not undo the partial rename of: ", "Не удалось вернуть прежние имена: ", "No se pudo deshacer el cambio de nombre de: ") +
                              string.Join(", ", ex.NotRolledBack.Select(i => Path.GetFileName(i.OldPath) + " → " + Path.GetFileName(i.NewPath))) + ".");
                    foreach (var item in ex.NotRolledBack)
                    {
                        result.OldToNew[item.OldPath] = item.NewPath;
                        ImageSaveWriter.NotifyRenamed(item.OldPath, item.NewPath);
                        result.RenamedCount++;
                    }
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("BatchRenameService.Rename", ex, AppLog.Describe(group[0].OldPath));
                    result.ErrorMessage = Loc.T(
                        $"Stopped at \"{Path.GetFileName(group[0].OldPath)}\": {ex.Message} Files already renamed keep their new names; nothing was overwritten or lost.",
                        $"Остановлено на «{Path.GetFileName(group[0].OldPath)}»: {ex.Message} Уже переименованные файлы сохранили новые имена; ничего не перезаписано и не потеряно.",
                        $"Detenido en «{Path.GetFileName(group[0].OldPath)}»: {ex.Message} Los ya renombrados conservan el nuevo nombre; no se perdió nada.");
                    break;
                }
            }

            return result;
        }

        private static void ValidateGroupPaths(IEnumerable<BatchRenamePlanItem> group, DirectoryPathGuard guard)
        {
            foreach (var item in group)
            {
                string oldDirectory = Normalize(Path.GetDirectoryName(Path.GetFullPath(item.OldPath))!);
                string newDirectory = Normalize(Path.GetDirectoryName(Path.GetFullPath(item.NewPath))!);
                if (!string.Equals(oldDirectory, newDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Renaming may not move files outside their original folder.");
                using var current = DirectoryPathGuard.Acquire(oldDirectory);
                if (!string.Equals(current.ActualDirectory, guard.ActualDirectory, StringComparison.OrdinalIgnoreCase)
                    || item.ActualDirectory.Length > 0 && !string.Equals(item.ActualDirectory, current.ActualDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The actual folder differs from the rename plan.");
                string scope = item.ScopeRoot.Length > 0 ? item.ScopeRoot : oldDirectory;
                if (ScopeProblem(scope, item.RecursiveScope) != null || ScopeProblem(oldDirectory, false) != null)
                    throw new IOException("The rename scope is no longer safe.");
            }
        }

        /// <summary>Возвращает прежние имена уже переименованным файлам группы. Результат — что вернуть не удалось.</summary>
        private static List<BatchRenamePlanItem> RollBack(List<BatchRenamePlanItem> done)
        {
            var failed = new List<BatchRenamePlanItem>();
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    using var guard = DirectoryPathGuard.Acquire(Path.GetDirectoryName(done[i].NewPath)!);
                    ValidateGroupPaths(new[] { done[i] }, guard);
                    string source = guard.FilePath(done[i].NewPath), destination = guard.FilePath(done[i].OldPath);
                    DirectoryPathGuard.RequireRegularFile(source);
                    DirectoryPathGuard.RequireRegularFile(destination, allowMissing: true);
                    guard.Validate();
                    File.Move(source, destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn("BatchRenameService.RollBack", ex, AppLog.Describe(done[i].OldPath));
                    failed.Add(done[i]);
                }
            }
            return failed;
        }

        private sealed class GroupRenameException : Exception
        {
            public List<BatchRenamePlanItem> NotRolledBack { get; }
            public GroupRenameException(Exception inner, List<BatchRenamePlanItem> notRolledBack) : base(inner.Message, inner) => NotRolledBack = notRolledBack;
        }

        private static void CollectFiles(string folder, bool recursive, List<string> results, FileOperationContext? operation)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = Normalize(folder);
            pending.Push(root);
            while (pending.Count > 0)
            {
                operation?.Checkpoint(FileOperationStage.Planning, results.Count);
                string current;
                try
                {
                    current = Normalize(pending.Pop());
                    if (!visited.Add(current)) continue;
                    if (SkipDirectory(current, isRoot: string.Equals(current, root, StringComparison.OrdinalIgnoreCase))) continue;
                }
                catch (Exception ex)
                {
                    AppLog.Debug("BatchRenameService.CheckDirectory", ex);
                    continue;
                }

                using DirectoryPathGuard currentGuard = DirectoryPathGuard.Acquire(current);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        operation?.Token.ThrowIfCancellationRequested();
                        results.Add(file);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Debug("BatchRenameService.CollectFiles", ex); }
                if (!recursive) continue;

                try
                {
                    var subdirs = new List<string>();
                    foreach (var subdir in Directory.EnumerateDirectories(current))
                    {
                        operation?.Token.ThrowIfCancellationRequested();
                        subdirs.Add(subdir);
                    }
                    // Вложенные папки — в естественном порядке, как в сетке миниатюр.
                    subdirs.Sort(NaturalStringComparer.FileName);
                    // Итеративный DFS сохраняет порядок, но не расходует стек вызовов.
                    for (int i = subdirs.Count - 1; i >= 0; i--)
                        pending.Push(subdirs[i]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Debug("BatchRenameService.CollectSubfolders", ex); }
            }
        }

        private static List<string> DefaultOrder(List<string> files)
        {
            var copy = new List<string>(files);
            copy.Sort(NaturalStringComparer.FileName);
            return copy;
        }

        private static bool IsNumericName(string name) =>
            name.Length > 0 && name.All(char.IsDigit);
    }
}
