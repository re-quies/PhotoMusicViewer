using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Vorbis;

namespace PhotoMusicViewer.Services
{
    public class AudioPlayerService : IDisposable
    {
        private enum Backend { MediaPlayer, NAudio }

        private MediaPlayer _mediaPlayer = new();
        private readonly DispatcherTimer _positionTimer = new();

        private WaveOutEvent? _waveOut;
        private WaveStream? _naudioReader; // Ogg Vorbis / Ogg Opus / MP4 Opus
        private SpeedSampleProvider? _speedProvider; // регулятор скорости для NAudio-ветки

        private Backend _backend = Backend.MediaPlayer;
        private bool _isPlaying;
        private bool _isLoaded;
        private bool _isLoading;
        private bool _playRequested;
        private double _volume = 0.8;
        private double _speed = 1.0;
        private TimeSpan? _pendingSeek;

        public event Action? TrackEnded;
        public event Action<TimeSpan, TimeSpan>? PositionChanged; // current, total

        public bool IsPlaying => _isPlaying;
        public bool IsLoading => _isLoading;
        public bool CanPlay => _isLoaded || _isLoading;
        public bool IsPlayPending => _isLoading && _playRequested;
        public event Action? PlaybackStateChanged;

        public AudioPlayerService()
        {
            HookMediaPlayer(_mediaPlayer);

            _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
            _positionTimer.Tick += (_, _) => ReportPosition();
        }

        private void HookMediaPlayer(MediaPlayer player)
        {
            // Каждый Open использует отдельный объект: события старого файла
            // после переключения трека не изменяют состояние нового.
            player.MediaOpened += (_, _) =>
            {
                if (!ReferenceEquals(player, _mediaPlayer) ||
                    _backend != Backend.MediaPlayer || !_isLoading) return;
                _isLoading = false;
                _isLoaded = true;
                try
                {
                    if (_pendingSeek.HasValue)
                    {
                        player.Position = _pendingSeek.Value;
                        _pendingSeek = null;
                    }
                    if (_playRequested) Play();
                    else SetPlaying(false);
                }
                catch (Exception ex)
                {
                    StopInternalPlayback();
                    PlaybackError?.Invoke(Loc.T("Failed to open audio", "Не удалось открыть аудио", "No se pudo abrir el audio") + $": {ex.Message}");
                }
            };
            player.MediaEnded += (_, _) =>
            {
                if (!ReferenceEquals(player, _mediaPlayer) || _backend != Backend.MediaPlayer || !_isLoaded) return;
                SetPlaying(false);
                TrackEnded?.Invoke();
            };
            player.MediaFailed += (_, args) =>
            {
                if (!ReferenceEquals(player, _mediaPlayer) || _backend != Backend.MediaPlayer || !CanPlay) return;
                StopInternalPlayback();
                PlaybackError?.Invoke(Loc.T("MediaPlayer failed to play file", "MediaPlayer не смог воспроизвести файл", "MediaPlayer no pudo reproducir el archivo") + $": {args.ErrorException}");
            };
        }

        private void SetPlaying(bool playing)
        {
            _isPlaying = playing;
            if (playing) _positionTimer.Start();
            else _positionTimer.Stop();
            PlaybackStateChanged?.Invoke();
        }

        public event Action<string>? PlaybackError;


/// <summary>NAudio: true после инициализации. MediaPlayer: true означает,
/// что Open принят; готовность/ошибка приходят асинхронно через события.</summary>
public bool Load(string path)
{
    StopInternalPlayback();
    try
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Audio path is empty.", nameof(path));
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file does not exist.", path);
        var format = AudioFormatProbe.Read(path);
        if (format.Codec is AudioCodec.Opus or AudioCodec.Vorbis)
        {
            _backend = Backend.NAudio;
            _naudioReader = format.Codec == AudioCodec.Vorbis
                ? new VorbisWaveReader(path)
                : new OpusFileReader(path);

            // Opus теперь потоковый, без ограничения длительности по размеру PCM.
            // Ошибка Read/LastError поступит в WaveOut_PlaybackStopped и PlaybackError.

            _waveOut = new WaveOutEvent();
            // Регулировка скорости: ридер оборачивается в ресемплер (см. SpeedSampleProvider).
            // На 1.0x интерполяция отдаёт исходные сэмплы без искажений.
            _speedProvider = new SpeedSampleProvider(_naudioReader.ToSampleProvider()) { Speed = _speed };
            _waveOut.Init(_speedProvider);
            _waveOut.Volume = (float)_volume;
            _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
            _isLoaded = true;
        }
        else
        {
            _backend = Backend.MediaPlayer;
            _mediaPlayer = new MediaPlayer();
            HookMediaPlayer(_mediaPlayer);
            _mediaPlayer.Volume = _volume;
            _mediaPlayer.SpeedRatio = _speed;
            _isLoading = true;
            _mediaPlayer.Open(new Uri(path, UriKind.Absolute));
        }
        PlaybackStateChanged?.Invoke();
        return CanPlay;
    }
    catch (Exception ex)
    {
        StopInternalPlayback();
        AppLog.Error("AudioPlayerService.Load", ex, AppLog.Describe(path));
        PlaybackError?.Invoke(Loc.T("Failed to load", "Не удалось загрузить", "No se pudo cargar") + $" '{System.IO.Path.GetFileName(path)}': {ex}");
        return false;
    }
}

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (!_positionTimer.Dispatcher.CheckAccess())
            {
                _positionTimer.Dispatcher.BeginInvoke(new Action(() => WaveOut_PlaybackStopped(sender, e)));
                return;
            }
            if (!ReferenceEquals(sender, _waveOut) || _backend != Backend.NAudio) return;
            if (e.Exception != null)
            {
                StopInternalPlayback();
                PlaybackError?.Invoke(Loc.T("Playback error", "Ошибка воспроизведения", "Error de reproducción") + $": {e.Exception}");
                return;
            }
            if (_naudioReader != null && _naudioReader.Position >= _naudioReader.Length)
            {
                SetPlaying(false);
                TrackEnded?.Invoke();
            }
        }

        /// <summary>Возвращает false, если файл не загружен или запуск
        /// не удался. Пока MediaPlayer открывает файл, ставит запрос в очередь,
        /// но IsPlaying остаётся false до MediaOpened и успешного Play.</summary>
        public bool Play()
        {
            if (_isLoading)
            {
                _playRequested = true;
                return true;
            }
            if (!_isLoaded || (_backend == Backend.NAudio && _waveOut == null)) return false;
            try
            {
                if (_backend == Backend.NAudio) _waveOut!.Play();
                else _mediaPlayer.Play();
                _playRequested = false;
                SetPlaying(true);
                return true;
            }
            catch (Exception ex)
            {
                StopInternalPlayback();
                AppLog.Error("AudioPlayerService.Play", ex);
                PlaybackError?.Invoke(Loc.T("Failed to start playback", "Не удалось начать воспроизведение", "No se pudo iniciar la reproducción") + $": {ex}");
                return false;
            }
        }

        public void Pause()
        {
            _playRequested = false;
            if (_isLoaded)
            {
                if (_backend == Backend.NAudio) _waveOut?.Pause();
                else _mediaPlayer.Pause();
            }
            SetPlaying(false);
        }

        public void Stop() => StopInternalPlayback();

        /// <summary>Освобождает открытый файл перед переименованием/очисткой.</summary>
        public void Unload() => StopInternalPlayback();

        private void StopInternalPlayback()
        {
            _isLoaded = _isLoading = _playRequested = false;
            _pendingSeek = null;
            var waveOut = _waveOut;
            _waveOut = null;
            var reader = _naudioReader;
            _naudioReader = null;
            _speedProvider = null;
            // Opus поддерживает конкурентную отмену: прекращаем длинную перемотку
            // до ожидания устройства. Остальные ридеры освобождаем после WaveOut.
            if (reader is OpusFileReader)
            {
                try { reader.Dispose(); }
                catch (Exception ex) { AppLog.Debug("AudioPlayerService.DisposeReader", ex); }
            }
            if (waveOut != null)
            {
                waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
                try { waveOut.Stop(); }
                catch (Exception ex) { AppLog.Debug("AudioPlayerService.Stop", ex); }
                try { waveOut.Dispose(); }
                catch (Exception ex) { AppLog.Debug("AudioPlayerService.DisposeWaveOut", ex); }
            }
            if (reader is not OpusFileReader)
            {
                try { reader?.Dispose(); }
                catch (Exception ex) { AppLog.Debug("AudioPlayerService.DisposeReader", ex); }
            }
            try { _mediaPlayer.Close(); }
            catch (Exception ex) { AppLog.Debug("AudioPlayerService.Close", ex); }
            SetPlaying(false);
        }

        public void SeekTo(TimeSpan position)
{
    if (!CanPlay) return;
    if (_backend == Backend.NAudio && _naudioReader != null)
    {
        var reader = _naudioReader;
        var provider = _speedProvider;
        if (provider == null) return;
        // Read не может вклиниться между изменением позиции и сбросом буфера.
        provider.Seek(() => reader.CurrentTime = position);
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

        public TimeSpan Position => !CanPlay ? TimeSpan.Zero : _backend == Backend.NAudio && _naudioReader != null
    ? _naudioReader.CurrentTime
    : _mediaPlayer.Position;

        public TimeSpan? Duration
{
    get
    {
        if (!_isLoaded) return null;
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