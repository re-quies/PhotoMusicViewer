using System;
using System.IO;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Включает уборку следов открытия файлов в Windows. Обе функции по умолчанию
    /// <b>выключены</b>: они меняют чужие данные (реестр Проводника, папку «Недавние»),
    /// и делать это без явного согласия пользователя приложение не должно.
    ///
    /// - <see cref="ExplorerRegistry"/> — удалять записи о файле из реестра Проводника
    ///   (RecentDocs, ComDlg32\OpenSavePidlMRU / LastVisitedPidlMRU / CIDSizeMRU).
    /// - <see cref="RecentItems"/> — удалять ярлыки файла из папки «Недавние»
    ///   (%APPDATA%\Microsoft\Windows\Recent) и очищать список переходов самого приложения.
    ///
    /// Состояние запоминается пустыми файлами-метками в %LOCALAPPDATA%\PhotoMusicViewer —
    /// так же, как галочка журнала: оно нужно уже при запуске (двойной клик по файлу),
    /// до открытия окна настроек, и не зависит от того, сохраняются ли настройки перевода.
    /// Метки не содержат данных. Класс никогда не бросает исключений.
    /// </summary>
    internal static class TraceCleanupOptions
    {
        private const string RegistryMarkerName = "clean-explorer-registry";
        private const string RecentMarkerName = "clean-recent-items";

        private static readonly object Gate = new();
        private static bool _loaded, _registry, _recent;

        /// <summary>Папка меток; переопределяется только тестами.</summary>
        internal static string DirectoryPath { get; set; } = BuildDirectory();

        /// <summary>Удалять записи о файле из реестра Проводника. По умолчанию выключено.</summary>
        public static bool ExplorerRegistry { get { EnsureLoaded(); return _registry; } }

        /// <summary>Удалять ярлыки из «Недавних» и чистить список переходов. По умолчанию выключено.</summary>
        public static bool RecentItems { get { EnsureLoaded(); return _recent; } }

        public static bool AnyEnabled => ExplorerRegistry || RecentItems;

        /// <summary>Применяется сразу и переживает перезапуск. false — метку записать не удалось
        /// (значение действует только до выхода).</summary>
        public static bool SetExplorerRegistry(bool on) => Set(RegistryMarkerName, on, ref _registry);

        /// <summary>Применяется сразу и переживает перезапуск. false — метку записать не удалось.</summary>
        public static bool SetRecentItems(bool on) => Set(RecentMarkerName, on, ref _recent);

        /// <summary>Перечитать метки с диска (для тестов).</summary>
        internal static void Reload()
        {
            lock (Gate) _loaded = false;
        }

        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (_loaded) return;
                _registry = MarkerExists(RegistryMarkerName);
                _recent = MarkerExists(RecentMarkerName);
                _loaded = true;
            }
        }

        private static bool Set(string marker, bool on, ref bool field)
        {
            EnsureLoaded();
            lock (Gate)
            {
                field = on;
                try
                {
                    if (DirectoryPath.Length == 0) return false;
                    string path = Path.Combine(DirectoryPath, marker);
                    if (on)
                    {
                        Directory.CreateDirectory(DirectoryPath);
                        File.WriteAllBytes(path, Array.Empty<byte>());
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("TraceCleanupOptions.Set", ex);
                    return false;
                }
            }
        }

        private static bool MarkerExists(string marker)
        {
            try
            {
                return DirectoryPath.Length > 0 && File.Exists(Path.Combine(DirectoryPath, marker));
            }
            catch (Exception ex)
            {
                AppLog.Debug("TraceCleanupOptions.MarkerExists", ex);
                return false;
            }
        }

        private static string BuildDirectory()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return local.Length == 0 ? "" : Path.Combine(local, "PhotoMusicViewer");
            }
            catch (Exception ex)
            {
                AppLog.Debug("TraceCleanupOptions.BuildDirectory", ex);
                return "";
            }
        }
    }
}
