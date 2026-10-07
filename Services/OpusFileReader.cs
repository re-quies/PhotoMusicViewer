using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Читает Ogg Opus файл как WaveStream с поддержкой перемотки (Position).
    ///
    /// Декодирование идёт в фоновом потоке: воспроизведение начинается сразу,
    /// не дожидаясь декодирования всего файла (раньше длинная запись
    /// декодировалась целиком до старта, с заметной паузой).
    /// Length/TotalTime растут, пока фоновое декодирование не закончится
    /// (обычно это секунды — декодер многократно быстрее реального времени).
    ///
    /// Память: PCM хранится цепочкой блоков по ChunkBytes, а не одним растущим
    /// массивом. Раньше буфер удваивался через Array.Resize: каждое расширение
    /// копировало всё уже декодированное и на пике требовало два буфера сразу,
    /// а до половины выделенной памяти оставалось неиспользованной.
    ///
    /// Буфер ограничен потолком MaxPcmBytes (~2.9 часа звука): более длинные
    /// записи загружаются частично, о чём сообщает событие Truncated.
    /// </summary>
    public class OpusFileReader : WaveStream
    {
        private readonly WaveFormat _waveFormat = new WaveFormat(48000, 16, 2);
        private readonly object _lock = new();

        private readonly FileStream _fileStream;
        private readonly OpusOggReadStream _oggStream;

        // Потолок буфера декодированного PCM (~2 ГБ = ~2.9 часа при 48кГц/16бит/стерео).
        // Превышение останавливает декодирование явно - см. Truncated.
        private const long MaxPcmBytes = 2_000_000_000;

        // Размер одного блока буфера. Должен быть чётным: тогда сэмпл (2 байта)
        // никогда не разрывается между блоками.
        private const int ChunkBytes = 4 << 20;   // 4 МБ

        private readonly List<byte[]> _chunks = new();
        private long _decodedBytes;
        private bool _decodingComplete;
        private bool _disposed;
        private long _position;

        /// <summary>
        /// Запись длиннее потолка буфера: декодирование остановлено.
        /// Аргумент - длительность успешно загруженной части.
        /// </summary>
        public event Action<TimeSpan>? Truncated;

        public OpusFileReader(string path)
        {
            // Заголовок читаем синхронно: если файл не Opus, исключение
            // вылетит прямо из конструктора (как и раньше)
            _fileStream = File.OpenRead(path);
            try
            {
                var decoder = new OpusDecoder(48000, 2);
                _oggStream = new OpusOggReadStream(decoder, _fileStream);
            }
            catch
            {
                _fileStream.Dispose();
                throw;
            }

            var thread = new Thread(DecodeAll) { IsBackground = true };
            thread.Start();
        }

        private void DecodeAll()
        {
            bool truncated = false;

            try
            {
                while (true)
                {
                    lock (_lock)
                    {
                        if (_disposed) break;
                    }

                    if (!_oggStream.HasNextPacket) break;

                    short[]? packet = _oggStream.DecodeNextPacket();
                    if (packet == null) continue;

                    lock (_lock)
                    {
                        if (_disposed) break;

                        if (_decodedBytes + packet.Length * 2L > MaxPcmBytes)
                        {
                            // Потолок достигнут - останавливаемся явно,
                            // уже декодированная часть остаётся доступной для воспроизведения
                            truncated = true;
                            break;
                        }

                        AppendSamples(packet);
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch (Exception ex)
            {
                // повреждённый хвост файла - считаем поток законченным
                AppLog.Warn("OpusFileReader.DecodeAll", ex);
            }
            finally
            {
                lock (_lock)
                {
                    _decodingComplete = true;
                    Monitor.PulseAll(_lock);
                }
                _fileStream.Dispose();

                if (truncated) Truncated?.Invoke(TotalTime);
            }
        }

        /// <summary>
        /// Дописывает декодированные сэмплы в блочный буфер (PCM16, little-endian).
        /// Новые блоки выделяются по мере надобности, уже записанное не копируется.
        /// Вызывается только под _lock.
        /// </summary>
        private void AppendSamples(short[] packet)
        {
            int written = 0;

            while (written < packet.Length)
            {
                int chunkIndex = (int)(_decodedBytes / ChunkBytes);
                int offset = (int)(_decodedBytes % ChunkBytes);

                while (chunkIndex >= _chunks.Count) _chunks.Add(new byte[ChunkBytes]);
                var chunk = _chunks[chunkIndex];

                // _decodedBytes всегда чётное (2 байта на сэмпл), а ChunkBytes — чётный,
                // поэтому в блоке всегда есть место как минимум под один целый сэмпл
                int room = (ChunkBytes - offset) / 2;
                int take = Math.Min(room, packet.Length - written);

                for (int i = 0; i < take; i++)
                {
                    short sample = packet[written + i];
                    chunk[offset + i * 2] = (byte)(sample & 0xFF);
                    chunk[offset + i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }

                written += take;
                _decodedBytes += take * 2L;
            }
        }

        public override WaveFormat WaveFormat => _waveFormat;

        // Пока декодирование не закончено, Length растёт вместе с буфером
        public override long Length
        {
            get { lock (_lock) return _decodedBytes; }
        }

        public override long Position
        {
            get { lock (_lock) return _position; }
            set
            {
                lock (_lock)
                {
                    long aligned = value - (value % WaveFormat.BlockAlign);
                    _position = Math.Clamp(aligned, 0, _decodedBytes);
                    Monitor.PulseAll(_lock);
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                // Ждём, пока фоновый декодер догонит позицию чтения
                while (_position >= _decodedBytes && !_decodingComplete && !_disposed)
                    Monitor.Wait(_lock, 100);

                if (_disposed) return 0;

                int bytesAvailable = (int)Math.Min(int.MaxValue, _decodedBytes - _position);
                int bytesToCopy = Math.Min(count, bytesAvailable);
                if (bytesToCopy <= 0) return 0;

                // Копируем по блокам: запрошенный кусок может пересекать границу блока
                int copied = 0;
                while (copied < bytesToCopy)
                {
                    long pos = _position + copied;
                    int chunkIndex = (int)(pos / ChunkBytes);
                    int chunkOffset = (int)(pos % ChunkBytes);
                    int take = Math.Min(bytesToCopy - copied, ChunkBytes - chunkOffset);

                    Array.Copy(_chunks[chunkIndex], chunkOffset, buffer, offset + copied, take);
                    copied += take;
                }

                _position += copied;
                return copied;
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                _disposed = true;
                // Освобождаем буфер сразу: длинная запись держит сотни мегабайт,
                // а после Dispose чтение всё равно возвращает 0
                _chunks.Clear();
                _chunks.TrimExcess();
                Monitor.PulseAll(_lock);
            }
            base.Dispose(disposing);
        }
    }
}
