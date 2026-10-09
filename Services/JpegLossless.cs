using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoMusicViewer.Services
{
    /// <summary>jpegtran transforms DCT coefficients, never decompresses/recompresses pixels.
    /// -perfect rejects incomplete MCU transforms: no trimming, no silent fallback.</summary>
    internal static class JpegLossless
    {
        /// <summary>Заголовок JPEG. McuWidth/McuHeight — размер iMCU в пикселях хранимого (не повёрнутого) снимка:
        /// по этой сетке jpegtran может резать без перекодирования.</summary>
        internal readonly record struct Header(int Width, int Height, int Orientation, int McuWidth = 8, int McuHeight = 8, int Components = 3);
        internal static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif";
        internal static int Compose(int orientation, int degrees)
        {
            return new ImageViewTransformState(degrees).ComposeOrientation(orientation);
        }
        // Streaming header parser. Metadata payloads never exceed JPEG's 64KiB marker limit.
        internal static Header ReadHeader(Stream source)
        {
            if (source.ReadByte() != 255 || source.ReadByte() != 216) throw new InvalidDataException("Not a JPEG.");
            int width = 0, height = 0, orientation = 1, segments = 0, exifSegments = 0, mcuW = 8, mcuH = 8, components = 3;
            while (++segments <= 4096)
            {
                var (marker, payload) = ReadSegment(source);
                if (marker == 0xDA || marker == 0xD9) break;
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    if (payload.Length < 6) throw new InvalidDataException("Invalid JPEG frame.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(1));
                    width = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(3));
                    if (payload[0] != 8) throw new NotSupportedException("Only 8-bit JPEG is supported by the lossless helper.");
                    components = payload[5];
                    if (components < 1 || payload.Length < 6 + components * 3) throw new InvalidDataException("Invalid JPEG components.");
                    int maxH = 1, maxV = 1;
                    for (int c = 0; c < components; c++)
                    {
                        int sampling = payload[7 + c * 3];
                        maxH = Math.Max(maxH, sampling >> 4); maxV = Math.Max(maxV, sampling & 15);
                    }
                    // Один компонент (оттенки серого) кодируется блоками 8×8 независимо от множителей.
                    if (components == 1) { maxH = 1; maxV = 1; }
                    if (maxH is < 1 or > 4 || maxV is < 1 or > 4) throw new InvalidDataException("Invalid JPEG sampling factors.");
                    mcuW = 8 * maxH; mcuH = 8 * maxV;
                }
                if (marker == 0xE1 && IsExif(payload))
                {
                    if (++exifSegments > 1) throw new InvalidDataException("Multiple EXIF segments are ambiguous; lossless transform refused.");
                    orientation = VisitExif(payload, null, null);
                }
            }
            // jpegtran работает с DCT-коэффициентами в отдельном процессе, пиксели в память
            // приложения не попадают: проверяется только правдоподобие заголовка.
            ImageSafetyPolicy.ValidateSourceDimensions(width, height);
            return new Header(width, height, orientation, mcuW, mcuH, components);
        }
        private static (int Marker, byte[] Payload) ReadSegment(Stream source)
        {
            if (source.ReadByte() != 255) throw new InvalidDataException("Invalid JPEG marker.");
            int marker;
            do { marker = source.ReadByte(); } while (marker == 255);
            if (marker < 0 || marker == 0) throw new InvalidDataException("Truncated JPEG header.");
            if (marker is 0xD9 or 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 1) return (marker, Array.Empty<byte>());
            int hi = source.ReadByte(), lo = source.ReadByte();
            if (hi < 0 || lo < 0 || ((hi << 8) | lo) < 2) throw new InvalidDataException("Invalid JPEG segment length.");
            byte[] data = new byte[((hi << 8) | lo) - 2]; source.ReadExactly(data); return (marker, data);
        }
        private static bool IsMpf(byte[] p) => p.Length >= 4 && p[0] == 77 && p[1] == 80 && p[2] == 70 && p[3] == 0;
        private static bool IsExif(byte[] p) => p.Length >= 14 && p[0] == 69 && p[1] == 120 && p[2] == 105 && p[3] == 102 && p[4] == 0 && p[5] == 0;
        private static int VisitExif(byte[] p, int? width, int? height, bool normalizeOrientation = true)
        {
            // Invalid EXIF is rejected for the preserve-metadata mode, rather than silently dropped.
            bool little = p[6] == 73 && p[7] == 73;
            if (!little && !(p[6] == 77 && p[7] == 77)) throw new InvalidDataException("Invalid EXIF byte order.");
            ushort U16(int at) { if (at < 6 || at > p.Length - 2) throw new InvalidDataException("Invalid EXIF offset."); return little ? BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(at)); }
            uint U32(int at) { if (at < 6 || at > p.Length - 4) throw new InvalidDataException("Invalid EXIF offset."); return little ? BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at)); }
            void Put16(int at, ushort value) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(at), value); else BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(at), value); }
            void Put32(int at, uint value) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(at), value); else BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(at), value); }
            if (U16(8) != 42) throw new InvalidDataException("Invalid EXIF TIFF header.");
            int orientation = 1;
            int Visit(uint offset, bool main)
            {
                int start = checked(6 + (int)offset); int count = U16(start);
                if (count > 4096 || start + 2L + count * 12L + 4L > p.Length) throw new InvalidDataException("Invalid EXIF directory.");
                uint exifOffset = 0;
                for (int i = 0; i < count; i++)
                {
                    int at = start + 2 + i * 12; int tag = U16(at), type = U16(at + 2); uint n = U32(at + 4);
                    if (main && tag == 274)
                    {
                        if (type != 3 || n != 1) throw new InvalidDataException("Invalid EXIF orientation.");
                        orientation = U16(at + 8);
                        if (orientation is < 1 or > 8) throw new InvalidDataException("Invalid EXIF orientation value.");
                        if (width.HasValue && normalizeOrientation) Put16(at + 8, 1);
                    }
                    if (main && tag == 34665 && type == 4 && n == 1) exifOffset = U32(at + 8);
                    if (width.HasValue && n == 1 && tag is 256 or 257 or 40962 or 40963)
                    {
                        uint val = (uint)(tag is 256 or 40962 ? width.Value : height!.Value);
                        if (type == 3) Put16(at + 8, checked((ushort)val)); else if (type == 4) Put32(at + 8, val);
                    }
                }
                if (width.HasValue && main) Put32(start + 2 + count * 12, 0); // disconnect stale IFD1 thumbnail
                if (main && exifOffset != 0) Visit(exifOffset, false);
                return orientation;
            }
            return Visit(U32(10), true);
        }
        /// <summary>Публичная обёртка над разбором EXIF: размеры, ориентация 1 (или прежняя), отключённая миниатюра.</summary>
        internal static void PatchExif(byte[] payload, int width, int height, bool normalizeOrientation) =>
            VisitExif(payload, width, height, normalizeOrientation);

        internal static bool IsExifPayload(byte[] payload) => IsExif(payload);

        internal static (int Marker, byte[] Payload) ReadMarkerSegment(Stream source) => ReadSegment(source);

        internal static void CopyNormalized(Stream source, Stream output, int width, int height, CancellationToken token) =>
            CopyNormalized(source, output, width, height, token, keepOrientation: false);

        /// <summary>
        /// keepOrientation — для обрезки без перекодирования: пиксели не поворачивались,
        /// поэтому тег ориентации и XMP остаются верными и сохраняются (рейтинги, ключевые слова).
        /// </summary>
        internal static void CopyNormalized(Stream source, Stream output, int width, int height, CancellationToken token, bool keepOrientation)
        {
            if (source.ReadByte() != 255 || source.ReadByte() != 216) throw new InvalidDataException("Invalid helper output.");
            output.Write(new byte[] { 255, 216 });
            for (int i = 0; i < 4096; i++)
            {
                token.ThrowIfCancellationRequested(); var (marker, data) = ReadSegment(source);
                if (marker == 0xE1 && IsExif(data)) VisitExif(data, width, height, normalizeOrientation: !keepOrientation);
                // Adobe XMP can contain a second stale orientation/thumbnail; remove it explicitly.
                if (marker == 0xE1 && !IsExif(data) && !keepOrientation) continue;
                // MPF (Ultra HDR, панорамы) указывает на картинки после конца JPEG; jpegtran их
                // не переносит, поэтому таблица MPF стала бы ссылкой в никуда.
                if (marker == 0xE2 && IsMpf(data)) continue;
                output.WriteByte(255); output.WriteByte((byte)marker);
                if (data.Length != 0 || marker is not (0xD9 or 0xD8 or 1) && marker is not (>= 0xD0 and <= 0xD7))
                { int len = data.Length + 2; output.WriteByte((byte)(len >> 8)); output.WriteByte((byte)len); output.Write(data); }
                if (marker == 0xDA) { CopyBounded(source, output, token, ImageSafetyPolicy.MaxFileBytes); return; }
                if (marker == 0xD9) return;
            }
            throw new InvalidDataException("Too many JPEG markers.");
        }
        private static void Verify(string file, string expected)
        {
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bundled JPEG tool checksum mismatch. Reinstall the original tools folder.");
        }
        internal static void Transform(string path, Stream destination, int degrees, bool strip, CancellationToken token, string? testHelper = null) =>
            Transform(path, destination, new ImageViewTransformState(degrees), strip, token, testHelper);

        internal static void Transform(string path, Stream destination, ImageViewTransformState viewTransform, bool strip, CancellationToken token, string? testHelper = null)
        {
            using var lockedSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ImageSafetyPolicy.ValidateFileLength(lockedSource.Length);
            Header h = ReadHeader(lockedSource); int transform = viewTransform.ComposeOrientation(h.Orientation);
            var args = new List<string> { "-copy", strip ? "none" : "all", "-perfect", "-maxmemory", "2097152k" };
            string[] options = transform switch
            {
                2 => new[] { "-flip", "horizontal" }, 3 => new[] { "-rotate", "180" }, 4 => new[] { "-flip", "vertical" },
                5 => new[] { "-transpose" }, 6 => new[] { "-rotate", "90" }, 7 => new[] { "-transverse" }, 8 => new[] { "-rotate", "270" }, _ => Array.Empty<string>()
            };
            args.AddRange(options);
            RunHelper(path, args, token, testHelper, (spool, result) => CopyNormalized(spool, destination, result.Width, result.Height, token));
        }

        /// <summary>Есть ли комплектный jpegtran для этой платформы (только Windows x64).</summary>
        internal static bool HelperAvailable(string? testHelper = null)
        {
            if (testHelper != null) return File.Exists(testHelper);
            try
            {
                return OperatingSystem.IsWindows() &&
                       System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
                       File.Exists(DefaultHelperPath) && File.Exists(Path.Combine(Path.GetDirectoryName(DefaultHelperPath)!, "libjpeg-62.dll"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private static string DefaultHelperPath => Path.Combine(AppContext.BaseDirectory, "tools", "jpegtran", "jpegtran.exe");

        // ---------------- Обрезка без перекодирования ----------------

        /// <summary>Прямоугольник в пикселях (x, y, ширина, высота).</summary>
        internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
        {
            public int Right => X + Width;
            public int Bottom => Y + Height;
        }

        /// <summary>
        /// План обрезки без потерь. Display — что получится на экране (может быть чуть больше
        /// выбранного: левый и верхний край сдвигаются к сетке JPEG); Raw — тот же участок в
        /// координатах хранимого снимка, который передаётся jpegtran.
        /// </summary>
        internal readonly record struct LosslessCropPlan(PixelRect Requested, PixelRect Display, PixelRect Raw, Header Header)
        {
            public bool Exact => Requested == Display;
            public int ExtraLeft => Requested.X - Display.X;
            public int ExtraTop => Requested.Y - Display.Y;
            public int ExtraRight => Display.Right - Requested.Right;
            public int ExtraBottom => Display.Bottom - Requested.Bottom;
        }

        /// <summary>Размер снимка на экране (после EXIF-ориентации).</summary>
        internal static (int Width, int Height) DisplaySize(Header h) =>
            h.Orientation is 5 or 6 or 7 or 8 ? (h.Height, h.Width) : (h.Width, h.Height);

        /// <summary>Точка экрана → пиксель хранимого снимка (W, H — размер хранимого снимка).</summary>
        internal static (int X, int Y) DisplayToRaw(int dx, int dy, int w, int h, int orientation) => orientation switch
        {
            2 => (w - 1 - dx, dy),
            3 => (w - 1 - dx, h - 1 - dy),
            4 => (dx, h - 1 - dy),
            5 => (dy, dx),
            6 => (dy, h - 1 - dx),
            7 => (w - 1 - dy, h - 1 - dx),
            8 => (w - 1 - dy, dx),
            _ => (dx, dy)
        };

        /// <summary>Пиксель хранимого снимка → точка экрана (обратное к DisplayToRaw).</summary>
        internal static (int X, int Y) RawToDisplay(int rx, int ry, int w, int h, int orientation) => orientation switch
        {
            2 => (w - 1 - rx, ry),
            3 => (w - 1 - rx, h - 1 - ry),
            4 => (rx, h - 1 - ry),
            5 => (ry, rx),
            6 => (h - 1 - ry, rx),
            7 => (h - 1 - ry, w - 1 - rx),
            8 => (ry, w - 1 - rx),
            _ => (rx, ry)
        };

        private static PixelRect MapRect(PixelRect r, Func<int, int, (int X, int Y)> map)
        {
            var a = map(r.X, r.Y); var b = map(r.Right - 1, r.Bottom - 1);
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
            return new PixelRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        /// <summary>
        /// jpegtran режет только по сетке iMCU: левый и верхний край хранимого снимка
        /// округляются вниз до кратного размера блока, правый и нижний — точные.
        /// </summary>
        internal static LosslessCropPlan PlanCrop(Header h, PixelRect requested)
        {
            var (dw, dh) = DisplaySize(h);
            int x = Math.Clamp(requested.X, 0, dw - 1), y = Math.Clamp(requested.Y, 0, dh - 1);
            var req = new PixelRect(x, y, Math.Clamp(requested.Width, 1, dw - x), Math.Clamp(requested.Height, 1, dh - y));
            var raw = MapRect(req, (px, py) => DisplayToRaw(px, py, h.Width, h.Height, h.Orientation));
            int ax = raw.X / h.McuWidth * h.McuWidth, ay = raw.Y / h.McuHeight * h.McuHeight;
            var aligned = new PixelRect(ax, ay, raw.Right - ax, raw.Bottom - ay);
            var display = MapRect(aligned, (px, py) => RawToDisplay(px, py, h.Width, h.Height, h.Orientation));
            return new LosslessCropPlan(req, display, aligned, h);
        }

        internal static LosslessCropPlan PlanCrop(string path, PixelRect requested)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ImageSafetyPolicy.ValidateFileLength(stream.Length);
            return PlanCrop(ReadHeader(stream), requested);
        }

        /// <summary>
        /// Обрезка JPEG без перекодирования: jpegtran вырезает DCT-блоки, пиксели не
        /// пересжимаются. Ориентация снимка не меняется (тег EXIF сохраняется), поэтому
        /// сохраняются и EXIF/GPS/камера/ICC, и XMP; обновляются размеры, устаревшая
        /// миниатюра EXIF отключается.
        /// </summary>
        internal static LosslessCropPlan Crop(string path, Stream destination, PixelRect requested, CancellationToken token, string? testHelper = null)
        {
            using var lockedSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ImageSafetyPolicy.ValidateFileLength(lockedSource.Length);
            var plan = PlanCrop(ReadHeader(lockedSource), requested);
            var r = plan.Raw;
            var args = new List<string> { "-copy", "all", "-maxmemory", "2097152k",
                "-crop", FormattableString.Invariant($"{r.Width}x{r.Height}+{r.X}+{r.Y}") };
            RunHelper(path, args, token, testHelper, (spool, result) =>
            {
                if (result.Width != r.Width || result.Height != r.Height)
                    throw new InvalidDataException(FormattableString.Invariant(
                        $"Lossless crop produced {result.Width}x{result.Height}, expected {r.Width}x{r.Height}. Nothing was saved."));
                CopyNormalized(spool, destination, result.Width, result.Height, token, keepOrientation: true);
            });
            return plan;
        }

        private static void RunHelper(string path, IEnumerable<string> arguments, CancellationToken token, string? testHelper, Action<Stream, Header> finish)
        {
            string exe = testHelper ?? DefaultHelperPath;
            if (testHelper == null)
            {
                if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                    throw new NotSupportedException("Lossless JPEG helper requires Windows x64.");
                Verify(exe, "a90d8c20128fcb6a762f704fd011f22159a053a372c9e71f603d88b7c93b084d");
                Verify(Path.Combine(Path.GetDirectoryName(exe)!, "libjpeg-62.dll"), "3493ce5a6b0615b44ad8475ee060bd5c965069809a9dc2453b6503af55b9c65e");
            }
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in arguments) info.ArgumentList.Add(a);
            info.ArgumentList.Add(Path.GetFullPath(path));
            token.ThrowIfCancellationRequested();
            using var process = Process.Start(info) ?? throw new IOException("Cannot start JPEG helper.");
            using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var stderr = Task.Run(() => { char[] buffer = new char[1024]; string text = ""; int n; while ((n = process.StandardError.Read(buffer)) > 0) { if (text.Length < 8192) text += new string(buffer, 0, Math.Min(n, 8192 - text.Length)); } return text; });
            // DeleteOnClose output spool bounds RAM and keeps partially failed transforms out of destination.
            string temp = Path.Combine(Path.GetTempPath(), "pmv-jpeg-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using var spool = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                try { CopyBounded(process.StandardOutput.BaseStream, spool, token, ImageSafetyPolicy.MaxFileBytes); process.WaitForExit(); }
                catch { try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } process.WaitForExit(); token.ThrowIfCancellationRequested(); throw; }
                string error = stderr.GetAwaiter().GetResult(); token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("Lossless JPEG transform failed (no trimming or lossy fallback): " + error);
                spool.Position = 0; Header result = ReadHeader(spool); spool.Position = 0;
                finish(spool, result);
            }
            finally { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        }
        private static void CopyBounded(Stream source, Stream destination, CancellationToken token, long max)
        {
            byte[] buffer = new byte[65536]; long total = 0; int n;
            while ((n = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); total = checked(total + n); if (total > max) throw new IOException("JPEG output exceeds safe file limit."); destination.Write(buffer, 0, n); }
        }
    }
}
