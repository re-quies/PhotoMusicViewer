using System;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Vorbis;

namespace PhotoMusicViewer.Services
{
    public class AudioPlayerService : IDisposable
    {
        private enum Backend { MediaPlayer, NAudio }

        private readonly MediaPlayer _mediaPlayer = new();
        private readonly DispatcherTimer _positionTimer = new();

        private WaveOutEvent? _waveOut;
        private WaveStream? _naudioReader; // VorbisWaveReader или OpusFileReader
        private SpeedSampleProvider? _speedProvider; // регулятор скорости для NAudio-ветки

        private Backend _backend = Backend.MediaPlayer;
        private bool _isPlaying;
        private double _volume = 0.8;
        private double _speed = 1.0;
        private TimeSpan? _pendingSeek;

        public event Action? TrackEnded;
        public event Action<TimeSpan, TimeSpan>? PositionChanged; // current, total

        public bool IsPlaying => _isPlaying;

        public AudioPlayerService()
        {
            _mediaPlayer.MediaOpened += (_, _) =>
            {
                if (_pendingSeek.HasValue)
                {
                    _mediaPlayer.Position = _pendingSeek.Value;
                    _pendingSeek = null;
                }
            };

            _mediaPlayer.MediaEnded += (_, _) =>
            {
                _isPlaying = false;
                _positionTimer.Stop();
                TrackEnded?.Invoke();
            };
            _mediaPlayer.MediaFailed += (_, args) =>
{
    _isPlaying = false;
    _positionTimer.Stop();
    PlaybackError?.Invoke(Loc.T("MediaPlayer failed to play file", "MediaPlayer не смог воспроизвести файл", "MediaPlayer no pudo reproducir el archivo") + $": {args.ErrorException}");
};

            _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
            _positionTimer.Tick += (_, _) => ReportPosition();
        }

        public event Action<string>? PlaybackError;

        /// <summary>Некритичные уведомления (например, очень длинная Opus-запись загружена не целиком).</summary>
        public event Action<string>? PlaybackNotice;

public void Load(string path)
{
    StopInternalPlayback();
    _pendingSeek = null;

    var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

    try
    {
        if (ext == ".ogg" || ext == ".opus")
        {
            _backend = Backend.NAudio;

            try
            {
                _naudioReader = ext == ".opus"
                    ? new OpusFileReader(path)
                    : new VorbisWaveReader(path);
            }
            catch (Exception)
            {
                // Не Ogg Vorbis — пробуем как Ogg Opus
                _naudioReader = new OpusFileReader(path);
            }

            // Очень длинная запись может не поместиться в буфер целиком -
            // сообщаем об этом явно вместо прежней молчаливой обрезки
            if (_naudioReader is OpusFileReader opusReader)
            {
                opusReader.Truncated += loadedDuration => PlaybackNotice?.Invoke(Loc.T(
                    "This recording is very long. Only the first " +
                    $"{(int)loadedDuration.TotalMinutes} minutes were loaded; " +
                    "playback beyond that point isn't available.",
                    "Эта запись очень длинная. Загружены только первые " +
                    $"{(int)loadedDuration.TotalMinutes} мин.; " +
                    "воспроизведение дальше этой точки недоступно.",
                    "Esta grabación es muy larga. Solo se cargaron los primeros " +
                    $"{(int)loadedDuration.TotalMinutes} min; " +
                    "la reproducción más allá de ese punto no está disponible."));
            }

            _waveOut = new WaveOutEvent();
            // Регулировка скорости: ридер оборачивается в ресемплер (см. SpeedSampleProvider).
            // На 1.0x интерполяция отдаёт исходные сэмплы без искажений.
            _speedProvider = new SpeedSampleProvider(_naudioReader.ToSampleProvider()) { Speed = _speed };
            _waveOut.Init(_speedProvider);
            _waveOut.Volume = (float)_volume;
            _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
        }
        else
        {
            _backend = Backend.MediaPlayer;
            _mediaPlayer.Open(new Uri(path, UriKind.Absolute));
            _mediaPlayer.Volume = _volume;
            _mediaPlayer.SpeedRatio = _speed;
        }
    }
    catch (Exception ex)
    {
        PlaybackError?.Invoke(Loc.T("Failed to load", "Не удалось загрузить", "No se pudo cargar") + $" '{System.IO.Path.GetFileName(path)}': {ex}");
    }
}

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
{
    if (e.Exception != null)
    {
        PlaybackError?.Invoke(Loc.T("Playback error", "Ошибка воспроизведения", "Error de reproducción") + $": {e.Exception}");
        _isPlaying = false;
        _positionTimer.Stop();
        return;
    }

    if (_naudioReader != null && _naudioReader.Position >= _naudioReader.Length)
    {
        _isPlaying = false;
        _positionTimer.Stop();
        TrackEnded?.Invoke();
    }
}

        public void Play()
{
    try
    {
        if (_backend == Backend.NAudio) _waveOut?.Play();
        else _mediaPlayer.Play();

        _isPlaying = true;
        _positionTimer.Start();
    }
    catch (Exception ex)
    {
        PlaybackError?.Invoke(Loc.T("Failed to start playback", "Не удалось начать воспроизведение", "No se pudo iniciar la reproducción") + $": {ex}");
    }
}

        public void Pause()
        {
            if (_backend == Backend.NAudio) _waveOut?.Pause();
            else _mediaPlayer.Pause();

            _isPlaying = false;
            _positionTimer.Stop();
        }

        public void Stop()
        {
            StopInternalPlayback();
            _isPlaying = false;
            _positionTimer.Stop();
        }

        /// <summary>
        /// Полностью освобождает открытый файл (в отличие от Stop, который держит его открытым).
        /// Нужно перед переименованием играющего трека и при очистке плейлиста.
        /// </summary>
        public void Unload()
        {
            StopInternalPlayback();
            _mediaPlayer.Close();
            _isPlaying = false;
            _positionTimer.Stop();
        }

        private void StopInternalPlayback()
{
    if (_waveOut != null)
    {
        _waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
        _waveOut.Stop();
        _waveOut.Dispose();
        _waveOut = null;
    }
    _naudioReader?.Dispose();
    _naudioReader = null;
    _speedProvider = null;

    _mediaPlayer.Stop();
}

        public void SeekTo(TimeSpan position)
{
    if (_backend == Backend.NAudio && _naudioReader != null)
    {
        _naudioReader.CurrentTime = position;
        _speedProvider?.Reset(); // выбрасываем уже прочитанный "хвост" старых данных
    }
    else if (_mediaPlayer.NaturalDuration.HasTimeSpan)
    {
        _mediaPlayer.Position = position;
    }
    else
    {
        _pendingSeek = position; // файл ещё открывается - позиция применится в MediaOpened
    }
}

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 1);
                _mediaPlayer.Volume = _volume;
                if (_waveOut != null) _waveOut.Volume = (float)_volume;
            }
        }

        /// <summary>
        /// Скорость воспроизведения (0.1x..3x), меняется на лету для обоих бэкендов.
        /// Как у плёнки: вместе со скоростью меняется и высота тона
        /// (так ведёт себя и MediaPlayer.SpeedRatio, поэтому бэкенды звучат одинаково).
        /// </summary>
        public double Speed
        {
            get => _speed;
            set
            {
                _speed = Math.Clamp(value, SpeedSampleProvider.MinSpeed, SpeedSampleProvider.MaxSpeed);
                _mediaPlayer.SpeedRatio = _speed;
                if (_speedProvider != null) _speedProvider.Speed = _speed;
            }
        }

        public TimeSpan Position => _backend == Backend.NAudio && _naudioReader != null
    ? _naudioReader.CurrentTime
    : _mediaPlayer.Position;

        public TimeSpan? Duration
{
    get
    {
        if (_backend == Backend.NAudio && _naudioReader != null)
            return _naudioReader.TotalTime;

        return _mediaPlayer.NaturalDuration.HasTimeSpan
            ? _mediaPlayer.NaturalDuration.TimeSpan
            : null;
    }
}

        private void ReportPosition()
        {
            var duration = Duration;
            if (duration.HasValue)
            {
                PositionChanged?.Invoke(Position, duration.Value);
            }
        }

        public void Dispose()
        {
            StopInternalPlayback();
            _positionTimer.Stop();
        }
    }
}