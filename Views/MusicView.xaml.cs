using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer.Views
{
    public partial class MusicView : UserControl
    {
        private static readonly string[] SupportedExtensions =
        {
            ".mp3", ".flac", ".wav", ".ogg", ".opus", ".aac", ".m4a"
        };

        private readonly ObservableCollection<TrackItem> _tracks = new();
        private readonly AudioPlayerService _player = new();
        private readonly Random _random = new();

        private int _currentIndex = -1;
        private bool _isSeeking;
        private RepeatMode _repeatMode = RepeatMode.Off;

        private SortMode _sortMode = SortMode.Name;
        private bool _sortDescending;

        private int _dragSourceIndex = -1;      // индекс перетаскиваемого трека
        private bool _reorderActive;            // порог перетаскивания пройден
        private Point _dragStartPoint;
        private TrackItem? _editingTrack;       // трек, название которого сейчас редактируется
        private bool _windowHooked;

        public event Action<bool>? ModeSwitchRequested;

        private enum RepeatMode { Off, RepeatOne, RepeatAll }
        private enum SortMode { Name, DateModified, Size, Type }

        private class TrackItem : INotifyPropertyChanged
        {
            private string _path = "";
            private bool _isPlaying;
            private bool _isEditing;

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Path
            {
                get => _path;
                set { _path = value; Notify(); }
            }

            public bool IsPlaying
            {
                get => _isPlaying;
                set
                {
                    if (_isPlaying == value) return;
                    _isPlaying = value;
                    Notify();
                }
            }

            // true, пока название редактируется прямо в плейлисте
            public bool IsEditing
            {
                get => _isEditing;
                set
                {
                    if (_isEditing == value) return;
                    _isEditing = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
                }
            }

            public string FileName =>
                _path.Length > 0 ? System.IO.Path.GetFileName(_path) : "";

            // \u266A слева отмечает трек, который сейчас играет
            public string DisplayName => (_isPlaying ? "\u266A  " : "") + FileName;

            public override string ToString() => DisplayName;

            private void Notify() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }

        public MusicView()
        {
            InitializeComponent();
            TrackListBox.ItemsSource = _tracks;
            _tracks.CollectionChanged += (_, _) => UpdateTrackCounter();

            Loc.LanguageChanged += ApplyLocalization;
            ApplyLocalization();

            _player.TrackEnded += OnTrackEnded;
            _player.PositionChanged += OnPositionChanged;
            _player.Volume = VolumeSlider.Value;
            _player.PlaybackError += msg => Dispatcher.Invoke(() =>
    MessageBox.Show(msg, Loc.T("PhotoMusicViewer - Playback Error", "PhotoMusicViewer - ошибка воспроизведения", "PhotoMusicViewer - Error de reproducción"), MessageBoxButton.OK, MessageBoxImage.Error));
            _player.PlaybackNotice += msg => Dispatcher.Invoke(() =>
    MessageBox.Show(msg, "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information));

            Loaded += (_, _) =>
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input, new Action(() => Focus()));

                if (!_windowHooked)
                {
                    var window = Window.GetWindow(this);
                    if (window != null)
                    {
                        window.Deactivated += (_, __) => CommitTrackRename();
                        _windowHooked = true;
                    }
                }
            };

            UpdateTrackCounter();
            UpdateVolumeText(VolumeSlider.Value);
        }

        // --- Добавление файлов/папок ---

        private void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            // Родной WPF-диалог (.NET 8) вместо System.Windows.Forms.FolderBrowserDialog -
            // убирает зависимость от WinForms целиком
            var dialog = new Microsoft.Win32.OpenFolderDialog();

            if (dialog.ShowDialog() == true)
            {
                // Диалог выбора папки тоже оставляет след в "Недавних файлах" Windows -
                // чистим его, как это уже делается в фото-режиме
                PrivacyCleanupService.RemoveFromRecentItems(dialog.FolderName);

                bool wasEmpty = _tracks.Count == 0;
                AddPathsToPlaylist(new[] { dialog.FolderName });

                if (wasEmpty && _tracks.Count > 0)
                {
                    LoadTrack(0);
                }
            }
        }

        private void AddPathsToPlaylist(IEnumerable<string> paths)
        {
            // Защита от дубликатов: файлы, уже присутствующие в плейлисте,
            // повторно не добавляются (раньше открытие второго файла из той же
            // папки задваивало весь список)
            var existing = new HashSet<string>(
                _tracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    var files = Directory.EnumerateFiles(path)
                        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

                    foreach (var file in files)
                    {
                        if (existing.Add(file))
                            _tracks.Add(new TrackItem { Path = file });
                    }
                }
                else if (File.Exists(path) &&
                         SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    if (existing.Add(path))
                        _tracks.Add(new TrackItem { Path = path });
                }
            }
        }

        // --- Воспроизведение ---

        private void LoadTrack(int index)
        {
            if (index < 0) return;

            // Исчезнувшие с диска файлы молча выбрасываем из плейлиста
            // (без окон с ошибками - как пропуск отсутствующих файлов в фото-режиме)
            while (_tracks.Count > 0)
            {
                if (index >= _tracks.Count) index = 0;
                if (File.Exists(_tracks[index].Path)) break;
                _tracks.RemoveAt(index);
                if (index < _currentIndex) _currentIndex--;
            }

            if (_tracks.Count == 0)
            {
                ResetPlayerUi();
                return;
            }

            _currentIndex = index;
            TrackListBox.SelectedIndex = index;
            TrackListBox.ScrollIntoView(_tracks[index]);
            MarkPlayingTrack(_tracks[index]);

            _player.Load(_tracks[index].Path);
            NowPlayingText.Text = _tracks[index].FileName;
            _player.Play();
            PlayPauseButton.Content = "\u23F8";
        }

        private void MarkPlayingTrack(TrackItem? playing)
        {
            foreach (var t in _tracks)
                t.IsPlaying = ReferenceEquals(t, playing);
        }

        private void ResetPlayerUi()
        {
            _player.Unload();
            _currentIndex = -1;
            MarkPlayingTrack(null);
            NowPlayingText.Text = Loc.T("No track loaded", "Трек не загружен", "Ninguna pista cargada");
            PlayPauseButton.Content = "\u25B6";
            SeekSlider.Value = 0;
            TimeText.Text = "00:00 / 00:00";
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

        private void TogglePlayPause()
        {
            if (_currentIndex < 0)
            {
                if (_tracks.Count > 0) LoadTrack(0);
                return;
            }

            if (_player.IsPlaying)
            {
                _player.Pause();
                PlayPauseButton.Content = "\u25B6";
            }
            else
            {
                _player.Play();
                PlayPauseButton.Content = "\u23F8";
            }
        }

        private void PrevTrackButton_Click(object sender, RoutedEventArgs e) => PlayPrev();
        private void NextTrackButton_Click(object sender, RoutedEventArgs e) => PlayNext(userInitiated: true);

        private void PlayPrev()
        {
            if (_tracks.Count == 0) return;
            int nextIndex = (_currentIndex - 1 + _tracks.Count) % _tracks.Count;
            LoadTrack(nextIndex);
        }

        private void PlayNext(bool userInitiated)
        {
            if (_tracks.Count == 0) return;

            if (!userInitiated && _repeatMode == RepeatMode.RepeatOne)
            {
                // Не пересоздаём декодер (для ogg/opus это было дорого) -
                // просто перематываем на начало и играем снова
                _player.SeekTo(TimeSpan.Zero);
                _player.Play();
                PlayPauseButton.Content = "\u23F8";
                return;
            }

            int nextIndex = _currentIndex + 1;

            if (nextIndex >= _tracks.Count)
            {
                if (_repeatMode == RepeatMode.RepeatAll || userInitiated)
                {
                    nextIndex = 0;
                }
                else
                {
                    _player.Stop();
                    PlayPauseButton.Content = "\u25B6";
                    return;
                }
            }

            LoadTrack(nextIndex);
        }

        private void OnTrackEnded()
        {
            Dispatcher.Invoke(() => PlayNext(userInitiated: false));
        }

        private void OnPositionChanged(TimeSpan current, TimeSpan total)
        {
            Dispatcher.Invoke(() =>
            {
                if (!_isSeeking)
                {
                    SeekSlider.Maximum = total.TotalSeconds;
                    SeekSlider.Value = current.TotalSeconds;
                }
                TimeText.Text = $"{Format(current)} / {Format(total)}";
            });
        }

        private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";

        // --- Перемотка стрелками ---

        private void SeekRelative(TimeSpan delta)
        {
            if (_currentIndex < 0) return;

            var duration = _player.Duration ?? TimeSpan.Zero;
            var newPos = _player.Position + delta;

            if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && newPos > duration) newPos = duration;

            _player.SeekTo(newPos);
        }

        // --- Seek по полоске (точный клик и перетаскивание) ---

        private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = true;
            SetSliderValueFromMouse(e.GetPosition(SeekSlider));
            e.Handled = true; // не даём стандартному "постраничному" клику перехватить событие
        }

        private void SeekSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isSeeking && e.LeftButton == MouseButtonState.Pressed)
            {
                SetSliderValueFromMouse(e.GetPosition(SeekSlider));
            }
        }

        private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isSeeking = false;
            _player.SeekTo(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        private void SetSliderValueFromMouse(Point pos)
        {
            if (SeekSlider.ActualWidth <= 0) return;
            double ratio = Math.Clamp(pos.X / SeekSlider.ActualWidth, 0, 1);
            SeekSlider.Value = ratio * SeekSlider.Maximum;
        }

        // --- Громкость ---

        private bool _isVolumeDragging;

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _player.Volume = e.NewValue;
            UpdateVolumeText(e.NewValue);
        }

        // Точный клик и перетаскивание - как у полоски перемотки
        // (стандартный клик по полоске слайдера прыгает на LargeChange, а не в точку клика)
        private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isVolumeDragging = true;
            SetSliderValueFromMouse(VolumeSlider, e.GetPosition(VolumeSlider));
            e.Handled = true;
        }

        private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isVolumeDragging && e.LeftButton == MouseButtonState.Pressed)
                SetSliderValueFromMouse(VolumeSlider, e.GetPosition(VolumeSlider));
        }

        private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isVolumeDragging = false;
        }

        private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double step = e.Delta > 0 ? 0.05 : -0.05;
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, 0, 1);
            e.Handled = true;
        }

        // --- Скорость воспроизведения (0.1x..3x) ---

        private bool _isSpeedDragging;

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _player.Speed = e.NewValue;
            if (SpeedText != null)
                SpeedText.Text = e.NewValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x";
        }

        private void SpeedSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSpeedDragging = true;
            SetSliderValueFromMouse(SpeedSlider, e.GetPosition(SpeedSlider));
            e.Handled = true;
        }

        private void SpeedSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isSpeedDragging && e.LeftButton == MouseButtonState.Pressed)
                SetSliderValueFromMouse(SpeedSlider, e.GetPosition(SpeedSlider));
        }

        private void SpeedSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isSpeedDragging = false;
        }

        private void SpeedSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double step = e.Delta > 0 ? 0.1 : -0.1;
            SpeedSlider.Value = Math.Clamp(SpeedSlider.Value + step, SpeedSlider.Minimum, SpeedSlider.Maximum);
            e.Handled = true;
        }

        private void SpeedText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SpeedSlider.Value = 1.0;
            e.Handled = true;
        }

        // Общий помощник: ставит значение слайдера в точку клика (с учётом Minimum,
        // в отличие от варианта для SeekSlider, где Minimum всегда 0)
        private static void SetSliderValueFromMouse(Slider slider, Point pos)
        {
            if (slider.ActualWidth <= 0) return;
            double ratio = Math.Clamp(pos.X / slider.ActualWidth, 0, 1);
            slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }

        private void UpdateVolumeText(double volume)
        {
            if (VolumePercentText != null)
                VolumePercentText.Text = $"{(int)Math.Round(volume * 100)}%";
        }

        // --- Shuffle (одноразовое перемешивание) / Repeat ---

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            int startIndex = _currentIndex < 0 ? 0 : _currentIndex + 1;
            if (startIndex >= _tracks.Count - 1) return; // меньше двух треков для перемешивания

            var sub = _tracks.Skip(startIndex).ToList();
            for (int i = sub.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (sub[i], sub[j]) = (sub[j], sub[i]);
            }
            for (int i = 0; i < sub.Count; i++)
            {
                _tracks[startIndex + i] = sub[i];
            }
        }

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            _repeatMode = _repeatMode switch
            {
                RepeatMode.Off => RepeatMode.RepeatAll,
                RepeatMode.RepeatAll => RepeatMode.RepeatOne,
                RepeatMode.RepeatOne => RepeatMode.Off,
                _ => RepeatMode.Off
            };

            UpdateRepeatButtonText();
        }

        private void UpdateRepeatButtonText()
        {
            RepeatButton.Content = _repeatMode switch
            {
                RepeatMode.Off => Loc.T("Repeat: Off", "Повтор: выкл", "Repetir: no"),
                RepeatMode.RepeatAll => Loc.T("Repeat: All", "Повтор: все", "Repetir: todo"),
                RepeatMode.RepeatOne => Loc.T("Repeat: One", "Повтор: один", "Repetir: uno"),
                _ => Loc.T("Repeat: Off", "Повтор: выкл", "Repetir: no")
            };
        }

        // --- Локализация (EN/RU) ---

        private const string DeveloperGitHubUrl = "https://github.com/re-quies/fastcollageforwin";

        private void GitHubButton_Click(object sender, RoutedEventArgs e) => OpenDeveloperGitHub();

        private static void OpenDeveloperGitHub()
        {
            if (string.IsNullOrWhiteSpace(DeveloperGitHubUrl))
            {
                MessageBox.Show(
                    Loc.T("The GitHub link isn't set yet.",
                          "Ссылка на GitHub пока не указана.",
                          "El enlace de GitHub aún no está configurado."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // открываем только http/https — чтобы кнопка не запустила посторонний файл
            if (!Uri.TryCreate(DeveloperGitHubUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(
                    Loc.T("The GitHub link is invalid. Use an http(s) address.",
                          "Неверная ссылка на GitHub. Используйте адрес http(s).",
                          "El enlace de GitHub no es válido. Use una dirección http(s)."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true   // открывает браузер по умолчанию
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.T("Couldn't open the browser: ", "Не удалось открыть браузер: ", "No se pudo abrir el navegador: ") + ex.Message,
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LangButton_Click(object sender, RoutedEventArgs e) => Loc.Toggle();

        private void ApplyLocalization()
        {
            LangButton.Content = Loc.Code;

            PhotoModeToggleBtn.Content = Loc.T("Photo", "Фото", "Foto");
            MusicModeToggleBtn.Content = Loc.T("Music", "Музыка", "Música");
            AddFolderButton.Content = Loc.T("Add Folder", "Добавить папку", "Añadir carpeta");
            GitHubButton.ToolTip = Loc.T("Open the developer's GitHub page", "Открыть страницу GitHub разработчика", "Abrir la página de GitHub del desarrollador");
            ShuffleButton.Content = Loc.T("Shuffle", "Перемешать", "Aleatorio");
            RemoveTrackButton.Content = Loc.T("Remove", "Убрать", "Quitar");
            ClearButton.Content = Loc.T("Clear", "Очистить", "Vaciar");
            SpeedLabel.Text = Loc.T("Speed", "Скорость", "Velocidad");
            VolLabel.Text = Loc.T("Vol", "Громк.", "Vol.");
            SpeedText.ToolTip = Loc.T("Click to reset speed to 1.0x", "Клик — сбросить скорость на 1.0x", "Clic — restablecer la velocidad a 1.0x");

            SetComboItemText(SortModeCombo, 0, Loc.T("Name", "Имя", "Nombre"));
            SetComboItemText(SortModeCombo, 1, Loc.T("Date modified", "Дата изменения", "Fecha de modificación"));
            SetComboItemText(SortModeCombo, 2, Loc.T("Size", "Размер", "Tamaño"));
            SetComboItemText(SortModeCombo, 3, Loc.T("Type", "Тип", "Tipo"));

            UpdateRepeatButtonText();
            UpdateTrackCounter();

            // Текст-заглушка виден, только пока трек не загружен
            if (_currentIndex < 0)
                NowPlayingText.Text = Loc.T("No track loaded", "Трек не загружен", "Ninguna pista cargada");
        }

        private static void SetComboItemText(ComboBox combo, int index, string text)
        {
            if (index < combo.Items.Count && combo.Items[index] is ComboBoxItem item)
                item.Content = text;
        }

        // --- Сортировка (как в фото-режиме) ---

        private void SortModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_tracks.Count == 0) return;

            _sortMode = SortModeCombo.SelectedIndex switch
            {
                0 => SortMode.Name,
                1 => SortMode.DateModified,
                2 => SortMode.Size,
                3 => SortMode.Type,
                _ => SortMode.Name
            };

            ReapplySort();
        }

        private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tracks.Count == 0) return;

            _sortDescending = !_sortDescending;
            SortDirectionButton.Content = _sortDescending ? "\u2193" : "\u2191";

            ReapplySort();
        }

        private void ReapplySort()
        {
            string? currentPath = (_currentIndex >= 0 && _currentIndex < _tracks.Count)
                ? _tracks[_currentIndex].Path
                : null;

            IEnumerable<TrackItem> query = _tracks.ToList();

            query = _sortMode switch
            {
                SortMode.Name => query.OrderBy(t => t.FileName, StringComparer.OrdinalIgnoreCase),
                SortMode.DateModified => query.OrderBy(t => SafeLastWrite(t.Path)),
                SortMode.Size => query.OrderBy(t => SafeFileLength(t.Path)),
                SortMode.Type => query.OrderBy(t => Path.GetExtension(t.Path).ToLowerInvariant())
                                      .ThenBy(t => t.FileName, StringComparer.OrdinalIgnoreCase),
                _ => query
            };

            if (_sortDescending)
                query = query.Reverse();

            var sorted = query.ToList();
            _tracks.Clear();
            foreach (var t in sorted)
                _tracks.Add(t);

            if (currentPath != null)
            {
                _currentIndex = FindTrackIndexByPath(currentPath);
                if (_currentIndex >= 0)
                {
                    TrackListBox.SelectedIndex = _currentIndex;
                    TrackListBox.ScrollIntoView(_tracks[_currentIndex]);
                }
            }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        private static long SafeFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        // --- Список: двойной клик, удаление, очистка, drag-and-drop ---

        private void TrackListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TrackListBox.SelectedIndex >= 0)
            {
                LoadTrack(TrackListBox.SelectedIndex);
            }
        }

        private void RemoveTrackButton_Click(object sender, RoutedEventArgs e)
        {
            int index = TrackListBox.SelectedIndex;
            if (index < 0) return;

            bool removingCurrent = index == _currentIndex;

            _tracks.RemoveAt(index);

            if (removingCurrent)
            {
                ResetPlayerUi();
            }
            else if (index < _currentIndex)
            {
                _currentIndex--;
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tracks.Count == 0) return;
            _tracks.Clear();
            ResetPlayerUi();
        }

        private void UpdateTrackCounter()
        {
            TrackCountText.Text = _tracks.Count switch
            {
                0 => "",
                1 => Loc.T("1 track", "1 трек", "1 pista"),
                _ => Loc.T($"{_tracks.Count} tracks", $"Треков: {_tracks.Count}", $"Pistas: {_tracks.Count}")
            };
        }

        private void TrackListBox_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
        }

        private void TrackListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Клик по тексту названия - это переименование; перетаскивание
            // начинается только с пустой области строки (правее текста)
            if (IsOverTrackTitle(e.OriginalSource))
            {
                _dragSourceIndex = -1;
                return;
            }

            _dragStartPoint = e.GetPosition(null);
            var item = ItemFromPoint(e.GetPosition(TrackListBox));
            _dragSourceIndex = item != null ? _tracks.IndexOf(item) : -1;
            _reorderActive = false;
        }

        private void TrackListBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragSourceIndex < 0)
            {
                StopReorder();
                return;
            }

            if (!_reorderActive)
            {
                var currentPos = e.GetPosition(null);
                if (Math.Abs(currentPos.X - _dragStartPoint.X) < 6 &&
                    Math.Abs(currentPos.Y - _dragStartPoint.Y) < 6) return;

                _reorderActive = true;
                TrackListBox.CaptureMouse(); // курсор можно уводить за границы строки
            }

            // Живая перестановка: перетаскиваемый трек следует за курсором
            var target = ItemFromPoint(e.GetPosition(TrackListBox));
            if (target == null) return;

            int targetIndex = _tracks.IndexOf(target);
            if (targetIndex < 0 || targetIndex == _dragSourceIndex) return;

            string? playingPath = (_currentIndex >= 0 && _currentIndex < _tracks.Count)
                ? _tracks[_currentIndex].Path
                : null;

            _tracks.Move(_dragSourceIndex, targetIndex);
            _dragSourceIndex = targetIndex;
            TrackListBox.SelectedIndex = targetIndex;

            if (playingPath != null)
                _currentIndex = FindTrackIndexByPath(playingPath);
        }

        private void TrackListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragSourceIndex = -1;
            StopReorder();
        }

        private void StopReorder()
        {
            if (!_reorderActive) return;
            _reorderActive = false;
            TrackListBox.ReleaseMouseCapture();
        }

        private static bool IsOverTrackTitle(object? source)
        {
            var element = source as DependencyObject;
            while (element != null && element is not ListBoxItem)
            {
                if (element is FrameworkElement fe &&
                    (fe.Name == "TrackTitleText" || fe.Name == "TrackEditBox"))
                    return true;
                element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        private void TrackListBox_Drop(object sender, DragEventArgs e)
        {
            // Перетаскивание файлов/папок из проводника Windows
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                AddPathsToPlaylist(paths);
            }
            _dragSourceIndex = -1;
        }

        private TrackItem? ItemFromPoint(Point point)
        {
            var element = TrackListBox.InputHitTest(point) as DependencyObject;
            while (element != null && element is not ListBoxItem)
            {
                element = VisualTreeHelper.GetParent(element);
            }
            return (element as ListBoxItem)?.DataContext as TrackItem;
        }

        // --- Клавиатура ---

        private void MusicView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Во время редактирования названия горячие клавиши не работают
            // (иначе пробел и стрелки нельзя было бы набрать в поле)
            if (_editingTrack != null) return;

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;
                case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                    PlayNext(userInitiated: true);
                    e.Handled = true;
                    break;
                case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                    PlayPrev();
                    e.Handled = true;
                    break;
                case Key.Right:
                    SeekRelative(TimeSpan.FromSeconds(5));
                    e.Handled = true;
                    break;
                case Key.Left:
                    SeekRelative(TimeSpan.FromSeconds(-5));
                    e.Handled = true;
                    break;
                case Key.Up:
                case Key.Down:
                case Key.Tab:
                    e.Handled = true;
                    break;
            }
        }

        // --- Переключение режима ---

        private void PhotoModeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            PhotoModeToggleBtn.IsChecked = true;
            MusicModeToggleBtn.IsChecked = false;
            ModeSwitchRequested?.Invoke(false);
        }

        private void MusicModeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            MusicModeToggleBtn.IsChecked = true;
            PhotoModeToggleBtn.IsChecked = false;
        }

        public void OpenFile(string path)
        {
            if (!File.Exists(path)) return;
            CommitTrackRename();

            var folder = Path.GetDirectoryName(path)!;
            AddPathsToPlaylist(new[] { folder });

            int index = FindTrackIndexByPath(path);
            if (index >= 0)
            {
                LoadTrack(index);
            }
        }

        private int FindTrackIndexByPath(string path)
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (string.Equals(_tracks[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // --- Переименование трека прямо в плейлисте (клик по названию) ---

        private void TrackTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TrackItem track) return;

            CommitTrackRename(); // закрываем предыдущее редактирование, если было

            var box = FindEditBoxFor(track);
            if (box == null) return;

            box.Text = Path.GetFileNameWithoutExtension(track.Path);
            _editingTrack = track;
            track.IsEditing = true;

            // Фокус и каретка - после того как поле станет видимым
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }));

            e.Handled = true;
        }

        private void TrackEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTrackRename();
                Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTrackRename();
                Focus();
                e.Handled = true;
            }
        }

        private void TrackEditBox_LostFocus(object sender, RoutedEventArgs e) => CommitTrackRename();

        private void CancelTrackRename()
        {
            var track = _editingTrack;
            _editingTrack = null;
            if (track != null) track.IsEditing = false;
        }

        private TextBox? FindEditBoxFor(TrackItem track)
        {
            if (TrackListBox.ItemContainerGenerator.ContainerFromItem(track) is not ListBoxItem container)
                return null;
            return FindDescendantTextBox(container);
        }

        private static TextBox? FindDescendantTextBox(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox box) return box;
                var found = FindDescendantTextBox(child);
                if (found != null) return found;
            }
            return null;
        }

        private void MusicView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_editingTrack == null) return;

            var box = FindEditBoxFor(_editingTrack);
            if (box == null || !IsWithin(e.OriginalSource as DependencyObject, box))
            {
                CommitTrackRename();
                Focus();
            }
        }

        private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
        {
            while (element != null)
            {
                if (ReferenceEquals(element, ancestor)) return true;
                element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        private static string GetAvailablePath(string desiredPath)
        {
            if (!File.Exists(desiredPath)) return desiredPath;

            string folder = Path.GetDirectoryName(desiredPath)!;
            string baseName = Path.GetFileNameWithoutExtension(desiredPath);
            string ext = Path.GetExtension(desiredPath);

            int counter = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(folder, $"{baseName} ({counter}){ext}");
                counter++;
            } while (File.Exists(candidate));

            return candidate;
        }

        private void CommitTrackRename()
        {
            var track = _editingTrack;
            if (track == null) return;

            var box = FindEditBoxFor(track);
            _editingTrack = null;
            track.IsEditing = false;

            if (box == null) return;

            var oldPath = track.Path;
            string newBaseName = box.Text.Trim();
            string oldBaseName = Path.GetFileNameWithoutExtension(oldPath);
            string ext = Path.GetExtension(oldPath);

            if (string.IsNullOrEmpty(newBaseName) || newBaseName == oldBaseName) return;

            foreach (char c in Path.GetInvalidFileNameChars())
                newBaseName = newBaseName.Replace(c, '_');

            string folder = Path.GetDirectoryName(oldPath)!;
            string newPath = GetAvailablePath(Path.Combine(folder, newBaseName + ext));

            bool renamingCurrent = _currentIndex >= 0 && _currentIndex < _tracks.Count &&
                                   ReferenceEquals(_tracks[_currentIndex], track);

            if (!renamingCurrent)
            {
                // Трек сейчас не играет - файл свободен, просто переименовываем
                try
                {
                    File.Move(oldPath, newPath);
                    track.Path = newPath;
                    ReapplySort();
                }
                catch
                {
                    // не получилось (файл занят/нет прав) - оставляем старое имя
                }
                return;
            }

            // Играющий файл держится плеером открытым - на время переименования
            // освобождаем его, затем продолжаем с той же позиции
            var position = _player.Position;
            bool wasPlaying = _player.IsPlaying;
            _player.Unload();

            try
            {
                File.Move(oldPath, newPath);
                track.Path = newPath;
            }
            catch
            {
                newPath = oldPath; // не получилось (файл занят/нет прав) - продолжаем со старым именем
            }

            NowPlayingText.Text = Path.GetFileName(newPath);
            ReapplySort();

            try
            {
                _player.Load(newPath);
                _player.SeekTo(position);
                if (wasPlaying)
                {
                    _player.Play();
                    PlayPauseButton.Content = "\u23F8";
                }
                else
                {
                    PlayPauseButton.Content = "\u25B6";
                }
            }
            catch
            {
                // не удалось перезапустить воспроизведение - трек останется на паузе
            }
        }
    }
}
