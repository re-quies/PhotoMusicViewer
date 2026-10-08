using System;
using System.Collections.Generic;
using System.IO;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Естественный порядок имён, как в Проводнике: img2 &lt; img10 &lt; img100.
    ///
    /// - Цифровые фрагменты (только ASCII 0–9) сравниваются по значению. Длина числа
    ///   не ограничена: значения не разбираются в long, сравниваются значащие цифры,
    ///   поэтому имя из 40 цифр не переполняет и не ломает сортировку.
    /// - Ведущие нули не влияют на значение: 007 == 7. При полном равенстве
    ///   остального меньше нулей — раньше (1 &lt; 01 &lt; 001).
    /// - Знаки и пробелы идут раньше цифр, цифры раньше букв (как в Проводнике).
    /// - Остальной текст — без учёта регистра, посимвольно (ToUpperInvariant):
    ///   результат одинаков на любой машине и не зависит от языка Windows.
    /// - Полный порядок: если строки равны по всем правилам, решает обычное
    ///   ординальное сравнение, так что разные строки никогда не «равны» —
    ///   порядок не прыгает между запусками и после FileSystemWatcher.
    ///
    /// Управляемая реализация, а не StrCmpLogicalW: работает в переносимых тестах
    /// и не зависит от политики Windows «NoStrCmpLogical».
    /// </summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        /// <summary>Сравнивает по имени файла без пути: сначала имя, затем расширение, затем полный путь.</summary>
        public static readonly IComparer<string> FileName = Comparer<string>.Create(CompareFileNames);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int natural = CompareNatural(x.AsSpan(), y.AsSpan(), out int zeroTie);
            if (natural != 0) return natural;
            if (zeroTie != 0) return zeroTie;
            int caseTie = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            return caseTie != 0 ? Math.Sign(caseTie) : Math.Sign(string.CompareOrdinal(x, y));
        }

        public static int CompareFileNames(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            // «фото.jpg» раньше «фото (2).jpg» и «фото1.jpg»: имя без расширения важнее точки.
            int c = Instance.CompareCore(Path.GetFileNameWithoutExtension(x.AsSpan()), Path.GetFileNameWithoutExtension(y.AsSpan()));
            if (c != 0) return c;
            c = Instance.CompareCore(Path.GetExtension(x.AsSpan()), Path.GetExtension(y.AsSpan()));
            if (c != 0) return c;
            return Instance.Compare(x, y); // одинаковые имена в разных папках — по пути
        }

        private int CompareCore(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
        {
            int natural = CompareNatural(x, y, out int zeroTie);
            if (natural != 0) return natural;
            if (zeroTie != 0) return zeroTie;
            int caseTie = x.CompareTo(y, StringComparison.OrdinalIgnoreCase);
            return caseTie != 0 ? Math.Sign(caseTie) : Math.Sign(x.CompareTo(y, StringComparison.Ordinal));
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static int CharClass(char c) => IsDigit(c) ? 1 : char.IsLetter(c) ? 2 : 0;

        /// <summary>
        /// Основное сравнение. zeroTie — первое различие в количестве ведущих нулей
        /// у равных по значению чисел; применяется, только если всё остальное совпало.
        /// </summary>
        private static int CompareNatural(ReadOnlySpan<char> x, ReadOnlySpan<char> y, out int zeroTie)
        {
            zeroTie = 0;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                char a = x[i], b = y[j];
                if (IsDigit(a) && IsDigit(b))
                {
                    int si = i, sj = j;
                    while (i < x.Length && x[i] == '0') i++;
                    while (j < y.Length && y[j] == '0') j++;
                    int zi = i - si, zj = j - sj;
                    int vi = i, vj = j;
                    while (i < x.Length && IsDigit(x[i])) i++;
                    while (j < y.Length && IsDigit(y[j])) j++;
                    int li = i - vi, lj = j - vj;
                    if (li != lj) return li < lj ? -1 : 1; // больше значащих цифр — больше число
                    for (int k = 0; k < li; k++)
                    {
                        char da = x[vi + k], db = y[vj + k];
                        if (da != db) return da < db ? -1 : 1;
                    }
                    if (zeroTie == 0 && zi != zj) zeroTie = zi < zj ? -1 : 1;
                    continue;
                }

                if (a != b)
                {
                    char ua = char.ToUpperInvariant(a), ub = char.ToUpperInvariant(b);
                    if (ua != ub)
                    {
                        // Как в Проводнике: знаки и пробелы, затем цифры, затем буквы
                        // («a_1» < «a1» < «ab»), внутри группы — по коду символа.
                        int ca = CharClass(a), cb = CharClass(b);
                        if (ca != cb) return ca < cb ? -1 : 1;
                        return ua < ub ? -1 : 1;
                    }
                }
                i++; j++;
            }
            // Более короткая строка-префикс идёт первой.
            int restX = x.Length - i, restY = y.Length - j;
            return restX == restY ? 0 : (restX < restY ? -1 : 1);
        }
    }
}
