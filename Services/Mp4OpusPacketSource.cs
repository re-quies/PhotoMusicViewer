using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Concentus;
using Concentus.Structs;

namespace PhotoMusicViewer.Services
{
    /// <summary>Потоковый демультиплексор обычного (не fragmented) ISO BMFF/MP4 Opus.
    /// Mono/stereo, mapping family 0. Пакеты читаются из mdat без конвертации файла.</summary>
    internal sealed class Mp4OpusPacketSource : IOpusPacketSource
    {
        private readonly FileStream _stream;
        private readonly Mp4OpusIndex _index;
        private IOpusDecoder _decoder = ManagedOpusDecoderFactory.CreateStereo48k();
        private int _packet;
        private long _skipSamples, _returnedSamples;
        private readonly double _gain;
        public long PcmLength { get; }
        public bool CanSeek => true;
        internal Mp4OpusPacketSource(string path)
        {
            // Не допускаем обычной перезаписи/замены источника на Windows во время чтения.
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                _index = Mp4OpusIndex.Read(_stream);
                PcmLength = checked(_index.OutputSamples * 4);
                _skipSamples = _index.StartSample;
                _gain = Math.Pow(10, _index.Gain / (256.0 * 20));
            }
            catch { _decoder.Dispose(); _stream.Dispose(); throw; }
        }
        public byte[]? ReadPacket()
        {
            while (_returnedSamples < _index.OutputSamples)
            {
                if (_packet >= _index.Sizes.Length) throw new EndOfStreamException("Truncated MP4 Opus timeline.");
                int i = _packet++;
                _stream.Position = _index.Offsets[i];
                byte[] packet = new byte[_index.Sizes[i]];
                _stream.ReadExactly(packet);
                int frames = OpusPacketInfo.GetNumSamples(packet.AsSpan(), 48000);
                int duration = _index.Durations[i];
                if (frames <= 0 || frames > 5760 || duration > frames ||
                    (duration != frames && i != _index.Sizes.Length - 1))
                    throw new InvalidDataException("MP4 timing does not match the Opus packet duration.");
                short[] samples = new short[frames * 2];
                if (_decoder.Decode(packet.AsSpan(), samples.AsSpan(), frames, false) != frames)
                    throw new InvalidDataException("Unexpected MP4 Opus decode length.");
                int skip = (int)Math.Min(duration, _skipSamples);
                _skipSamples -= skip;
                int take = (int)Math.Min(duration - skip, _index.OutputSamples - _returnedSamples);
                if (take == 0) continue;
                if (_gain != 1)
                    for (int j = skip * 2; j < (skip + take) * 2; j++)
                        samples[j] = (short)Math.Clamp(Math.Round(samples[j] * _gain), short.MinValue, short.MaxValue);
                byte[] pcm = new byte[take * 4];
                Buffer.BlockCopy(samples, skip * 4, pcm, 0, pcm.Length);
                _returnedSamples += take;
                return pcm;
            }
            return null;
        }
        public void Seek(long pcmBytePosition)
        {
            if (pcmBytePosition < 0 || pcmBytePosition % 4 != 0) throw new ArgumentOutOfRangeException(nameof(pcmBytePosition));
            _returnedSamples = Math.Min(pcmBytePosition / 4, _index.OutputSamples);
            long target = checked(_index.StartSample + _returnedSamples);
            // RFC minimum is 80 ms. Use 200 ms for tighter convergence on short packets.
            long warmup = Math.Max(0, target - Math.Max(OpusPacketSource.PreRollSamples, 9600));
            int found = Array.BinarySearch(_index.Starts, warmup);
            _packet = found >= 0 ? found : Math.Max(0, ~found - 1);
            _skipSamples = target - _index.Starts[_packet];
            _decoder.ResetState();
        }
        public void Dispose()
        {
            try { _stream.Dispose(); }
            finally { _decoder.Dispose(); }
        }
    }

    /// <summary>Строго ограниченный парсер таблиц MP4. Ссылки за mdat, внешние данные,
    /// шифрование, fragmented MP4 и сложные монтажные списки не поддерживаются.</summary>
    internal sealed class Mp4OpusIndex
    {
        internal const int MaxSamples = 2_000_000;
        internal const int MaxBoxes = 100_000;
        internal const int MaxPacketSize = 64 * 1024;
        internal const long MaxMoovBytes = 64L * 1024 * 1024;
        internal long[] Offsets { get; private set; } = Array.Empty<long>();
        internal int[] Sizes { get; private set; } = Array.Empty<int>();
        internal int[] Durations { get; private set; } = Array.Empty<int>();
        internal long[] Starts { get; private set; } = Array.Empty<long>();
        internal long StartSample { get; private set; }
        internal long OutputSamples { get; private set; }
        internal short Gain { get; private set; }
        private readonly record struct Box(string Type, long Data, long End)
        {
            internal long Length => End - Data;
        }
        private sealed class Parser
        {
            internal readonly Stream Stream;
            private int _boxes;
            internal Parser(Stream stream) { Stream = stream; }
            internal List<Box> Boxes(long begin, long end)
            {
                var result = new List<Box>();
                if (begin < 0 || end > Stream.Length || begin > end) Bad("Invalid MP4 box range.");
                Span<byte> header = stackalloc byte[16];
                while (begin < end)
                {
                    if (++_boxes > MaxBoxes) Bad("Too many MP4 boxes.");
                    if (end - begin < 8) Bad("Truncated MP4 box.");
                    Stream.Position = begin; Stream.ReadExactly(header[..8]);
                    ulong size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                    string type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
                    int bytes = 8;
                    if (size == 1)
                    {
                        if (end - begin < 16) Bad("Truncated extended MP4 box.");
                        Stream.ReadExactly(header.Slice(8, 8));
                        size = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8)); bytes = 16;
                    }
                    if (size == 0) size = (ulong)(end - begin);
                    if (size < (ulong)bytes || size > (ulong)(end - begin)) Bad("Invalid MP4 box size.");
                    long next = begin + (long)size;
                    result.Add(new Box(type, begin + bytes, next)); begin = next;
                }
                return result;
            }
            internal List<Box> Children(Box box) => Boxes(box.Data, box.End);
            internal byte[] Bytes(Box box, int max)
            {
                if (box.Length < 0 || box.Length > max) Bad("MP4 metadata exceeds safety limit.");
                byte[] data = new byte[(int)box.Length]; Stream.Position = box.Data; Stream.ReadExactly(data); return data;
            }
        }
        private static void Bad(string message) => throw new InvalidDataException(message);
        private static NotSupportedException Unsupported(string detail) => new(Loc.T(
            "Unsupported MP4/Opus variant: ", "Неподдерживаемый вариант MP4/Opus: ", "Variante MP4/Opus no compatible: ") + detail);
        private static Box One(List<Box> boxes, string type)
        {
            var found = boxes.FindAll(x => x.Type == type);
            if (found.Count != 1) Bad("Missing or duplicate MP4 box: " + type);
            return found[0];
        }
        private static Box? Optional(List<Box> boxes, string type)
        {
            var found = boxes.FindAll(x => x.Type == type);
            if (found.Count > 1) Bad("Duplicate MP4 box: " + type);
            return found.Count == 0 ? null : found[0];
        }
        private static uint U32(byte[] b, int at)
        {
            if (at < 0 || at > b.Length - 4) { Bad("Truncated MP4 metadata."); }
            return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4));
        }
        private static ulong U64(byte[] b, int at)
        {
            if (at < 0 || at > b.Length - 8) Bad("Truncated MP4 metadata.");
            return BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at, 8));
        }
        private static void FullBox(byte[] bytes, byte version = 0)
        {
            if (bytes.Length < 4 || bytes[0] != version || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
                Bad("Unsupported MP4 full-box version or flags.");
        }
        private static (uint Scale, ulong Duration) TimeHeader(Parser p, Box box)
        {
            byte[] b = p.Bytes(box, 256);
            if (b.Length < 4) Bad("Truncated MP4 time header.");
            uint scale; ulong duration;
            if (b[0] == 0) { FullBox(b); scale = U32(b, 12); duration = U32(b, 16); }
            else if (b[0] == 1) { FullBox(b, 1); scale = U32(b, 20); duration = U64(b, 24); }
            else throw Unsupported("time header version");
            if (scale == 0 || duration > long.MaxValue) Bad("Invalid MP4 timescale/duration.");
            return (scale, duration);
        }
        private static List<Box> AudioTracks(Parser p, Box moov)
        {
            var tracks = new List<Box>();
            foreach (Box trak in p.Children(moov).FindAll(x => x.Type == "trak"))
            {
                Box mdia = One(p.Children(trak), "mdia");
                byte[] h = p.Bytes(One(p.Children(mdia), "hdlr"), 4096);
                if (h.Length < 12) Bad("Truncated MP4 handler.");
                if (h.AsSpan(8, 4).SequenceEqual("soun"u8)) tracks.Add(trak);
            }
            return tracks;
        }
        private static List<Box> SampleEntries(Parser p, Box trak)
        {
            Box mdia = One(p.Children(trak), "mdia");
            Box minf = One(p.Children(mdia), "minf");
            Box stbl = One(p.Children(minf), "stbl");
            Box stsd = One(p.Children(stbl), "stsd");
            if (stsd.Length < 8 || stsd.Length > MaxMoovBytes) Bad("Invalid MP4 sample description.");
            byte[] prefix = new byte[8]; p.Stream.Position = stsd.Data; p.Stream.ReadExactly(prefix); FullBox(prefix);
            uint count = U32(prefix, 4);
            if (count == 0 || count > 256) Bad("Invalid MP4 sample description count.");
            var entries = p.Boxes(stsd.Data + 8, stsd.End);
            if (entries.Count != count) Bad("Inconsistent MP4 sample descriptions.");
            return entries;
        }
        internal static AudioCodec ProbeCodec(Stream stream)
        {
            var p = new Parser(stream);
            var top = p.Boxes(0, stream.Length);
            Box ftyp = One(top, "ftyp");
            if (ftyp.Length < 8 || ftyp.Length % 4 != 0) Bad("Invalid MP4 file-type box.");
            Box moov = One(top, "moov");
            if (moov.Length > MaxMoovBytes) Bad("MP4 moov exceeds safety limit.");
            var tracks = AudioTracks(p, moov);
            AudioCodec codec = AudioCodec.Unknown;
            foreach (Box track in tracks)
                foreach (Box entry in SampleEntries(p, track))
                {
                    if (entry.Type == "Opus") return AudioCodec.Opus;
                    if (entry.Type == "alac") codec = AudioCodec.Alac;
                    if (entry.Type == "mp4a" && IsAac(p, entry)) codec = AudioCodec.Aac;
                }
            return codec;
        }
        private static bool IsAac(Parser p, Box entry)
        {
            if (entry.Length < 28) Bad("Truncated MPEG-4 audio entry.");
            byte[] prefix = new byte[28]; p.Stream.Position = entry.Data; p.Stream.ReadExactly(prefix);
            // Other QuickTime sample-entry versions remain system-decoder territory.
            if (BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(8, 2)) != 0) return false;
            Box? esds = Optional(p.Boxes(entry.Data + 28, entry.End), "esds");
            if (!esds.HasValue) return false;
            byte[] b = p.Bytes(esds.Value, 64 * 1024); FullBox(b);
            int at = 4;
            (int Tag, int End) Descriptor(int limit)
            {
                if (at >= limit) { Bad("Missing MPEG-4 descriptor."); }
                int tag = b[at++], length = 0; bool terminated = false;
                for (int i = 0; i < 4; i++)
                {
                    if (at >= limit) Bad("Truncated MPEG-4 descriptor length.");
                    byte value = b[at++]; length = checked((length << 7) | (value & 127));
                    if ((value & 128) == 0) { terminated = true; break; }
                }
                if (!terminated || length > limit - at) Bad("Invalid MPEG-4 descriptor length.");
                return (tag, at + length);
            }
            var es = Descriptor(b.Length);
            if (es.Tag != 3 || es.End - at < 3) Bad("Invalid ES descriptor.");
            at += 2; byte flags = b[at++];
            if ((flags & 128) != 0) at += 2;
            if ((flags & 64) != 0)
            {
                if (at >= es.End) Bad("Truncated ES URL.");
                int length = b[at++]; at += length;
            }
            if ((flags & 32) != 0) at += 2;
            if (at > es.End) Bad("Invalid ES descriptor flags.");
            var decoder = Descriptor(es.End);
            if (decoder.Tag != 4 || decoder.End - at < 13) Bad("Invalid decoder descriptor.");
            byte objectType = b[at], streamType = b[at + 1];
            return (streamType >> 2) == 5 && (objectType == 0x40 || objectType is 0x66 or 0x67 or 0x68);
        }
        internal static Mp4OpusIndex Read(Stream stream)
        {
            var p = new Parser(stream);
            var top = p.Boxes(0, stream.Length);
            Box moov = One(top, "moov");
            if (moov.Length > MaxMoovBytes) Bad("MP4 moov exceeds safety limit.");
            var moovChildren = p.Children(moov);
            if (Optional(moovChildren, "mvex") != null || top.Exists(x => x.Type == "moof")) throw Unsupported("fragmented MP4");
            var tracks = AudioTracks(p, moov);
            if (tracks.Count != 1) throw Unsupported("requires exactly one audio track");
            Box trak = tracks[0];
            var entries = SampleEntries(p, trak);
            if (entries.Count != 1 || entries[0].Type != "Opus") throw Unsupported("sample description switching / non-Opus audio");
            Box entry = entries[0];
            if (entry.Length < 28) Bad("Truncated Opus sample entry.");
            byte[] entryHeader = new byte[28]; stream.Position = entry.Data; stream.ReadExactly(entryHeader);
            if (BinaryPrimitives.ReadUInt16BigEndian(entryHeader.AsSpan(8, 2)) != 0) throw Unsupported("audio sample-entry version");
            int reference = BinaryPrimitives.ReadUInt16BigEndian(entryHeader.AsSpan(6, 2));
            var entryChildren = p.Boxes(entry.Data + 28, entry.End);
            if (Optional(entryChildren, "sinf") != null) throw Unsupported("encrypted audio");
            byte[] ops = p.Bytes(One(entryChildren, "dOps"), 512);
            if (ops.Length != 11 || ops[0] != 0 || (ops[1] != 1 && ops[1] != 2) || ops[10] != 0)
                throw Unsupported("only mono/stereo channel mapping family 0 is supported");
            int preSkip = BinaryPrimitives.ReadUInt16BigEndian(ops.AsSpan(2, 2));
            var index = new Mp4OpusIndex { Gain = BinaryPrimitives.ReadInt16BigEndian(ops.AsSpan(8, 2)) };
            Box mdia = One(p.Children(trak), "mdia");
            var time = TimeHeader(p, One(p.Children(mdia), "mdhd"));
            if (time.Scale != 48000) throw Unsupported("Opus media timescale must be 48000");
            Box minf = One(p.Children(mdia), "minf");
            ValidateDataReference(p, minf, reference);
            Box stbl = One(p.Children(minf), "stbl");
            var tables = p.Children(stbl);
            if (Optional(tables, "senc") != null || Optional(tables, "saiz") != null || Optional(tables, "saio") != null)
                throw Unsupported("encrypted/auxiliary sample data");
            if (Optional(tables, "ctts") != null) throw Unsupported("composition offsets");
            index.Sizes = ReadSizes(p, One(tables, "stsz"));
            index.Durations = ReadDurations(p, One(tables, "stts"), index.Sizes.Length);
            index.Starts = new long[index.Sizes.Length];
            long total = 0;
            for (int i = 0; i < index.Starts.Length; i++) { index.Starts[i] = total; total = checked(total + index.Durations[i]); }
            if ((ulong)total != time.Duration) Bad("MP4 sample durations disagree with media duration.");
            index.StartSample = preSkip;
            long end = total;
            Box? edts = Optional(p.Children(trak), "edts");
            if (edts.HasValue)
            {
                byte[] elst = p.Bytes(One(p.Children(edts.Value), "elst"), 1024);
                if (elst.Length < 4) Bad("Truncated edit list.");
                bool wide = elst[0] == 1;
                FullBox(elst, wide ? (byte)1 : (byte)0);
                if (U32(elst, 4) != 1 || elst.Length != (wide ? 28 : 20)) throw Unsupported("complex/empty edit list");
                ulong movieDuration = wide ? U64(elst, 8) : U32(elst, 8);
                long mediaStart = wide ? unchecked((long)U64(elst, 16)) : unchecked((int)U32(elst, 12));
                int rateAt = wide ? 24 : 16;
                if (mediaStart < preSkip || U32(elst, rateAt) != 0x00010000) throw Unsupported("edit rate, negative time or missing pre-skip trim");
                var movieTime = TimeHeader(p, One(moovChildren, "mvhd"));
                if (movieDuration > (ulong)(long.MaxValue / 48000)) Bad("Edit duration overflow.");
                long presented = (long)(movieDuration * 48000 / movieTime.Scale);
                index.StartSample = mediaStart;
                end = Math.Min(total, checked(mediaStart + presented));
            }
            if (index.StartSample < 0 || index.StartSample >= total || end <= index.StartSample) Bad("Invalid MP4 Opus presentation range.");
            index.OutputSamples = end - index.StartSample;
            index.Offsets = ReadOffsets(p, tables, index.Sizes, top.FindAll(x => x.Type == "mdat"));
            return index;
        }
        private static void ValidateDataReference(Parser p, Box minf, int reference)
        {
            Box dref = One(p.Children(One(p.Children(minf), "dinf")), "dref");
            byte[] prefix = new byte[8];
            if (dref.Length < 8) Bad("Truncated data reference.");
            p.Stream.Position = dref.Data; p.Stream.ReadExactly(prefix); FullBox(prefix);
            var entries = p.Boxes(dref.Data + 8, dref.End);
            if (U32(prefix, 4) != entries.Count || reference < 1 || reference > entries.Count) Bad("Invalid MP4 data reference.");
            Box entry = entries[reference - 1]; byte[] value = p.Bytes(entry, 4096);
            if (entry.Type != "url " || value.Length != 4 || U32(value, 0) != 1) throw Unsupported("external media references");
        }
        private static byte[] Table(Parser p, Box box, int stride, int prefix, out int count)
        {
            byte[] b = p.Bytes(box, checked(prefix + MaxSamples * stride)); FullBox(b);
            uint n = U32(b, prefix - 4);
            if (n == 0 || n > MaxSamples || b.Length != prefix + (long)n * stride) Bad("Invalid MP4 sample table size.");
            count = (int)n; return b;
        }
        private static int[] ReadSizes(Parser p, Box box)
        {
            byte[] b = p.Bytes(box, 12 + MaxSamples * 4); FullBox(b);
            uint uniform = U32(b, 4), n = U32(b, 8);
            if (n == 0 || n > MaxSamples || b.Length != (uniform == 0 ? 12 + (long)n * 4 : 12)) Bad("Invalid MP4 sample sizes.");
            int[] result = new int[(int)n];
            for (int i = 0; i < result.Length; i++)
            {
                uint size = uniform == 0 ? U32(b, 12 + i * 4) : uniform;
                if (size == 0 || size > MaxPacketSize) Bad("Opus packet exceeds safety limit.");
                result[i] = (int)size;
            }
            return result;
        }
        private static int[] ReadDurations(Parser p, Box box, int samples)
        {
            byte[] b = Table(p, box, 8, 8, out int runs);
            int[] result = new int[samples]; int at = 0;
            for (int i = 0; i < runs; i++)
            {
                uint count = U32(b, 8 + i * 8), duration = U32(b, 12 + i * 8);
                if (count == 0 || count > samples - at || duration == 0 || duration > 5760) Bad("Invalid Opus sample durations.");
                Array.Fill(result, (int)duration, at, (int)count); at += (int)count;
            }
            if (at != samples) Bad("MP4 timing/sample count mismatch.");
            return result;
        }
        private static bool InsideMedia(List<Box> media, long begin, long end)
        {
            // Top-level mdat ranges are already ordered. Avoid chunks × mdat scans
            // on hostile files with many boxes: O(log mdat) per chunk.
            int lo = 0, hi = media.Count - 1, found = -1;
            while (lo <= hi)
            {
                int middle = lo + (hi - lo) / 2;
                if (media[middle].Data <= begin) { found = middle; lo = middle + 1; }
                else hi = middle - 1;
            }
            return found >= 0 && end > begin && end <= media[found].End;
        }
        private static long[] ReadOffsets(Parser p, List<Box> tables, int[] sizes, List<Box> media)
        {
            Box? stco = Optional(tables, "stco"), co64 = Optional(tables, "co64");
            if (stco.HasValue == co64.HasValue) Bad("Missing or duplicate MP4 chunk offsets.");
            bool wide = co64.HasValue;
            byte[] offsets = Table(p, wide ? co64!.Value : stco!.Value, wide ? 8 : 4, 8, out int chunks);
            byte[] mapping = Table(p, One(tables, "stsc"), 12, 8, out int runs);
            uint previous = 0;
            for (int i = 0; i < runs; i++)
            {
                uint first = U32(mapping, 8 + i * 12), count = U32(mapping, 12 + i * 12), description = U32(mapping, 16 + i * 12);
                if ((i == 0 && first != 1) || first <= previous || first > chunks || count == 0 || count > sizes.Length || description != 1)
                    Bad("Invalid MP4 chunk mapping.");
                previous = first;
            }
            long[] result = new long[sizes.Length]; int sample = 0, run = 0;
            var occupied = new List<(long Begin, long End)>();
            for (int chunk = 1; chunk <= chunks; chunk++)
            {
                if (run + 1 < runs && U32(mapping, 8 + (run + 1) * 12) == chunk) run++;
                uint count = U32(mapping, 12 + run * 12);
                if (count > sizes.Length - sample) Bad("MP4 chunk/sample count mismatch.");
                ulong value = wide ? U64(offsets, 8 + (chunk - 1) * 8) : U32(offsets, 8 + (chunk - 1) * 4);
                if (value > long.MaxValue) Bad("MP4 chunk offset overflow.");
                long start = (long)value, end = start;
                for (int j = 0; j < count; j++) { result[sample] = end; end = checked(end + sizes[sample++]); }
                if (!InsideMedia(media, start, end)) Bad("MP4 samples point outside mdat.");
                occupied.Add((start, end));
            }
            if (sample != sizes.Length) Bad("MP4 chunk/sample count mismatch.");
            occupied.Sort((a, b) => a.Begin.CompareTo(b.Begin));
            for (int i = 1; i < occupied.Count; i++) if (occupied[i].Begin < occupied[i - 1].End) Bad("Overlapping MP4 audio chunks.");
            return result;
        }
    }
}
