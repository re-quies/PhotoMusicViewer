using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Concentus;
using Concentus.Structs;
using NAudio.Wave;

namespace PhotoMusicViewer.Services
{
    internal interface IOpusPacketSource : IDisposable
    {
        long PcmLength { get; }
        byte[]? ReadPacket();

        /// <summary>Источник умеет переходить к позиции без декодирования с начала.</summary>
        bool CanSeek => false;

        /// <summary>Следующий ReadPacket вернёт PCM ровно с этой позиции (в байтах PCM16 stereo).</summary>
        void Seek(long pcmBytePosition) => throw new NotSupportedException();
    }

    /// <summary>
    /// Потоковый Opus: декодирует только по запросу NAudio. В памяти один пакет.
    /// Скользящий PCM-кэш на диске ограничен 64 МиБ и удаляется при закрытии.
    /// Перемотка внутри кэша — чтение из кэша. Вне кэша (назад дальше 64 МиБ или далеко
    /// вперёд) источник переходит к нужной странице Ogg по индексу, собранному при
    /// открытии, и декодирует разгон перед целевой точкой. Для MP4 — индекс пакетов,
    /// разгон минимум 200 мс. Ogg/MP4 выбирается по содержимому, не по расширению.
    /// Раньше перемотка назад за пределы кэша декодировала запись с самого начала,
    /// что для многочасовых аудиокниг занимало минуты. Полная длительность не урезается.
    /// </summary>
    public class OpusFileReader : WaveStream
    {
        private readonly WaveFormat _waveFormat = new(48000, 16, 2);
        private readonly object _stateLock = new();
        private readonly object _ioLock = new();
        private readonly Func<IOpusPacketSource> _openSource;
        private IOpusPacketSource? _source;
        private readonly FileStream _cache;
        private readonly long _cacheCapacity;
        private readonly long _length;
        private long _cacheStart, _decodedEnd;
        private long _position, _seekVersion, _activeVersion;
        private bool _disposed;
        private Exception? _failure;
        private long _failureReportedVersion = -1;
        internal const long MaxCacheBytes = 64L * 1024 * 1024;
        internal const int MaxPacketBytes = 5760 * 2 * 2; // 120 ms, stereo PCM16
        /// <summary>Вперёд дальше этого (10 с PCM) — переход по индексу, а не декодирование подряд.</summary>
        internal const long ForwardSeekBytes = 10L * 48000 * 4;
        private long _cacheBase;

        /// <summary>Ошибка чтения/декодирования; вызывается на потоке Read.
        /// Также передаётся исключением в NAudio -> PlaybackStopped -> PlaybackError.</summary>
        public event Action<Exception>? DecodeFailed;

        public OpusFileReader(string path) : this(() => OpenPacketSource(path), MaxCacheBytes) { }

        private static IOpusPacketSource OpenPacketSource(string path)
        {
            var format = AudioFormatProbe.Read(path);
            if (format.Codec != AudioCodec.Opus)
                throw new InvalidDataException("The file does not contain a supported Opus stream.");
            return format.Container switch
            {
                AudioContainer.Ogg => new OpusPacketSource(path),
                AudioContainer.Mp4 => new Mp4OpusPacketSource(path),
                _ => throw new NotSupportedException("Unsupported Opus container.")
            };
        }

        internal OpusFileReader(Func<IOpusPacketSource> openSource, long cacheCapacity)
        {
            ArgumentNullException.ThrowIfNull(openSource);
            if (cacheCapacity < 4 || cacheCapacity > MaxCacheBytes || cacheCapacity % 4 != 0)
                throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
            _openSource = openSource;
            _cacheCapacity = cacheCapacity;
            _source = openSource();
            try
            {
                _length = _source.PcmLength;
                if (_length < 0 || _length % 4 != 0) throw new InvalidDataException("Invalid Opus PCM length.");
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PhotoMusicViewer", "OpusCache");
                Directory.CreateDirectory(directory);
                _cache = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pcm"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
                    FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            }
            catch { _source.Dispose(); throw; }
        }

        public override WaveFormat WaveFormat => _waveFormat;
        public override long Length => _length;
        public override long Position
        {
            get { lock (_stateLock) return _position; }
            set
            {
                lock (_stateLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    long clamped = Math.Clamp(value, 0, _length);
                    _position = clamped - clamped % WaveFormat.BlockAlign;
                    ++_seekVersion;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            count -= count % WaveFormat.BlockAlign;
            if (count == 0) return 0;
            lock (_ioLock)
            {
                int copied = 0;
                while (copied < count)
                {
                    long position, version;
                    lock (_stateLock)
                    {
                        if (_disposed) return copied;
                        position = _position;
                        version = _seekVersion;
                    }
                    if (position >= _length) return copied;
                    try
                    {
                        if (version != _activeVersion)
                        {
                            copied = 0; // Не смешиваем результаты разных запросов перемотки.
                            bool retryFailure = _failure != null;
                            _activeVersion = version;
                            _failure = null;
                            if (retryFailure) ResetSource(position);
                            else if (position < _cacheStart || position > _decodedEnd + ForwardSeekBytes) Reposition(position);
                        }
                        if (_failure != null) throw new IOException("Opus decoding previously failed.", _failure);
                        if (position < _cacheStart) Reposition(position);
                        if (position >= _decodedEnd)
                        {
                            // Декодирование только на потребление/перемотку, не DecodeAll.
                            byte[]? packet = _source!.ReadPacket();
                            if (packet == null) throw new EndOfStreamException("Opus stream ended before its declared duration.");
                            if (packet.Length == 0 || packet.Length > MaxPacketBytes || packet.Length % 4 != 0)
                                throw new InvalidDataException("Invalid decoded Opus packet size.");
                            if (packet.Length > _cacheCapacity)
                                throw new InvalidDataException("PCM cache is smaller than an Opus packet.");
                            AppendPacket(packet);
                            continue; // Проверяем Dispose/новую позицию после каждого пакета.
                        }
                        int take = (int)Math.Min(count - copied, Math.Min(_decodedEnd - position, _length - position));
                        lock (_stateLock)
                        {
                            if (_disposed) return copied;
                            if (version != _seekVersion) continue;
                            ReadCache(position, buffer, offset + copied, take);
                            _position += take;
                        }
                        copied += take;
                    }
                    catch (Exception ex)
                    {
                        lock (_stateLock)
                        {
                            if (_disposed) return copied;
                            if (version != _seekVersion) continue;
                        }
                        _failure = ex;
                        AppLog.Warn("OpusFileReader.Decode", ex);
                        if (_failureReportedVersion != version)
                        {
                            _failureReportedVersion = version;
                            try { DecodeFailed?.Invoke(ex); }
                            catch (Exception callbackError) { AppLog.Debug("OpusFileReader.DecodeFailed callback", callbackError); }
                        }
                        throw new IOException(Loc.T("Failed to decode Opus audio.",
                            "Не удалось декодировать аудио Opus.", "No se pudo decodificar el audio Opus."), ex);
                    }
                }
                return copied;
            }
        }

        /// <summary>Переход к позиции вне кэша: по индексу, если источник умеет,
        /// иначе (только назад) — заново с начала.</summary>
        private void Reposition(long position)
        {
            if (_source != null && _source.CanSeek)
            {
                _source.Seek(position);
                _cacheBase = _cacheStart = _decodedEnd = position;
            }
            else if (position < _cacheStart || _source == null) ResetSource(position);
            // Без индекса вперёд просто декодируем подряд, как раньше.
        }

        private void ResetSource(long position = 0)
        {
            _source?.Dispose();
            _source = null;
            _source = _openSource();
            if (_source.PcmLength != _length) throw new IOException("Opus file changed during playback.");
            _cache.SetLength(0);
            _cacheBase = _cacheStart = _decodedEnd = 0;
            if (position > 0 && _source.CanSeek)
            {
                _source.Seek(position);
                _cacheBase = _cacheStart = _decodedEnd = position;
            }
        }

        private void AppendPacket(byte[] packet)
        {
            int index = (int)(_decodedEnd % _cacheCapacity);
            int first = (int)Math.Min(packet.Length, _cacheCapacity - index);
            _cache.Position = index;
            _cache.Write(packet, 0, first);
            if (first < packet.Length)
            {
                _cache.Position = 0;
                _cache.Write(packet, first, packet.Length - first);
            }
            _decodedEnd = checked(_decodedEnd + packet.Length);
            _cacheStart = Math.Max(_cacheBase, _decodedEnd - _cacheCapacity);
        }

        private void ReadCache(long position, byte[] buffer, int offset, int count)
        {
            long index = position % _cacheCapacity;
            int first = (int)Math.Min(count, _cacheCapacity - index);
            _cache.Position = index;
            _cache.ReadExactly(buffer.AsSpan(offset, first));
            if (first < count)
            {
                _cache.Position = 0;
                _cache.ReadExactly(buffer.AsSpan(offset + first, count - first));
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_stateLock) { _disposed = true; }
            if (disposing)
            {
                lock (_ioLock)
                {
                    try { _source?.Dispose(); }
                    finally { _source = null; _cache.Dispose(); }
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class OpusPacketSource : IOpusPacketSource
    {
        /// <summary>Разгон декодера после перехода: RFC 7845 рекомендует не меньше 80 мс.</summary>
        internal const int PreRollSamples = 3840;
        private readonly FileStream _stream;
        private OggPacketStream _packets;
        private IOpusDecoder _decoder = ManagedOpusDecoderFactory.CreateStereo48k();
        private long _skipBytes, _returnedBytes;
        private readonly double _gain;
        private readonly OpusContainerInfo _info;
        public long PcmLength { get; }
        public bool CanSeek => _info.SeekOffsets.Length > 0;
        public OpusPacketSource(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                var info = _info = OpusContainerInfo.Read(_stream);
                _skipBytes = info.PreSkip * 4L;
                PcmLength = checked((info.Granules - info.StartGranule - info.PreSkip) * 4L);
                _stream.Position = 0;
                _packets = new OggPacketStream(_stream);
                byte[] head = _packets.ReadPacket() ?? throw new InvalidDataException("Missing OpusHead.");
                byte[] tags = _packets.ReadPacket() ?? throw new InvalidDataException("Missing OpusTags.");
                if (head.Length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8) ||
                    tags.Length < 8 || !tags.AsSpan(0, 8).SequenceEqual("OpusTags"u8))
                    throw new InvalidDataException("Invalid Opus header packets.");
                _gain = Math.Pow(10, BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16, 2)) / (256.0 * 20));
            }
            catch { _decoder.Dispose(); _stream.Dispose(); throw; }
        }
        public byte[]? ReadPacket()
        {
            while (_returnedBytes < PcmLength)
            {
                byte[] packet = _packets.ReadPacket() ?? throw new EndOfStreamException("Truncated Opus audio data.");
                int frames = OpusPacketInfo.GetNumSamples(packet.AsSpan(), 48000);
                if (frames <= 0 || frames > 5760) throw new InvalidDataException("Invalid Opus frame duration.");
                short[] samples = new short[checked(frames * 2)];
                int decoded = _decoder.Decode(packet.AsSpan(), samples.AsSpan(), frames, false);
                if (decoded != frames) throw new InvalidDataException("Unexpected Opus decode length.");
                int bytes = checked(samples.Length * 2);
                int skip = (int)Math.Min(bytes, _skipBytes);
                _skipBytes -= skip;
                int take = (int)Math.Min(bytes - skip, PcmLength - _returnedBytes);
                if (take == 0) continue;
                if (_gain != 1)
                {
                    for (int i = skip / 2; i < (skip + take) / 2; i++)
                        samples[i] = (short)Math.Clamp(Math.Round(samples[i] * _gain), short.MinValue, short.MaxValue);
                }
                byte[] pcm = new byte[take];
                Buffer.BlockCopy(samples, skip, pcm, 0, take);
                _returnedBytes += take;
                return pcm;
            }
            return null;
        }

        /// <summary>
        /// Переход к позиции PCM: ближайшая страница Ogg не позже (цель − 80 мс), новый
        /// декодер, лишние сэмплы до цели отбрасываются. Результат совпадает с линейным
        /// декодированием с точностью до сходимости состояния декодера (как в libopusfile).
        /// </summary>
        public void Seek(long pcmBytePosition)
        {
            if (pcmBytePosition < 0 || pcmBytePosition % 4 != 0) throw new ArgumentOutOfRangeException(nameof(pcmBytePosition));
            pcmBytePosition = Math.Min(pcmBytePosition, PcmLength);
            long target = pcmBytePosition / 4 + _info.PreSkip;                // в отсчётах от начала потока
            int index = _info.FindSeekPoint(Math.Max(0, target - PreRollSamples));
            _stream.Position = _info.SeekOffsets[index];
            _packets = new OggPacketStream(_stream);
            IOpusDecoder previousDecoder = _decoder;
            _decoder = ManagedOpusDecoderFactory.CreateStereo48k();
            previousDecoder.Dispose();
            _skipBytes = checked((target - _info.SeekSamples[index]) * 4L);
            _returnedBytes = pcmBytePosition;
        }

        public void Dispose()
        {
            try { _stream.Dispose(); }
            finally { _decoder.Dispose(); }
        }
    }

    /// <summary>Последовательный Ogg без сохранения истории страниц/пакетов.
    /// Одна страница <= 65025 байт, один пакет <= 1 МиБ; CRC проверяется при чтении.</summary>
    internal sealed class OggPacketStream
    {
        private readonly Stream _stream;
        private readonly byte[] _header = new byte[27];
        private readonly byte[] _lacing = new byte[255];
        private readonly byte[] _payload = new byte[65025];
        private readonly byte[] _packet = new byte[1024 * 1024];
        private int _segments, _segmentIndex, _payloadOffset;
        private bool _eos;
        private static readonly uint[] CrcTable = CreateCrcTable();
        internal OggPacketStream(Stream stream) { _stream = stream; }
        internal byte[]? ReadPacket()
        {
            int size = 0;
            while (true)
            {
                if (_segmentIndex >= _segments)
                {
                    if (_eos || _stream.Position == _stream.Length)
                    {
                        if (size != 0) throw new EndOfStreamException("Incomplete Ogg packet.");
                        return null;
                    }
                    LoadPage();
                    if (_segments == 0) continue;
                }
                int take = _lacing[_segmentIndex++];
                if (size > _packet.Length - take) throw new InvalidDataException("Ogg packet exceeds safe size limit.");
                Buffer.BlockCopy(_payload, _payloadOffset, _packet, size, take);
                _payloadOffset += take;
                size += take;
                if (take < 255)
                {
                    if (size == 0) throw new InvalidDataException("Empty Ogg audio packet.");
                    return _packet.AsSpan(0, size).ToArray();
                }
            }
        }
        private void LoadPage()
        {
            _stream.ReadExactly(_header);
            if (!_header.AsSpan(0, 4).SequenceEqual("OggS"u8) || _header[4] != 0)
                throw new InvalidDataException("Invalid Ogg page.");
            _segments = _header[26];
            _stream.ReadExactly(_lacing.AsSpan(0, _segments));
            int payloadBytes = 0;
            for (int i = 0; i < _segments; i++) payloadBytes += _lacing[i];
            _stream.ReadExactly(_payload.AsSpan(0, payloadBytes));
            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(22, 4));
            _header.AsSpan(22, 4).Clear();
            uint crc = UpdateCrc(0, _header);
            crc = UpdateCrc(crc, _lacing.AsSpan(0, _segments));
            crc = UpdateCrc(crc, _payload.AsSpan(0, payloadBytes));
            if (crc != expected) throw new InvalidDataException("Ogg CRC mismatch: the audio file is damaged.");
            _segmentIndex = _payloadOffset = 0;
            _eos = (_header[5] & 4) != 0;
        }
        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes) crc = (crc << 8) ^ CrcTable[(byte)((crc >> 24) ^ b)];
            return crc;
        }
        private static uint[] CreateCrcTable()
        {
            uint[] table = new uint[256];
            for (int i = 0; i < table.Length; i++)
            {
                uint crc = (uint)i << 24;
                for (int bit = 0; bit < 8; bit++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0u);
                table[i] = crc;
            }
            return table;
        }
    }

    /// <summary>Проверка контейнера до библиотеки. Только один mono/stereo Opus stream.
    /// Заголовки/таблицы читаются ограниченными буферами; PCM не распаковывается.</summary>
    internal sealed record OpusContainerInfo(int PreSkip, long Granules)
    {
        /// <summary>Отсчёт (в granule), с которого начинается звук: обычно 0, у потоков,
        /// записанных «с середины», — больше (RFC 7845, 4.5).</summary>
        internal long StartGranule { get; init; }

        /// <summary>Индекс перемотки: смещение страницы, с которой начинается новый пакет,
        /// и номер первого сэмпла этого пакета (от начала звука). Точки не чаще 1 с,
        /// для 10-часовой записи это ~36 000 пар — около 600 КБ.</summary>
        internal long[] SeekOffsets { get; init; } = Array.Empty<long>();
        internal long[] SeekSamples { get; init; } = Array.Empty<long>();
        internal const long SeekPointSpacing = 48000;

        /// <summary>Последняя точка с первым сэмплом не позже sample (или первая точка).</summary>
        internal int FindSeekPoint(long sample)
        {
            int index = Array.BinarySearch(SeekSamples, sample);
            if (index < 0) index = ~index - 1;
            return Math.Clamp(index, 0, SeekSamples.Length - 1);
        }

        internal static OpusContainerInfo Read(Stream stream)
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[27];
            Span<byte> lacing = stackalloc byte[255];
            Span<byte> opusHead = stackalloc byte[19];
            uint serial = 0, expectedSequence = 0;
            bool first = true, eos = false;
            int preSkip = 0;
            long granules = 0, packetBytes = 0;
            long completedPackets = 0, startGranule = -1;
            var seekOffsets = new System.Collections.Generic.List<long>();
            var seekGranules = new System.Collections.Generic.List<long>();
            while (stream.Position < stream.Length)
            {
                if (eos) throw new InvalidDataException("Chained or trailing Opus streams are not supported.");
                long pageOffset = stream.Position;
                stream.ReadExactly(header);
                if (!header[..4].SequenceEqual("OggS"u8) || header[4] != 0)
                    throw new InvalidDataException("Invalid Ogg page header.");
                byte flags = header[5];
                if ((flags & ~7) != 0 || (!first && (flags & 2) != 0))
                    throw new InvalidDataException("Invalid Ogg page flags.");
                uint currentSerial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]);
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(header[18..22]);
                if (first) { serial = currentSerial; expectedSequence = sequence; }
                if (currentSerial != serial || sequence != expectedSequence++)
                    throw new InvalidDataException("Missing pages or multiple Ogg streams.");
                if (((flags & 1) != 0) != (packetBytes > 0))
                    throw new InvalidDataException("Invalid Ogg packet continuation.");
                int segments = header[26];
                stream.ReadExactly(lacing[..segments]);
                // Страница, с которой начинается новый звуковой пакет (после OpusHead и
                // OpusTags), — точка перемотки. Её первый сэмпл = granule предыдущих страниц.
                bool audioPageStart = (flags & 1) == 0 && completedPackets >= 2 && segments > 0;
                long pageGranuleForIndex = BinaryPrimitives.ReadInt64LittleEndian(header[6..14]);
                int completedOnPage = 0;
                int payloadBytes = 0;
                for (int i = 0; i < segments; i++)
                {
                    payloadBytes += lacing[i];
                    packetBytes += lacing[i];
                    if (packetBytes > 1024 * 1024) throw new InvalidDataException("Ogg packet exceeds safe size limit.");
                    if (lacing[i] < 255) { packetBytes = 0; completedOnPage++; }
                }
                if (audioPageStart && startGranule < 0)
                {
                    // Первая звуковая страница: проверяем, не начинается ли поток не с нуля
                    startGranule = ReadStartGranule(stream, lacing[..segments], pageGranuleForIndex, packetBytes != 0);
                    stream.Position = pageOffset + 27 + segments;
                }
                if (audioPageStart && (seekGranules.Count == 0 ||
                    granules - seekGranules[^1] >= SeekPointSpacing))
                {
                    seekOffsets.Add(pageOffset);
                    seekGranules.Add(seekGranules.Count == 0 ? startGranule : granules);
                }
                completedPackets += completedOnPage;
                if (payloadBytes > stream.Length - stream.Position) throw new EndOfStreamException("Truncated Ogg page.");
                long payloadEnd = stream.Position + payloadBytes;
                if (first)
                {
                    if ((flags & 2) == 0 || segments == 0 || lacing[0] < 19)
                        throw new InvalidDataException("Missing Opus identification header.");
                    stream.ReadExactly(opusHead);
                    if (!opusHead[..8].SequenceEqual("OpusHead"u8) || opusHead[8] > 15 ||
                        opusHead[9] is not (1 or 2) || opusHead[18] != 0)
                        throw new InvalidDataException("Unsupported Opus header or channel mapping.");
                    preSkip = BinaryPrimitives.ReadUInt16LittleEndian(opusHead[10..12]);
                    first = false;
                }
                long pageGranule = BinaryPrimitives.ReadInt64LittleEndian(header[6..14]);
                if (pageGranule >= 0)
                {
                    if (pageGranule < granules) throw new InvalidDataException("Invalid Opus granule order.");
                    granules = pageGranule;
                }
                eos = (flags & 4) != 0;
                if (eos && (packetBytes != 0 || pageGranule < 0))
                    throw new InvalidDataException("Incomplete final Ogg packet.");
                stream.Position = payloadEnd;
            }
            if (startGranule < 0) startGranule = 0;
            if (first || !eos || granules - startGranule < preSkip)
                throw new InvalidDataException("Opus stream has no valid end-of-stream page.");
            var samples = new long[seekGranules.Count];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = Math.Max(0, seekGranules[i] - startGranule);
                // granule не убывают (проверено выше); первая точка — начало звука
                if (i > 0 && samples[i] < samples[i - 1]) throw new InvalidDataException("Invalid Opus granule order.");
            }
            if (samples.Length > 0) samples[0] = 0;
            return new OpusContainerInfo(preSkip, granules)
            {
                StartGranule = startGranule,
                SeekOffsets = seekOffsets.ToArray(),
                SeekSamples = samples
            };
        }

        /// <summary>
        /// Granule первого сэмпла звука. Обычно 0; у потока, записанного с середины,
        /// granule первой страницы больше числа её сэмплов. Меньше (обрезка начала)
        /// не поддерживаем и считаем нулём — как и раньше.
        /// </summary>
        private static long ReadStartGranule(Stream stream, ReadOnlySpan<byte> lacing, long pageGranule, bool lastPacketContinues)
        {
            if (pageGranule <= 0) return 0;
            int payloadBytes = 0;
            foreach (byte b in lacing) payloadBytes += b;
            byte[] payload = new byte[payloadBytes];
            stream.ReadExactly(payload);
            long samples = 0;
            int offset = 0, size = 0;
            for (int i = 0; i < lacing.Length; i++)
            {
                size += lacing[i];
                if (lacing[i] < 255)
                {
                    if (size > 0)
                    {
                        int frames = OpusPacketInfo.GetNumSamples(payload.AsSpan(offset, size), 48000);
                        if (frames <= 0 || frames > 5760) throw new InvalidDataException("Invalid Opus frame duration.");
                        samples += frames;
                    }
                    offset += size; size = 0;
                }
            }
            _ = lastPacketContinues; // незавершённый пакет в granule страницы не входит
            return Math.Max(0, pageGranule - samples);
        }
    }
}
