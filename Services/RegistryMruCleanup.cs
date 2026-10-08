using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PhotoMusicViewer.Services
{
    internal interface IRegistryMruKey : IDisposable
    {
        string[] ValueNames();
        string[] SubKeyNames();
        byte[]? ReadBinary(string name);
        void DeleteValue(string name);
        void WriteOrder(byte[] order);
    }

    internal interface IRegistryMruTransaction : IDisposable
    {
        IRegistryMruKey? OpenCurrentUser(string path);
        void Commit();
    }

    /// <summary>UI-independent planning. Writes are allowed only through an atomic transaction.</summary>
    internal static class RegistryMruCleanup
    {
        internal sealed record Plan(string[] DeleteNames, byte[] Order);

        // No basename/parent-name fallback. Unrecognised PIDLs are left untouched.
        internal static bool ContainsExactPath(byte[] data, string fullPath)
        {
            if (data.Length > 1024 * 1024 || string.IsNullOrEmpty(fullPath)) return false;
            foreach (var value in ExtractStrings(data))
                if (string.Equals(value, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static Plan? BuildPlan(byte[]? order, IReadOnlyDictionary<string, byte[]> values,
            Func<byte[], bool> matches)
        {
            if (order == null || order.Length < 4 || order.Length > 65536 || order.Length % 4 != 0) return null;
            var indexes = new List<int>();
            var seen = new HashSet<int>();
            for (int i = 0; i < order.Length; i += 4)
            {
                int index = BitConverter.ToInt32(order, i);
                if (index == -1)
                {
                    if (i != order.Length - 4) return null; // no trailing garbage
                    var remove = new List<string>();
                    var keep = new List<byte>(order.Length);
                    foreach (int item in indexes)
                    {
                        string name = item.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (!values.TryGetValue(name, out var bytes)) return null; // existing dangling/type-invalid reference
                        if (matches(bytes)) remove.Add(name);
                        else keep.AddRange(BitConverter.GetBytes(item));
                    }
                    if (remove.Count == 0) return null;
                    keep.AddRange(BitConverter.GetBytes(-1));
                    return new Plan(remove.ToArray(), keep.ToArray());
                }
                if (index < 0 || !seen.Add(index)) return null;
                indexes.Add(index);
            }
            return null; // missing terminator: do not delete anything
        }

        internal static int Clean(Func<IRegistryMruTransaction> begin, IEnumerable<string> keyPaths,
            Func<byte[], bool> matches)
        {
            using var transaction = begin(); // fails closed if Windows TxR is unavailable
            int removed = 0;
            foreach (string path in keyPaths)
            {
                using var key = transaction.OpenCurrentUser(path);
                if (key == null) continue;
                removed += CleanKey(key, matches);
                foreach (string childName in key.SubKeyNames())
                {
                    // Each child is explicitly associated with the SAME transaction.
                    using var child = transaction.OpenCurrentUser(path + "\\" + childName);
                    if (child != null) removed += CleanKey(child, matches);
                }
            }
            if (removed > 0) transaction.Commit();
            return removed; // count published only after a successful commit
        }

        private static int CleanKey(IRegistryMruKey key, Func<byte[], bool> matches)
        {
            byte[]? order = key.ReadBinary("MRUListEx");
            if (order == null) return 0;
            var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in key.ValueNames())
                if (name != "MRUListEx" && key.ReadBinary(name) is { } bytes) values[name] = bytes;
            var plan = BuildPlan(order, values, matches);
            if (plan == null) return 0;
            // No catch-and-continue: any error aborts the entire transaction.
            foreach (string name in plan.DeleteNames) key.DeleteValue(name);
            key.WriteOrder(plan.Order);
            return plan.DeleteNames.Length;
        }

        private static IEnumerable<string> ExtractStrings(byte[] data)
        {
            for (int align = 0; align <= 1; align++)
            {
                var text = new StringBuilder();
                for (int i = align; i + 1 < data.Length; i += 2)
                {
                    char c = (char)(data[i] | (data[i + 1] << 8));
                    if (c >= ' ' && c != '\uFFFD') text.Append(c);
                    else { if (text.Length >= 3) yield return text.ToString(); text.Clear(); }
                }
                if (text.Length >= 3) yield return text.ToString();
            }
            var ascii = new StringBuilder();
            foreach (byte b in data)
            {
                if (b >= 0x20 && b < 0x7F) ascii.Append((char)b);
                else { if (ascii.Length >= 3) yield return ascii.ToString(); ascii.Clear(); }
            }
            if (ascii.Length >= 3) yield return ascii.ToString();
        }
    }
}
