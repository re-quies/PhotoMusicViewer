using System;
using NAudio.Wave;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Меняет скорость воспроизведения простым ресемплингом с линейной
    /// интерполяцией: вместе со скоростью меняется и высота тона,
    /// как у плёнки/пластинки. То же поведение, что у MediaPlayer.SpeedRatio,
    /// поэтому оба бэкенда звучат одинаково. Никаких новых зависимостей -
    /// только математика поверх ISampleProvider.
    ///
    /// На скорости ровно 1.0x интерполяция вырождается в копирование
    /// исходных сэмплов без искажений.
    ///
    /// Read вызывается потоком NAudio, Speed/Reset - из UI-потока, поэтому lock.
    /// </summary>
    public class SpeedSampleProvider : ISampleProvider
    {
        public const double MinSpeed = 0.1;
        public const double MaxSpeed = 3.0;

        private readonly ISampleProvider _source;
        private readonly int _channels;
        private readonly float[] _frameA; // предыдущий кадр (по одному сэмплу на канал)
        private readonly float[] _frameB; // следующий кадр
        private readonly float[] _readBuffer;
        private int _readBufferCount;
        private int _readBufferPos;
        private double _phase;   // дробная позиция между _frameA и _frameB [0..1)
        private bool _primed;    // прочитаны ли стартовые кадры
        private bool _sourceEnded;
        private double _speed = 1.0;
        private readonly object _lock = new();

        public SpeedSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            _frameA = new float[_channels];
            _frameB = new float[_channels];
            // Небольшая порция чтения (~25 мс при 48кГц): после перемотки
            // не доигрывается длинный "хвост" уже прочитанных старых данных
            _readBuffer = new float[Math.Max(_channels * 1200, _channels)];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary>Скорость воспроизведения, ограничена MinSpeed..MaxSpeed. Можно менять на лету.</summary>
        public double Speed
        {
            get { lock (_lock) return _speed; }
            set { lock (_lock) _speed = Math.Clamp(value, MinSpeed, MaxSpeed); }
        }

        /// <summary>Сбрасывает внутренние буферы (вызывается после перемотки источника).</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _readBufferCount = 0;
                _readBufferPos = 0;
                _phase = 0;
                _primed = false;
                _sourceEnded = false;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                int frames = count / _channels;
                int written = 0;

                if (!_primed)
                {
                    if (!TryReadFrame(_frameA)) return 0;
                    if (!TryReadFrame(_frameB))
                    {
                        // В источнике был ровно один кадр - отдаём его и заканчиваем
                        Array.Copy(_frameA, 0, buffer, offset, _channels);
                        return _channels;
                    }
                    _primed = true;
                    _phase = 0;
                }

                for (int i = 0; i < frames; i++)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        float a = _frameA[ch];
                        float b = _frameB[ch];
                        buffer[offset + written + ch] = (float)(a + (b - a) * _phase);
                    }
                    written += _channels;

                    _phase += _speed;
                    while (_phase >= 1.0)
                    {
                        Array.Copy(_frameB, _frameA, _channels);
                        if (!TryReadFrame(_frameB))
                        {
                            return written; // источник закончился - отдаём, что успели
                        }
                        _phase -= 1.0;
                    }
                }

                return written;
            }
        }

        private bool TryReadFrame(float[] frame)
        {
            if (_readBufferPos + _channels > _readBufferCount)
            {
                if (_sourceEnded) return false;

                _readBufferCount = _source.Read(_readBuffer, 0, _readBuffer.Length);
                _readBufferPos = 0;

                if (_readBufferCount < _channels)
                {
                    _sourceEnded = true;
                    return false;
                }
            }

            Array.Copy(_readBuffer, _readBufferPos, frame, 0, _channels);
            _readBufferPos += _channels;
            return true;
        }
    }
}
