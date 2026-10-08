using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotoMusicViewer.Services
{
    /// <summary>Чем закончилось удаление.</summary>
    internal enum RecycleResult
    {
        /// <summary>Файл в корзине (или удалён после согласия в окне Windows).</summary>
        Removed,
        /// <summary>Пользователь отказался в окне Windows — файл на месте.</summary>
        Declined,
    }

    /// <summary>
    /// Удаление файла в корзину через SHFileOperation.
    ///
    /// Раньше использовался FileSystem.DeleteFile(..., UIOption.OnlyErrorDialogs): этот режим
    /// включает FOF_NOCONFIRMATION, а флага FOF_WANTNUKEWARNING нет. Поэтому файл, который не
    /// может попасть в корзину (флешка, сетевой диск, файл больше корзины), стирался навсегда
    /// и без вопроса — вопреки комментарию в коде.
    ///
    /// Теперь: FOF_ALLOWUNDO (в корзину) + FOF_WANTNUKEWARNING — если корзина недоступна,
    /// Windows обязательно спросит «Удалить безвозвратно?». Обычное подтверждение
    /// «Переместить в корзину?» показывает само приложение (настройка «Спрашивать перед
    /// удалением»), поэтому системное подавлено (FOF_NOCONFIRMATION) — но только оно.
    /// Ошибки (файл занят, нет прав) не прячутся: бросается исключение с понятным текстом.
    /// </summary>
    /// <summary>Удалить не удалось; файл на месте. ReportedByWindows — Windows уже показала своё окно.</summary>
    internal sealed class RecycleException : IOException
    {
        public bool ReportedByWindows { get; }
        public RecycleException(string message, int code, bool reportedByWindows) : base(message, code) => ReportedByWindows = reportedByWindows;
    }

    internal static class RecycleBinService
    {
        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040,
            FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>Флаги удаления: корзина, предупреждение о безвозвратном удалении, без системного «Вы уверены?».</summary>
        // FOF_NOERRORUI намеренно НЕ ставится: окна Windows не подавляются, чтобы ничто не
        // помешало показать предупреждение о безвозвратном удалении.
        internal const ushort Flags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING | FOF_NOCONFIRMATION | FOF_SILENT;

        /// <summary>
        /// Отправляет файл в корзину. owner — окно-владелец для предупреждения Windows.
        /// Declined — пользователь отказался от безвозвратного удаления, файл на месте.
        /// Исключение — удалить не удалось (файл на месте, причина в сообщении).
        /// </summary>
        public static RecycleResult Recycle(string path, IntPtr owner = default)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException(Loc.T("The file no longer exists.", "Файла уже нет.", "El archivo ya no existe."), path);
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recycle Bin requires Windows.");

            int code; bool aborted;
            // Строка-источник обязана заканчиваться двумя нулями: маршалер добавляет один.
            string from = path + "\0";
            if (IntPtr.Size == 8)
            {
                var op = new FileOp64 { hwnd = owner, wFunc = FO_DELETE, pFrom = from, fFlags = Flags };
                code = SHFileOperation64(ref op); aborted = op.fAnyOperationsAborted;
            }
            else
            {
                var op = new FileOp32 { hwnd = owner, wFunc = FO_DELETE, pFrom = from, fFlags = Flags };
                code = SHFileOperation32(ref op); aborted = op.fAnyOperationsAborted;
            }

            if (aborted || code == 0x4C7 /* ERROR_CANCELLED */) return RecycleResult.Declined;
            // Об этой ошибке Windows уже сообщила своим окном (FOF_NOERRORUI не стоит).
            if (code != 0) throw new RecycleException(Describe(code), code, reportedByWindows: true);
            // SHFileOperation иногда возвращает 0, ничего не сделав (например, файл занят и
            // ошибки подавлены) — проверяем факт.
            if (File.Exists(path))
                throw new RecycleException(Loc.T("The file is still in place: it may be open in another program.",
                    "Файл остался на месте: возможно, он открыт в другой программе.",
                    "El archivo sigue en su sitio: puede estar abierto en otro programa."), 0, reportedByWindows: false);
            return RecycleResult.Removed;
        }

        /// <summary>Тексты для кодов SHFileOperation (часть из них — устаревшие коды DE_*).</summary>
        internal static string Describe(int code) => code switch
        {
            0x20 or 0x21 => Loc.T("The file is open in another program.", "Файл открыт в другой программе.", "El archivo está abierto en otro programa."),
            0x5 or 0x78 => Loc.T("Access denied.", "Нет доступа.", "Acceso denegado."),
            0x2 or 0x3 or 0x402 => Loc.T("The file was not found.", "Файл не найден.", "No se encontró el archivo."),
            0x13 => Loc.T("The disk is write-protected.", "Диск защищён от записи.", "El disco está protegido contra escritura."),
            _ => Loc.T($"Windows could not delete the file (code 0x{code:X}).", $"Windows не смогла удалить файл (код 0x{code:X}).", $"Windows no pudo eliminar el archivo (código 0x{code:X}).")
        };

        /// <summary>
        /// Удаление по уже полученному согласию (конвертация WebP): в корзину; если она
        /// недоступна, Windows спросит про безвозвратное удаление. true — файла больше нет.
        /// </summary>
        public static bool TryDeleteWithConfirmation(string path, IntPtr owner = default)
        {
            try { return Recycle(path, owner) == RecycleResult.Removed; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                AppLog.Warn("RecycleBinService.TryDeleteWithConfirmation", ex, AppLog.Describe(path));
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileOp64
        {
            public IntPtr hwnd; public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        // В 32-битной Windows SHFILEOPSTRUCT упакована по 1 байту (pshpack1.h), в 64-битной — нет.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct FileOp32
        {
            public IntPtr hwnd; public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation64(ref FileOp64 op);

        [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation32(ref FileOp32 op);
    }
}
