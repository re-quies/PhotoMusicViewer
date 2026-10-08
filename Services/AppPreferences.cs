using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoMusicViewer.Services
{
    /// <summary>Порядок сортировки, общий для фото и плейлиста (тот же порядок, что в выпадающих списках).</summary>
    public enum FileSortKey { Name = 0, DateModified = 1, Size = 2, Type = 3 }

    /// <summary>Язык интерфейса при запуске. System — как в Windows.</summary>
    public enum StartupLanguage { System = 0, English = 1, Russian = 2, Spanish = 3 }

    /// <summary>
    /// Настройки интерфейса: язык при запуске, сортировка фото и плейлиста, громкость.
    /// Значение — то, что применяется при запуске (и при открытии новой папки фото);
    /// галочки Remember* решают, обновляется ли оно изменениями на панели инструментов.
    /// </summary>
    public sealed class PreferenceValues
    {
        public StartupLanguage Language { get; set; } = StartupLanguage.System;
        public bool RememberLanguage { get; set; } = true;

        // Фото: по дате, новые первыми (раньше было «по дате, по возрастанию»).
        public FileSortKey PhotoSort { get; set; } = FileSortKey.DateModified;
        public bool PhotoSortDescending { get; set; } = true;
        public FileSortKey MusicSort { get; set; } = FileSortKey.Name;
        public bool MusicSortDescending { get; set; }
        public bool RememberSort { get; set; } = true;

        /// <summary>Спрашивать «Переместить в корзину?» перед удалением фото. Предупреждение
        /// Windows о безвозвратном удалении показывается всегда, независимо от этой галочки.</summary>
        public bool ConfirmDelete { get; set; } = true;

        public double Volume { get; set; } = 0.8;
        public bool RememberVolume { get; set; } = true;

        public PreferenceValues Clone() => (PreferenceValues)MemberwiseClone();

        /// <summary>Неизвестные значения перечислений и битая громкость — к значениям по умолчанию.</summary>
        public void EnsureValid()
        {
            var defaults = new PreferenceValues();
            if (!Enum.IsDefined(Language)) Language = defaults.Language;
            if (!Enum.IsDefined(PhotoSort)) PhotoSort = defaults.PhotoSort;
            if (!Enum.IsDefined(MusicSort)) MusicSort = defaults.MusicSort;
            if (double.IsNaN(Volume) || double.IsInfinity(Volume)) Volume = defaults.Volume;
            Volume = Math.Round(Math.Clamp(Volume, 0, 1), 3);
        }
    }

    /// <summary>
    /// Хранит <see cref="PreferenceValues"/> в %LOCALAPPDATA%\PhotoMusicViewer\preferences.json.
    /// В файле нет ни путей, ни имён файлов, ни ключей — только перечисленные выше значения,
    /// поэтому он не зависит от галочки «Сохранять настройки перевода».
    /// Запись — через временный файл с атомарной заменой. Класс никогда не бросает исключений:
    /// битый или недоступный файл означает значения по умолчанию.
    /// </summary>
    public static class AppPreferences
    {
        public const string FileName = "preferences.json";
        private const int MaxFileBytes = 64 * 1024;

        private static readonly object Gate = new();
        private static PreferenceValues? _current;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Папка файла; переопределяется только тестами.</summary>
        internal static string DirectoryPath { get; set; } = BuildDirectory();

        internal static string FilePath => DirectoryPath.Length == 0 ? "" : Path.Combine(DirectoryPath, FileName);

        /// <summary>После сохранения из окна настроек: экраны применяют сортировку и громкость.</summary>
        public static event Action? Changed;

        /// <summary>Копия текущих значений (изменения копии ничего не меняют).</summary>
        public static PreferenceValues Current
        {
            get { lock (Gate) return (_current ??= Load()).Clone(); }
        }

        /// <summary>Сохранить значения из окна настроек и сообщить экранам.</summary>
        public static bool Save(PreferenceValues values, bool notify = true)
        {
            var copy = values.Clone();
            copy.EnsureValid();
            bool written;
            lock (Gate)
            {
                _current = copy;
                written = Write(copy);
            }
            if (notify) Changed?.Invoke();
            return written;
        }

        /// <summary>Изменение с панели инструментов: сохраняется, только если включено запоминание.</summary>
        public static void Update(Func<PreferenceValues, bool> shouldApply, Action<PreferenceValues> change)
        {
            lock (Gate)
            {
                var copy = (_current ??= Load()).Clone();
                if (!shouldApply(copy)) return;
                change(copy);
                copy.EnsureValid();
                _current = copy;
                Write(copy);
            }
        }

        public static void RememberLanguage(StartupLanguage language) =>
            Update(p => p.RememberLanguage && p.Language != language, p => p.Language = language);

        public static void RememberPhotoSort(FileSortKey key, bool descending) =>
            Update(p => p.RememberSort && (p.PhotoSort != key || p.PhotoSortDescending != descending),
                   p => { p.PhotoSort = key; p.PhotoSortDescending = descending; });

        public static void RememberMusicSort(FileSortKey key, bool descending) =>
            Update(p => p.RememberSort && (p.MusicSort != key || p.MusicSortDescending != descending),
                   p => { p.MusicSort = key; p.MusicSortDescending = descending; });

        public static void RememberVolume(double volume) =>
            Update(p => p.RememberVolume && Math.Abs(p.Volume - Math.Round(Math.Clamp(volume, 0, 1), 3)) > 0.0005,
                   p => p.Volume = volume);

        /// <summary>Язык, который реально включить: System → по языку интерфейса Windows.</summary>
        public static StartupLanguage Resolve(StartupLanguage language, string? systemTwoLetterCode)
        {
            if (language != StartupLanguage.System) return language;
            return (systemTwoLetterCode ?? "").ToLowerInvariant() switch
            {
                "ru" or "be" or "uk" or "kk" => StartupLanguage.Russian,
                "es" => StartupLanguage.Spanish,
                _ => StartupLanguage.English
            };
        }

        /// <summary>Перечитать с диска (для тестов).</summary>
        internal static void Reload()
        {
            lock (Gate) _current = null;
        }

        internal static PreferenceValues Parse(string json)
        {
            var values = JsonSerializer.Deserialize<PreferenceValues>(json, JsonOptions) ?? new PreferenceValues();
            values.EnsureValid();
            return values;
        }

        internal static string Serialize(PreferenceValues values) => JsonSerializer.Serialize(values, JsonOptions);

        private static PreferenceValues Load()
        {
            try
            {
                string path = FilePath;
                if (path.Length == 0 || !File.Exists(path)) return new PreferenceValues();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxFileBytes) throw new InvalidDataException("Preferences file is too large.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                // Битый файл не мешает запуску: значения по умолчанию, файл перезапишется при сохранении.
                AppLog.Warn("AppPreferences.Load", ex);
                return new PreferenceValues();
            }
        }

        private static bool Write(PreferenceValues values)
        {
            try
            {
                if (DirectoryPath.Length == 0) return false;
                Directory.CreateDirectory(DirectoryPath);
                byte[] bytes = Encoding.UTF8.GetBytes(Serialize(values));
                SafeFileReplace.WriteThenReplace(FilePath, stream => stream.Write(bytes, 0, bytes.Length));
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("AppPreferences.Write", ex);
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
                AppLog.Debug("AppPreferences.BuildDirectory", ex);
                return "";
            }
        }
    }
}
