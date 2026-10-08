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
        private bool _lastFrame;
        private bool _finished;
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

        /// <summary>Сбрасывает только буферы. Для перемотки используйте Seek:
        /// изменение позиции источника и сброс должны быть одной операцией.</summary>
        public void Reset()
        {
            lock (_lock) ResetCore();
        }

        /// <summary>Выполняет перемотку источника под той же блокировкой,
        /// что и Read. Даже при ошибке перемотки старый кеш не используется.</summary>
        public void Seek(Action seekSource)
        {
            ArgumentNullException.ThrowIfNull(seekSource);
            lock (_lock)
            {
                try { seekSource(); }
                finally { ResetCore(); }
            }
        }

        private void ResetCore()
        {
            _readBufferCount = 0;
            _readBufferPos = 0;
            _phase = 0;
            _primed = false;
            _sourceEnded = false;
            _lastFrame = false;
            _finished = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));

            lock (_lock)
            {
                int frames = count / _channels;
                if (frames == 0 || _finished) return 0;
                int written = 0;

                if (!_primed)
                {
                    if (!TryReadFrame(_frameA))
                    {
                        _finished = true;
                        return 0;
                    }
                    if (!TryReadFrame(_frameB))
                    {
                        Array.Copy(_frameA, _frameB, _channels);
                        _lastFrame = true;
                    }
                    _primed = true;
                    _phase = 0;
                }

                for (int i = 0; i < frames && !_finished; i++)
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
                        _phase -= 1.0;
                        if (_lastFrame)
                        {
                            _finished = true;
                            break;
                        }
                        Array.Copy(_frameB, _frameA, _channels);
                        if (!TryReadFrame(_frameB))
                        {
                            // Последний кадр имеет длительность одного входного кадра.
                            // На медленной скорости выдаём его до конца, затем Read == 0.
                            Array.Copy(_frameA, _frameB, _channels);
                            _lastFrame = true;
                        }
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
