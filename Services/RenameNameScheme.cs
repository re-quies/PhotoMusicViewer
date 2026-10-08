using System;
using System.Globalization;

namespace PhotoMusicViewer.Services
{
    public enum RenameNameStyle { Numeric, Letters, Mixed }

    /// <summary>12 схем: 6 ширин чисел, 3 минимальные длины латинских букв,
    /// 3 ширины цифрового префикса + буква. Все имена ASCII, culture-independent.</summary>
    public sealed record RenameNameScheme
    {
        public RenameNameStyle Style { get; }
        public int Width { get; }
        public const int OptionCount = 12;
        public RenameNameScheme(RenameNameStyle style, int width)
        {
            if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
            if (width < 1 || width > (style == RenameNameStyle.Numeric ? 6 : 3)) throw new ArgumentOutOfRangeException(nameof(width));
            Style = style; Width = width;
        }
        public static RenameNameScheme FromOption(int index)
        {
            if (index < 0 || index >= OptionCount) throw new ArgumentOutOfRangeException(nameof(index));
            return index < 6 ? new(RenameNameStyle.Numeric, index + 1)
                : index < 9 ? new(RenameNameStyle.Letters, index - 5)
                : new(RenameNameStyle.Mixed, index - 8);
        }
        private ulong LetterOffset
        {
            get { ulong sum = 0, power = 1; for (int i = 1; i < Width; i++) { power *= 26; sum += power; } return sum; }
        }
        public string Format(long position)
        {
            if (position < 0 || (Style != RenameNameStyle.Numeric && position == 0)) throw new ArgumentOutOfRangeException(nameof(position));
            if (Style == RenameNameStyle.Numeric) return position.ToString(CultureInfo.InvariantCulture).PadLeft(Width, '0');
            if (Style == RenameNameStyle.Mixed)
            {
                long zeroBased = position - 1;
                return (zeroBased / 26).ToString(CultureInfo.InvariantCulture).PadLeft(Width, '0') + (char)('a' + zeroBased % 26);
            }
            ulong value = checked((ulong)position + LetterOffset);
            Span<char> buffer = stackalloc char[32]; int at = buffer.Length;
            while (value > 0) { value--; buffer[--at] = (char)('a' + value % 26); value /= 26; }
            return new string(buffer[at..]);
        }
        /// <summary>Резервируем и варианты регистра/ведущих нулей во всей области.</summary>
        internal bool TryGetPosition(string stem, out long position)
        {
            position = 0;
            if (string.IsNullOrEmpty(stem)) return false;
            if (Style == RenameNameStyle.Numeric)
                return AllAsciiDigits(stem.AsSpan()) && long.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out position);
            if (Style == RenameNameStyle.Mixed)
            {
                if (stem.Length < 2) return false;
                char letter = char.ToLowerInvariant(stem[^1]);
                if (letter < 'a' || letter > 'z' || !AllAsciiDigits(stem.AsSpan(0, stem.Length - 1)) ||
                    !long.TryParse(stem.AsSpan(0, stem.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out long prefix)) return false;
                int tail = letter - 'a' + 1;
                if (prefix > (long.MaxValue - tail) / 26) return false;
                position = prefix * 26 + tail; return true;
            }
            ulong value = 0;
            foreach (char raw in stem)
            {
                char c = char.ToLowerInvariant(raw);
                if (c < 'a' || c > 'z') return false;
                ulong digit = (ulong)(c - 'a' + 1);
                if (value > (ulong.MaxValue - digit) / 26) return false;
                value = value * 26 + digit;
            }
            if (value <= LetterOffset || value - LetterOffset > long.MaxValue) return false;
            position = (long)(value - LetterOffset); return true;
        }
        private static bool AllAsciiDigits(ReadOnlySpan<char> text)
        { if (text.Length == 0) return false; foreach (char c in text) if (c < '0' || c > '9') return false; return true; }
        internal static bool IsWindowsDeviceName(string stem)
        {
            if (stem.Equals("con", StringComparison.OrdinalIgnoreCase) || stem.Equals("prn", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("aux", StringComparison.OrdinalIgnoreCase) || stem.Equals("nul", StringComparison.OrdinalIgnoreCase)) return true;
            return stem.Length == 4 && stem[3] >= '1' && stem[3] <= '9' &&
                (stem.StartsWith("com", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("lpt", StringComparison.OrdinalIgnoreCase));
        }
    }
}
