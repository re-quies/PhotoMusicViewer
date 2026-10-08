using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PhotoMusicViewer.Services
{
    /// <summary>Что удалось убрать. Нужен, чтобы вызывающий код мог не угадывать, а знать.</summary>
    public readonly record struct RecentTracesResult(
        int ShortcutsDeleted,
        bool JumpListCleared,
        int RegistryEntriesDeleted)
    {
        public bool AnythingRemoved => ShortcutsDeleted > 0 || JumpListCleared || RegistryEntriesDeleted > 0;
    }

    /// <summary>
    /// <b>По умолчанию ничего не делает.</b> Каждая часть включается отдельной галочкой
    /// в настройках (<see cref="TraceCleanupOptions"/>): «Недавние» (пункты 1–2) и
    /// реестр Проводника (пункт 3).
    ///
    /// Убирает следы открытия файла из трёх мест, куда стандартные диалоги
    /// Windows записывают его автоматически:
    ///
    ///   1. <b>%APPDATA%\Microsoft\Windows\Recent</b> — ярлыки .lnk. Сопоставление идёт
    ///      по <b>цели</b> ярлыка (разбор двоичного формата Shell Link), а не по имени
    ///      файла ярлыка. Старая версия сравнивала имена и поэтому одновременно
    ///      промахивалась (ярлык может называться иначе) и била мимо (удаляла чужой
    ///      ярлык на файл с таким же именем из другой папки).
    ///   2. <b>Список переходов (jump list)</b> — через IApplicationDestinations.
    ///      Очищается только список <b>этого</b> приложения: чужие не трогаем.
    ///   3. <b>Реестр</b> — RecentDocs, ComDlg32\OpenSavePidlMRU, LastVisitedPidlMRU, CIDSizeMRU:
    ///      только достоверный полный путь; значения и порядок MRUListEx меняются
    ///      одной транзакцией Windows. При конфликте/недоступной TxR очистка отменяется.
    ///
    /// <b>Чего этот класс не делает</b> (чтобы название не обещало лишнего):
    /// не чистит кэш эскизов (thumbcache_*.db — там нет поэлементного удаления),
    /// не трогает индекс поиска Windows, журнал USN, теневые копии и «Недавние»
    /// в облачных клиентах (OneDrive и пр.).
    ///
    /// Правильнее всего было бы вообще не создавать запись — флаг FOS_DONTADDTORECENT
    /// у IFileOpenDialog. В Microsoft.Win32.OpenFileDialog он не выведен наружу, так что это
    /// остаётся уборкой постфактум до перехода на собственный диалог.
    /// </summary>
    public static class RecentTracesService
    {
        /// <summary>
        /// Убирает следы только что открытого файла или папки.
        /// Никогда не бросает исключений и не показывает окон.
        /// </summary>
        public static RecentTracesResult Erase(string openedPath)
        {
            if (string.IsNullOrWhiteSpace(openedPath)) return default;
            // Обе функции по умолчанию выключены и включаются в настройках
            if (!TraceCleanupOptions.AnyEnabled) return default;
            // Фоновая уборка после запуска из Проводника и уборка после диалога/перетаскивания
            // могут совпасть по времени: реестр MRU переписываем строго по одному.
            lock (EraseLock) return EraseCore(openedPath);
        }

        private static readonly object EraseLock = new();

        // ----------------------------------------------- запуск двойным кликом / «Открыть с помощью»

        /// <summary>
        /// Моменты (мс от запуска), когда повторяется уборка после открытия из Проводника.
        /// Проводник пишет RecentDocs и ярлык в Recent сам, и ярлык/список переходов
        /// нередко появляются уже после старта нашего процесса. Одной уборки «сразу»
        /// мало — она проходит раньше, чем Проводник успевает записать след.
        /// </summary>
        internal static readonly int[] ShellLaunchEraseScheduleMs = { 0, 1500, 5000, 15000 };

        private static string? _pendingShellLaunchPath;

        /// <summary>
        /// Уборка следов файла, переданного в командной строке (двойной клик в Проводнике,
        /// «Открыть с помощью», перетаскивание на значок EXE). Идёт на фоновом STA-потоке
        /// с пониженным приоритетом и не задерживает показ файла. Если приложение закрыли
        /// раньше последней попытки, <see cref="FinishPendingShellLaunchErase"/> делает
        /// ещё одну уборку при выходе.
        /// </summary>
        public static void EraseAfterShellLaunch(string openedPath)
        {
            if (string.IsNullOrWhiteSpace(openedPath)) return;
            // Уборка выключена (по умолчанию) — фоновый поток даже не создаётся
            if (!TraceCleanupOptions.AnyEnabled) return;
            Volatile.Write(ref _pendingShellLaunchPath, openedPath);

            try
            {
                var thread = new Thread(() => RunShellLaunchSchedule(openedPath))
                {
                    IsBackground = true,
                    Name = "PMV recent traces",
                    Priority = ThreadPriority.BelowNormal
                };
                // IApplicationDestinations — COM-объект оболочки, ему нужен STA
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch (Exception ex)
            {
                AppLog.Debug("RecentTracesService.EraseAfterShellLaunch", ex);
                Erase(openedPath); // поток не создался — хотя бы одна уборка сразу
            }
        }

        private static void RunShellLaunchSchedule(string openedPath)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (int at in ShellLaunchEraseScheduleMs)
            {
                long wait = at - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                try { Erase(openedPath); }
                catch (Exception ex) { AppLog.Debug("RecentTracesService.ShellLaunchErase", ex); }
            }
            Interlocked.CompareExchange(ref _pendingShellLaunchPath, null, openedPath);
        }

        /// <summary>Вызывается при выходе: если расписание уборки не успело пройти
        /// до конца, убирает следы ещё раз синхронно. Никогда не бросает исключений.</summary>
        public static void FinishPendingShellLaunchErase()
        {
            string? path = Interlocked.Exchange(ref _pendingShellLaunchPath, null);
            if (path == null) return;
            try { Erase(path); }
            catch (Exception ex) { AppLog.Debug("RecentTracesService.FinishPendingShellLaunchErase", ex); }
        }

        private static RecentTracesResult EraseCore(string openedPath)
        {

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(openedPath.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                AppLog.Debug("RecentTracesService.Erase путь", ex);
                return default;
            }

            // Корень тома ("C:") после TrimEnd превращается в бессмысленный шаблон — чистить нечего
            if (fullPath.Length == 0 || Path.GetFileName(fullPath).Length == 0) return default;

            // Настройки читаются при каждой уборке: выключенная во время работы
            // функция перестаёт действовать и для уже запланированных повторов.
            bool recentItems = TraceCleanupOptions.RecentItems;
            bool explorerRegistry = TraceCleanupOptions.ExplorerRegistry;
            int shortcuts = recentItems ? RemoveRecentShortcuts(fullPath) : 0;
            bool jumpList = recentItems && ClearOwnJumpList();
            int registry = explorerRegistry ? RemoveRegistryMruEntries(fullPath) : 0;

            var result = new RecentTracesResult(shortcuts, jumpList, registry);
            AppLog.Debug("RecentTracesService.Erase", null,
                $"lnk={shortcuts} jumplist={jumpList} registry={registry}");
            return result;
        }

        // ------------------------------------------------------------- 1. ярлыки .lnk

        private static int RemoveRecentShortcuts(string fullPath)
        {
            int deleted = 0;
            try
            {
                string recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (!Directory.Exists(recentFolder)) return 0;

                foreach (var lnk in Directory.EnumerateFiles(recentFolder, "*.lnk"))
                {
                    if (!ShouldRemoveShortcut(lnk, fullPath, recentFolder)) continue;

                    try
                    {
                        File.Delete(lnk);
                        deleted++;
                    }
                    catch (Exception ex) { AppLog.Debug("RecentTracesService.DeleteShortcut", ex); }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("RecentTracesService.RemoveRecentShortcuts", ex);
            }

            return deleted;
        }

        internal static bool ShouldRemoveShortcut(string shortcut, string fullPath, string recentFolder) =>
            RecentShortcutPolicy.Matches(ShellLink.TryReadTarget(shortcut, recentFolder), fullPath);

        // ------------------------------------------------------------- 2. список переходов

        // CLSID_ApplicationDestinations / IID_IApplicationDestinations (shobjidl_core.h)
        private static readonly Guid ApplicationDestinationsClsid =
            new("86c14003-4d6b-4ef3-a7b4-0506663b2e68");

        [ComImport]
        [Guid("12337d35-94c6-48a0-bce7-6a9c69d4d600")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationDestinations
        {
            void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
            void RemoveDestination([MarshalAs(UnmanagedType.IUnknown)] object punk);
            void RemoveAllDestinations();
        }

        /// <summary>
        /// Очищает список переходов текущего приложения целиком.
        ///
        /// Поэлементное удаление (RemoveDestination) требует IShellItem ровно того же
        /// экземпляра, что лежит в списке, поэтому чистим всё: в этом списке в любом
        /// случае только те файлы, которые открывались в этом приложении.
        /// </summary>
        private static bool ClearOwnJumpList()
        {
            object? instance = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ApplicationDestinationsClsid);
                if (type == null) return false;

                instance = Activator.CreateInstance(type);
                if (instance is not IApplicationDestinations destinations) return false;

                destinations.RemoveAllDestinations();
                return true;
            }
            catch (Exception ex)
            {
                // Например, списки переходов отключены политикой — чистить просто нечего
                AppLog.Debug("RecentTracesService.ClearOwnJumpList", ex);
                return false;
            }
            finally
            {
                if (instance != null && Marshal.IsComObject(instance))
                {
                    try { Marshal.ReleaseComObject(instance); }
                    catch (Exception ex) { AppLog.Debug("RecentTracesService.ReleaseComObject", ex); }
                }
            }
        }

        // ------------------------------------------------------------- 3. реестр

        private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

        private static readonly string[] MruKeys =
        {
            ExplorerKey + @"\RecentDocs",
            ExplorerKey + @"\ComDlg32\OpenSavePidlMRU",
            ExplorerKey + @"\ComDlg32\LastVisitedPidlMRU",
            ExplorerKey + @"\ComDlg32\CIDSizeMRU"
        };

        private static int RemoveRegistryMruEntries(string fullPath)
        {
            try
            {
                return RegistryMruCleanup.Clean(() => new WindowsRegistryMruTransaction(), MruKeys, BuildMatcher(fullPath));
            }
            catch (Exception ex)
            {
                // Fail closed: transaction unavailable, conflict, access failure or malformed
                // state means no partial delete/rewrite and no nontransactional fallback.
                AppLog.Warn("RecentTracesService.RegistryCleanupAborted", ex);
                return 0;
            }
        }

        /// <summary>Only an exact embedded full-path string establishes ownership.
        /// PIDLs containing just a name or separate parent components are left untouched.</summary>
        private static Func<byte[], bool> BuildMatcher(string fullPath) =>
            data => RegistryMruCleanup.ContainsExactPath(data, fullPath);

        // ------------------------------------------------------- разбор формата .lnk

        /// <summary>
        /// Минимальный разбор Shell Link Binary File Format (MS-SHLLINK) — только ради пути цели.
        /// COM (IShellLink) здесь не используется специально: его Resolve() умеет ходить в сеть
        /// и поднимать съёмные носители, а нам нужно тихое чтение байтов.
        /// </summary>
        private static class ShellLink
        {
            private const int HeaderSize = 0x4C;
            private const uint HasLinkTargetIdList = 0x00000001;
            private const uint HasLinkInfo = 0x00000002;
            private const uint HasName = 0x00000004;
            private const uint HasRelativePath = 0x00000008;
            private const uint IsUnicode = 0x00000080;

            /// <summary>Возвращает полный путь цели или null, если его не вычитать.</summary>
            public static string? TryReadTarget(string lnkPath, string lnkFolder)
            {
                try
                {
                    var data = File.ReadAllBytes(lnkPath);
                    if (data.Length < HeaderSize || BitConverter.ToInt32(data, 0) != HeaderSize) return null;

                    uint flags = BitConverter.ToUInt32(data, 0x14);
                    int offset = HeaderSize;

                    if ((flags & HasLinkTargetIdList) != 0)
                    {
                        if (offset + 2 > data.Length) return null;
                        int idListSize = BitConverter.ToUInt16(data, offset);
                        offset += 2 + idListSize;
                    }

                    string? fromLinkInfo = (flags & HasLinkInfo) != 0
                        ? ReadLinkInfoPath(data, ref offset)
                        : null;

                    if (!string.IsNullOrEmpty(fromLinkInfo)) return Normalize(fromLinkInfo);

                    // Запасной вариант: RELATIVE_PATH считается от папки с ярлыком
                    string? relative = ReadRelativePath(data, offset, flags);
                    if (string.IsNullOrEmpty(relative)) return null;

                    return Normalize(Path.Combine(lnkFolder, relative));
                }
                catch (Exception ex)
                {
                    AppLog.Debug("ShellLink.TryReadTarget", ex);
                    return null;
                }
            }

            private static string Normalize(string path) =>
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            /// <summary>LinkInfo: LocalBasePath + CommonPathSuffix, предпочитая Unicode-варианты полей.</summary>
            private static string? ReadLinkInfoPath(byte[] data, ref int offset)
            {
                int start = offset;
                if (start + 8 > data.Length) return null;

                int infoSize = BitConverter.ToInt32(data, start);
                int headerSize = BitConverter.ToInt32(data, start + 4);
                if (infoSize <= 0 || start + infoSize > data.Length || headerSize < 0x1C) return null;

                offset = start + infoSize; // сдвигаем курсор на StringData даже если путь не соберётся

                uint infoFlags = BitConverter.ToUInt32(data, start + 8);
                const uint volumeIdAndLocalBasePath = 0x00000001;
                if ((infoFlags & volumeIdAndLocalBasePath) == 0) return null;

                bool hasUnicode = headerSize >= 0x24;

                string? basePath = hasUnicode
                    ? ReadStringAt(data, start, BitConverter.ToInt32(data, start + 28), unicode: true)
                    : null;
                basePath ??= ReadStringAt(data, start, BitConverter.ToInt32(data, start + 16), unicode: false);
                if (string.IsNullOrEmpty(basePath)) return null;

                string? suffix = hasUnicode
                    ? ReadStringAt(data, start, BitConverter.ToInt32(data, start + 32), unicode: true)
                    : null;
                suffix ??= ReadStringAt(data, start, BitConverter.ToInt32(data, start + 24), unicode: false);

                return string.IsNullOrEmpty(suffix) ? basePath : basePath + suffix;
            }

            private static string? ReadStringAt(byte[] data, int structStart, int relativeOffset, bool unicode)
            {
                if (relativeOffset <= 0) return null;

                int position = structStart + relativeOffset;
                if (position < 0 || position >= data.Length) return null;

                if (!unicode)
                {
                    int end = position;
                    while (end < data.Length && data[end] != 0) end++;
                    // Latin1 вместо текущей ANSI-кодовой страницы: в .NET её всё равно нет,
                    // а современная Windows пишет Unicode-вариант поля
                    return Encoding.Latin1.GetString(data, position, end - position);
                }

                int stop = position;
                while (stop + 1 < data.Length && !(data[stop] == 0 && data[stop + 1] == 0)) stop += 2;
                return Encoding.Unicode.GetString(data, position, stop - position);
            }

            /// <summary>StringData идёт строго по порядку: NAME_STRING, RELATIVE_PATH, дальше нам не нужно.</summary>
            private static string? ReadRelativePath(byte[] data, int offset, uint flags)
            {
                if ((flags & HasRelativePath) == 0) return null;

                bool unicode = (flags & IsUnicode) != 0;

                if ((flags & HasName) != 0 && !SkipCountedString(data, ref offset, unicode)) return null;

                return ReadCountedString(data, ref offset, unicode);
            }

            private static bool SkipCountedString(byte[] data, ref int offset, bool unicode) =>
                ReadCountedString(data, ref offset, unicode) != null;

            private static string? ReadCountedString(byte[] data, ref int offset, bool unicode)
            {
                if (offset + 2 > data.Length) return null;

                int characters = BitConverter.ToUInt16(data, offset);
                offset += 2;

                int bytes = unicode ? characters * 2 : characters;
                if (bytes < 0 || offset + bytes > data.Length) return null;

                var text = unicode
                    ? Encoding.Unicode.GetString(data, offset, bytes)
                    : Encoding.Latin1.GetString(data, offset, bytes);

                offset += bytes;
                return text;
            }
        }
    }
}
