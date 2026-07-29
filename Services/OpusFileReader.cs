using System;
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
        // Больше в byte[] всё равно не помещается; раньше превышение обрывало
        // декодирование молча (исключением), теперь остановка явная - см. Truncated.
        private const long MaxPcmBytes = 2_000_000_000;

        private byte[] _pcmData = new byte[1 << 20]; // растущий буфер декодированного PCM
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

                        EnsureCapacity(_decodedBytes + packet.Length * 2L);
                        foreach (var sample in packet)
                        {
                            _pcmData[_decodedBytes++] = (byte)(sample & 0xFF);
                            _pcmData[_decodedBytes++] = (byte)((sample >> 8) & 0xFF);
                        }
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch
            {
                // повреждённый хвост файла - считаем поток законченным
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

        private void EnsureCapacity(long required)
        {
            if (required <= _pcmData.Length) return;

            long newSize = _pcmData.Length;
            while (newSize < required) newSize *= 2;
            // int.MaxValue превышает максимально допустимый размер массива в .NET,
            // поэтому ограничиваемся потолком буфера (required не бывает больше него)
            if (newSize > MaxPcmBytes) newSize = MaxPcmBytes;

            Array.Resize(ref _pcmData, (int)newSize);
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

                Array.Copy(_pcmData, _position, buffer, offset, bytesToCopy);
                _position += bytesToCopy;
                return bytesToCopy;
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                _disposed = true;
                Monitor.PulseAll(_lock);
            }
            base.Dispose(disposing);
        }
    }
}
