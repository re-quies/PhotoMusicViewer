using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PhotoMusicViewer.Services;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Specialized;

namespace PhotoMusicViewer.Views
{

    
    public partial class PhotoView : UserControl
    {
       private enum ItemKind { Back, Folder, Image }

private class ThumbnailItem : INotifyPropertyChanged
{
    public ItemKind Kind { get; set; }
    public string Path { get; set; } = ""; // файл (Image), папка (Folder) или родительская папка (Back)
    public bool IsEnabled { get; set; } = true; // используется только для Back

    public string DisplayName => Kind == ItemKind.Folder
        ? System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
        : "";

    public Visibility ImageVisibility => Kind == ItemKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisibility => Kind == ItemKind.Folder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackVisibility => Kind == ItemKind.Back ? Visibility.Visible : Visibility.Collapsed;
    public double BackOpacity => IsEnabled ? 1.0 : 0.3;

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
        private readonly ObservableCollection<ThumbnailItem> _thumbnailItems = new();
        private CancellationTokenSource? _thumbnailLoadCts;
        private string? _gridCurrentFolder;
        private WrapPanel? _thumbnailWrapPanel;

        private void GridViewButton_Click(object sender, RoutedEventArgs e)
{
    if (ThumbnailOverlay.Visibility == Visibility.Visible)
        CloseThumbnailGrid();
    else
        OpenThumbnailGrid();
}

private void OpenThumbnailGrid()
{
    if (_currentIndex < 0 || _folderFiles.Count == 0) return;

    string currentFolder = Path.GetDirectoryName(_folderFiles[_currentIndex])!;
    ThumbnailOverlay.Visibility = Visibility.Visible;
    LoadGridFolder(currentFolder);
}
private void LoadGridFolder(string folderPath)
{
    _thumbnailLoadCts?.Cancel();
    _gridCurrentFolder = folderPath;
    _thumbnailItems.Clear();

    string? root = Path.GetPathRoot(folderPath);
    bool isRoot = !string.IsNullOrEmpty(root) &&
                  string.Equals(root.TrimEnd('\\'), folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    string? parent = null;
    if (!isRoot)
    {
        try { parent = Directory.GetParent(folderPath)?.FullName; }
        catch { parent = null; }
    }

    _thumbnailItems.Add(new ThumbnailItem
    {
        Kind = ItemKind.Back,
        Path = parent ?? "",
        IsEnabled = parent != null
    });

    List<string> subfolders;
    try
    {
        subfolders = Directory.EnumerateDirectories(folderPath)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    catch { subfolders = new List<string>(); }

    List<string> imageFiles;
    try
    {
        imageFiles = Directory.EnumerateFiles(folderPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
    }
    catch { imageFiles = new List<string>(); }

    imageFiles = ApplySort(imageFiles);

    foreach (var folder in subfolders)
        _thumbnailItems.Add(new ThumbnailItem { Kind = ItemKind.Folder, Path = folder });

    foreach (var file in imageFiles)
        _thumbnailItems.Add(new ThumbnailItem { Kind = ItemKind.Image, Path = file });

    ThumbnailItemsControl.ItemsSource = _thumbnailItems;

    _thumbnailLoadCts = new CancellationTokenSource();
    LoadThumbnailsAsync(_thumbnailLoadCts.Token);
}
private void CloseThumbnailGrid()
{
    _thumbnailLoadCts?.Cancel();
    ThumbnailOverlay.Visibility = Visibility.Collapsed;
}

// Кэш миниатюр только в памяти (на время сессии) - на диск ничего не пишется, никаких следов вроде thumbs.db.
// Ключ - путь, значение - миниатюра + время изменения файла (после Rotate/Crop/Strip EXIF запись сама устареет).
private static readonly object ThumbnailCacheLock = new();
private static readonly Dictionary<string, (long WriteTimeTicks, BitmapSource Thumb)> ThumbnailCache =
    new(StringComparer.OrdinalIgnoreCase);
private const int ThumbnailCacheLimit = 600; // ~90 МБ в худшем случае, дальше кэш просто очищается

private static bool TryGetCachedThumbnail(string path, out BitmapSource? thumb)
{
    thumb = null;
    long ticks = SafeWriteTimeTicks(path);
    lock (ThumbnailCacheLock)
    {
        if (ThumbnailCache.TryGetValue(path, out var entry) && entry.WriteTimeTicks == ticks)
        {
            thumb = entry.Thumb;
            return true;
        }
    }
    return false;
}

private static void CacheThumbnail(string path, BitmapSource thumb)
{
    long ticks = SafeWriteTimeTicks(path);
    lock (ThumbnailCacheLock)
    {
        if (ThumbnailCache.Count >= ThumbnailCacheLimit)
        {
            // Выбрасываем половину записей вместо полной очистки:
            // раньше в папках больше лимита кэш обнулялся целиком и переставал помогать
            var toRemove = ThumbnailCache.Keys.Take(ThumbnailCacheLimit / 2).ToList();
            foreach (var key in toRemove) ThumbnailCache.Remove(key);
        }
        ThumbnailCache[path] = (ticks, thumb);
    }
}

private static long SafeWriteTimeTicks(string path)
{
    try { return File.GetLastWriteTimeUtc(path).Ticks; }
    catch { return 0; }
}

// --- Упреждающее декодирование соседних фото ---
// Кэш только в памяти (на диск ничего не пишется, как и кэш миниатюр).
// Запись инвалидируется по времени изменения файла: после Rotate/Crop/Strip EXIF
// или замены файла извне устаревшая копия не используется.
private static readonly object PrefetchLock = new();
private static readonly Dictionary<string, (long WriteTimeTicks, BitmapSource Image, long SizeBytes)> PrefetchCache =
    new(StringComparer.OrdinalIgnoreCase);
private const int PrefetchCacheLimit = 4; // текущие соседи + небольшой запас

private static (BitmapSource Image, long SizeBytes) DecodeFullImage(string path)
{
    byte[] fileBytes = File.ReadAllBytes(path);
    using var ms = new MemoryStream(fileBytes);
    var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
        BitmapCacheOption.OnLoad);
    BitmapSource display = ExifOrientationService.ApplyOrientation(
        decoder.Frames[0], ExifOrientationService.GetOrientation(decoder.Frames[0]));
    if (display.CanFreeze) display.Freeze();
    return (display, fileBytes.LongLength);
}

private static bool TryGetPrefetched(string path, out BitmapSource image, out long sizeBytes)
{
    image = null!;
    sizeBytes = 0;

    long ticks = SafeWriteTimeTicks(path);
    lock (PrefetchLock)
    {
        if (PrefetchCache.TryGetValue(path, out var entry) && entry.WriteTimeTicks == ticks)
        {
            image = entry.Image;
            sizeBytes = entry.SizeBytes;
            return true;
        }
    }
    return false;
}

private void PrefetchNeighbors()
{
    if (_currentIndex < 0 || _folderFiles.Count < 2) return;

    int next = (_currentIndex + 1) % _folderFiles.Count;
    int prev = (_currentIndex - 1 + _folderFiles.Count) % _folderFiles.Count;

    var candidates = next == prev
        ? new[] { _folderFiles[next] }
        : new[] { _folderFiles[next], _folderFiles[prev] };

    foreach (var candidate in candidates)
    {
        string path = candidate;
        if (Path.GetExtension(path).ToLowerInvariant() == ".gif")
            continue; // GIF декодируется аниматором заново - кэшировать нечего

        long ticks = SafeWriteTimeTicks(path);
        lock (PrefetchLock)
        {
            if (PrefetchCache.TryGetValue(path, out var entry) && entry.WriteTimeTicks == ticks)
                continue; // уже декодирован и файл не менялся
        }

        Task.Run(() =>
        {
            try
            {
                var (image, sizeBytes) = DecodeFullImage(path);
                if (!image.IsFrozen) return; // незамороженный объект нельзя передавать между потоками

                lock (PrefetchLock)
                {
                    // Кэш крошечный, а соседи перечитываются мгновенно -
                    // при переполнении просто начинаем заново
                    if (PrefetchCache.Count >= PrefetchCacheLimit)
                        PrefetchCache.Clear();

                    PrefetchCache[path] = (SafeWriteTimeTicks(path), image, sizeBytes);
                }
            }
            catch
            {
                // повреждённый/занятый файл - ошибку пользователь увидит при обычном открытии
            }
        });
    }
}

private static BitmapSource? DecodeThumbnail(string path, int pixelWidth)
{
    try
    {
        byte[] bytes = File.ReadAllBytes(path);

        int orientation = 1;
        try
        {
            using var metaStream = new MemoryStream(bytes);
            var metaDecoder = BitmapDecoder.Create(metaStream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            orientation = ExifOrientationService.GetOrientation(metaDecoder.Frames[0]);
        }
        catch
        {
            // не удалось прочитать ориентацию - покажем миниатюру как есть
        }

        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = pixelWidth;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();

        return ExifOrientationService.ApplyOrientation(bmp, orientation);
    }
    catch
    {
        return null;
    }
}

private async void LoadThumbnailsAsync(CancellationToken token)
{
    const int thumbnailPixelWidth = 220;

    var pending = new List<ThumbnailItem>();
    foreach (var item in _thumbnailItems.ToList())
    {
        if (item.Kind != ItemKind.Image) continue;

        if (TryGetCachedThumbnail(item.Path, out var cached))
            item.Thumbnail = cached; // мгновенно, без обращения к диску
        else
            pending.Add(item);
    }

    if (pending.Count == 0) return;

    // Параллельное декодирование вместо строго по одной миниатюре
    using var semaphore = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));

    var tasks = pending.Select(async item =>
    {
        try { await semaphore.WaitAsync(token); }
        catch { return; } // отмена во время ожидания

        try
        {
            if (token.IsCancellationRequested) return;

            var thumb = await Task.Run(() => DecodeThumbnail(item.Path, thumbnailPixelWidth), token);
            if (thumb == null || token.IsCancellationRequested) return;

            CacheThumbnail(item.Path, thumb);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) item.Thumbnail = thumb;
            });
        }
        catch
        {
            // отмена или ошибка декодирования - оставляем ячейку без миниатюры
        }
        finally
        {
            semaphore.Release();
        }
    }).ToList();

    try { await Task.WhenAll(tasks); }
    catch
    {
        // отмена - штатный сценарий при закрытии сетки или смене папки
    }
}

private void ThumbnailItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (sender is not FrameworkElement fe || fe.DataContext is not ThumbnailItem item) return;

    switch (item.Kind)
    {
        case ItemKind.Back:
            if (item.IsEnabled) LoadGridFolder(item.Path);
            break;
        case ItemKind.Folder:
            LoadGridFolder(item.Path);
            break;
        case ItemKind.Image:
            OpenFileFromGrid(item.Path);
            break;
    }
}
private void OpenFileFromGrid(string path)
{
    var folder = Path.GetDirectoryName(path)!;
    _folderFiles = Directory.EnumerateFiles(folder)
        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .ToList();

    SortFolderFiles(); // сохраняет текущий выбранный режим сортировки, не сбрасывает его

    _currentIndex = _folderFiles.FindIndex(f =>
        string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    if (_currentIndex < 0)
    {
        _folderFiles = new List<string> { path };
        _currentIndex = 0;
    }

    CloseThumbnailGrid();
    ShowCurrent();
}
private void ThumbnailOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
{
    UpdateThumbnailItemSize();
}

private void UpdateThumbnailItemSize()
{
    if (_thumbnailWrapPanel == null) return;

    const int columns = 11;
    double itemSize = (ThumbnailOverlay.ActualWidth - 20) / columns;
    if (itemSize <= 0) return;

    _thumbnailWrapPanel.ItemWidth = itemSize;
    _thumbnailWrapPanel.ItemHeight = itemSize;
}
    
private void ThumbnailWrapPanel_Loaded(object sender, RoutedEventArgs e)
{
    _thumbnailWrapPanel = sender as WrapPanel;
    UpdateThumbnailItemSize();
}
       internal static readonly string[] SupportedExtensions =
{
    ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tiff", ".tif", ".gif",
    ".heic", ".cr2", ".nef", ".arw"
};

        private List<string> _folderFiles = new();
        private int _currentIndex = -1;
        private bool _isFullscreen;
        private bool _windowHooked;
        
        private enum SortMode { Name, DateModified, Size, Type }
        private SortMode _sortMode = SortMode.Name;
        private bool _sortDescending = false;
        private long _currentFileSizeBytes;
        private int _currentWidth;
        private int _currentHeight;
        private GifAnimator? _gifAnimator;

        private bool _isPanning;

        private bool _isDragCandidate;
        private Point _leftDownPos;
        private Point _panStartMouse;
        private double _panStartX;
        private double _panStartY;

        public event Action<bool>? FullscreenRequested;
        public event Action<bool>? ModeSwitchRequested;

        public PhotoView()
{
    InitializeComponent();

    Loc.LanguageChanged += ApplyLocalization;
    ApplyLocalization();

    Loaded += (_, _) =>
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input, new Action(() => Focus()));

        // Loaded срабатывает при каждом возврате в фото-режим - подписываемся на
        // Deactivated только один раз, иначе обработчики накапливаются
        // (та же защита, что уже есть в MusicView)
        if (!_windowHooked)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Deactivated += (_, __) => CommitRename();
                _windowHooked = true;
            }
        }
    };
}

        public void OpenFile(string path)
{
    if (!File.Exists(path)) return;

    // Файл может прийти через drag&drop в любом состоянии интерфейса - приводим его в порядок
    if (_isCropMode) ExitCropMode();
    if (ThumbnailOverlay.Visibility == Visibility.Visible) CloseThumbnailGrid();
    CommitRename();

    var folder = Path.GetDirectoryName(path)!;
    _folderFiles = Directory.EnumerateFiles(folder)
        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .ToList();

    // Сбрасываем сортировку на "Дата изменения, по возрастанию" для каждой новой открытой папки
    _sortMode = SortMode.DateModified;
    _sortDescending = false;
    SortModeCombo.SelectedIndex = 1;
    SortDirectionButton.Content = "\u2191";

    SortFolderFiles();

    _currentIndex = _folderFiles.FindIndex(f =>
        string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    if (_currentIndex < 0)
    {
        _folderFiles = new List<string> { path };
        _currentIndex = 0;
    }

    ShowCurrent();
}

private void SortFolderFiles()
{
    _folderFiles = ApplySort(_folderFiles);
}

private List<string> ApplySort(List<string> files)
{
    IEnumerable<string> query = files;

    query = _sortMode switch
    {
        SortMode.Name => query.OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
        SortMode.DateModified => query.OrderBy(f => File.GetLastWriteTime(f)),
        SortMode.Size => query.OrderBy(f => SafeFileLength(f)),
        SortMode.Type => query.OrderBy(f => Path.GetExtension(f).ToLowerInvariant())
                               .ThenBy(f => f, StringComparer.OrdinalIgnoreCase),
        _ => query
    };

    if (_sortDescending)
        query = query.Reverse();

    return query.ToList();
}

private static long SafeFileLength(string path)
{
    try { return new FileInfo(path).Length; }
    catch { return 0; }
}
        private void ShowCurrent()
        {
            if (_currentIndex < 0 || _currentIndex >= _folderFiles.Count) return;

            var path = _folderFiles[_currentIndex];
            _gifAnimator?.Stop();
            _gifAnimator = null;
            ResetTransform();

            var ext = Path.GetExtension(path).ToLowerInvariant();
            ConvertToJpgButton.Visibility = ext == ".webp" ? Visibility.Visible : Visibility.Collapsed;

            try
            {
                _currentWidth = 0;
                _currentHeight = 0;

                if (ext == ".gif")
                {
                    byte[] fileBytes = File.ReadAllBytes(path);
                    _currentFileSizeBytes = fileBytes.LongLength;
                    _gifAnimator = new GifAnimator(fileBytes, MainImage);
                    _gifAnimator.Start();
                    GifPlayPauseButton.Content = "\u2759\u2759";

                    // Первый кадр уже установлен аниматором - берём размеры из него без повторного декода
                    if (MainImage.Source is BitmapSource gifSource)
                    {
                        _currentWidth = gifSource.PixelWidth;
                        _currentHeight = gifSource.PixelHeight;
                    }
                }
                else
                {
                    // Сначала пробуем упреждающе декодированную копию (мгновенное листание),
                    // иначе декодируем синхронно, как раньше: один декод и для показа, и для размеров.
                    // EXIF-ориентация применяется на лету, сам файл не меняется.
                    if (!TryGetPrefetched(path, out var display, out var sizeBytes))
                    {
                        (display, sizeBytes) = DecodeFullImage(path);
                    }

                    MainImage.Source = display;
                    _currentFileSizeBytes = sizeBytes;
                    _currentWidth = display.PixelWidth;
                    _currentHeight = display.PixelHeight;
                }

                UpdateGifButtonVisibility();
                RefreshFileLabels(path);

                // Фоном декодируем соседние фото, чтобы следующее листание было мгновенным
                PrefetchNeighbors();
            }
           catch (Exception ex)
{
    FileNameDisplay.Text = Loc.T("Failed to open", "Не удалось открыть", "No se pudo abrir") + $": {Path.GetFileName(path)} ({ex.Message})";
    FileMetaText.Text = "";
}
        }

private void RefreshFileLabels(string path)
{
    FileNameDisplay.Text = Path.GetFileName(path);

    double sizeMb = _currentFileSizeBytes / (1024.0 * 1024.0);
    string sizeText = $"{sizeMb:0.00} MB";

    FileMetaText.Text = _currentWidth > 0
        ? $"({_currentWidth}\u00d7{_currentHeight}, {sizeText})   [{_currentIndex + 1}/{_folderFiles.Count}]"
        : $"({sizeText})   [{_currentIndex + 1}/{_folderFiles.Count}]";
}

        // --- Навигация ---

        private void PrevButton_Click(object sender, RoutedEventArgs e) => ShowPrev();
        private void NextButton_Click(object sender, RoutedEventArgs e) => ShowNext();

        private void ShowPrev() => NavigateSkippingMissing(forward: false);
private void ShowNext() => NavigateSkippingMissing(forward: true);

private void NavigateSkippingMissing(bool forward)
{
    if (_folderFiles.Count == 0) return;

    int guard = _folderFiles.Count; // предохранитель от бесконечного цикла

    while (guard-- > 0 && _folderFiles.Count > 0)
    {
        int candidateIndex = forward
            ? (_currentIndex + 1) % _folderFiles.Count
            : (_currentIndex - 1 + _folderFiles.Count) % _folderFiles.Count;

        string candidatePath = _folderFiles[candidateIndex];

        if (File.Exists(candidatePath))
        {
            _currentIndex = candidateIndex;
            ShowCurrent();
            return;
        }

        // Файл пропал (удалён/перемещён вне приложения) - убираем из списка и пробуем дальше
        _folderFiles.RemoveAt(candidateIndex);
        if (candidateIndex < _currentIndex) _currentIndex--;
        if (_currentIndex >= _folderFiles.Count) _currentIndex = _folderFiles.Count - 1;
    }

    ShowNoAccessibleFilesState();
}

private void GoToFirst()
{
    if (_folderFiles.Count == 0) return;
    _currentIndex = -1; // следующий кандидат при поиске вперёд - индекс 0
    NavigateSkippingMissing(forward: true);
}

private void GoToLast()
{
    if (_folderFiles.Count == 0) return;
    _currentIndex = 0; // предыдущий кандидат при поиске назад - последний индекс
    NavigateSkippingMissing(forward: false);
}

private void ShowNoAccessibleFilesState()
{
    _currentIndex = -1;
    _gifAnimator?.Stop();
    _gifAnimator = null;
    MainImage.Source = null;
    FileNameDisplay.Text = Loc.T("No accessible files in this folder", "В этой папке нет доступных файлов", "No hay archivos accesibles en esta carpeta");
    FileMetaText.Text = "";
    GifPlayPauseButton.Visibility = Visibility.Collapsed;
    ConvertToJpgButton.Visibility = Visibility.Collapsed;
}

        private void PhotoView_PreviewKeyDown(object sender, KeyEventArgs e)
{

if (FileNameEditBox.Visibility == Visibility.Visible)
    {
        return;
    }

    if (_isCropMode)
    {
        if (e.Key == Key.Escape)
        {
            ExitCropMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ApplyCrop();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab or Key.Space or Key.F11)
        {
            e.Handled = true;
        }
        return;
    }

    if (ThumbnailOverlay.Visibility == Visibility.Visible)
    {
        if (e.Key == Key.Escape)
        {
            CloseThumbnailGrid();
            e.Handled = true;
        }
        return;
    }
    switch (e.Key)
    {
        case Key.Left:
            ShowPrev();
            e.Handled = true;
            break;
        case Key.Right:
            ShowNext();
            e.Handled = true;
            break;
        case Key.Space:
            if (_gifAnimator != null) ToggleGifPlayback();
            e.Handled = true;
            break;
        case Key.Delete:
            DeleteCurrentFile();
            e.Handled = true;
            break;
        case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
            if (_currentIndex >= 0) CopyCurrentImageToClipboard();
            e.Handled = true;
            break;
        case Key.R:
            RotateCurrent();
            e.Handled = true;
            break;
        case Key.Home:
            GoToFirst();
            e.Handled = true;
            break;
        case Key.End:
            GoToLast();
            e.Handled = true;
            break;
        case Key.F11:
            ToggleFullscreen();
            e.Handled = true;
            break;
        case Key.Escape:
            if (_isFullscreen) ToggleFullscreen();
            e.Handled = true;
            break;
        case Key.Up:
        case Key.Down:
        case Key.Tab:
            // Явно блокируем: не даём WPF выполнять встроенную directional-навигацию,
            // которая раньше вызывала зависание при скрытых элементах в фулскрине
            e.Handled = true;
            break;
    }
}

        // --- Зум колесом мыши + сброс по ПКМ ---

        private void PhotoView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
{
    if (_isCropMode)
    {
        // Колесо меняет размер рамки обрезки (как в приложении "Фотографии" Windows):
        // вверх - уменьшить, вниз - увеличить
        ResizeCropRectByWheel(e.Delta);
        e.Handled = true;
        return;
    }
    if (ThumbnailOverlay.Visibility == Visibility.Visible) return;
    if (FileNameEditBox.Visibility == Visibility.Visible) return;
    if (_currentIndex < 0) return;

    double oldScale = ImageScaleTransform.ScaleX;
    double factor = e.Delta > 0 ? 1.1 : 0.9;
    double newScale = Math.Clamp(oldScale * factor, 0.2, 10);

    if (Math.Abs(newScale - oldScale) < 0.0001)
    {
        e.Handled = true;
        return;
    }

    if (e.Delta > 0)
    {
        // Приближение
        if (oldScale < 1.0)
        {
            // Картинка уже уменьшена относительно исходного размера -
            // едем в сторону исходного (нетронутого) положения, курсор не учитываем
            if (newScale >= 1.0)
            {
                // Достигли (или пересекли) исходный масштаб - смещение сбрасывается,
                // дальше начинает работать обычный зум к курсору
                ImageTranslateTransform.X = 0;
                ImageTranslateTransform.Y = 0;
            }
            else
            {
                double ratio = (1.0 - newScale) / (1.0 - oldScale);
                ImageTranslateTransform.X *= ratio;
                ImageTranslateTransform.Y *= ratio;
            }
        }
        else
        {
            // Обычный режим - зумируем к точке под курсором мыши
            Point cursor = e.GetPosition(this);
            Point imageCenter = GetImageAreaCenter();

            double offsetX = cursor.X - imageCenter.X;
            double offsetY = cursor.Y - imageCenter.Y;
            double ratio = newScale / oldScale;

            // Формула сохраняет точку под курсором неподвижной при изменении масштаба
            ImageTranslateTransform.X = offsetX * (1 - ratio) + ImageTranslateTransform.X * ratio;
            ImageTranslateTransform.Y = offsetY * (1 - ratio) + ImageTranslateTransform.Y * ratio;
        }
    }
    else
    {
        // Отдаление - без изменений: плавно едем в сторону исходного положения картинки
        if (oldScale > 1.0 && newScale > 1.0)
        {
            double ratio = (newScale - 1.0) / (oldScale - 1.0);
            ImageTranslateTransform.X *= ratio;
            ImageTranslateTransform.Y *= ratio;
        }
        else
        {
            // Достигли (или пересекли) исходный масштаб - смещение сбрасывается,
            // дальше просто уменьшаем вокруг центра
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
        }
    }

    ImageScaleTransform.ScaleX = newScale;
    ImageScaleTransform.ScaleY = newScale;
    UpdateZoomText();
    e.Handled = true;
}

        private Point GetImageAreaCenter()
        {
            // Центр области изображения в координатах "this" (UserControl),
            // не зависит от текущего RenderTransform картинки
            Point topLeft = ImageAreaGrid.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Point(
                topLeft.X + ImageAreaGrid.ActualWidth / 2.0,
                topLeft.Y + ImageAreaGrid.ActualHeight / 2.0);
        }


        private void ResetTransform()
        {
            ImageScaleTransform.ScaleX = 1;
            ImageScaleTransform.ScaleY = 1;
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
            UpdateZoomText();
        }

        private void UpdateZoomText()
        {
            int percent = (int)Math.Round(ImageScaleTransform.ScaleX * 100);
            ZoomText.Text = $"{percent}%";
        }

        // --- Панорамирование ---

        private void MainImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (ImageScaleTransform.ScaleX > 1.0)
    {
        // Увеличено - зажатие ЛКМ панорамирует
        _isPanning = true;
        _panStartMouse = e.GetPosition(this);
        _panStartX = ImageTranslateTransform.X;
        _panStartY = ImageTranslateTransform.Y;
        MainImage.CaptureMouse();
        MainImage.Cursor = Cursors.SizeAll;
    }
    else
    {
        // Исходный или уменьшенный масштаб - зажатие ЛКМ и движение мышью запускает
        // системный drag-and-drop файла (перетаскивание в другие приложения)
        _isDragCandidate = true;
        _leftDownPos = e.GetPosition(this);
    }
}

        private void MainImage_MouseMove(object sender, MouseEventArgs e)
{
    if (_isPanning)
    {
        var current = e.GetPosition(this);
        ImageTranslateTransform.X = _panStartX + (current.X - _panStartMouse.X);
        ImageTranslateTransform.Y = _panStartY + (current.Y - _panStartMouse.Y);
        return;
    }

    if (_isDragCandidate && e.LeftButton == MouseButtonState.Pressed && _currentIndex >= 0)
    {
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _leftDownPos.X) > 6 || Math.Abs(current.Y - _leftDownPos.Y) > 6)
        {
            _isDragCandidate = false;
            StartImageDrag();
        }
    }
}

        private void MainImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
{
    _isPanning = false;
    _isDragCandidate = false;
    MainImage.ReleaseMouseCapture();
    MainImage.Cursor = Cursors.Arrow;
}
private void StartImageDrag()
{
    if (_currentIndex < 0) return;
    var path = _folderFiles[_currentIndex];

    var data = new DataObject();
    data.SetFileDropList(new StringCollection { path });

    try
    {
        DragDrop.DoDragDrop(MainImage, data, DragDropEffects.Copy);
    }
    catch
    {
        // перетаскивание прервано/не удалось - молча игнорируем, без окон
    }
}
        // --- GIF play/pause ---

        private void GifPlayPauseButton_Click(object sender, RoutedEventArgs e) => ToggleGifPlayback();

        private void ToggleGifPlayback()
        {
            if (_gifAnimator == null) return;

            if (_gifAnimator.IsPlaying)
            {
                _gifAnimator.Pause();
                GifPlayPauseButton.Content = "\u25B6";
            }
            else
            {
                _gifAnimator.Start();
                GifPlayPauseButton.Content = "\u2759\u2759";
            }
        }

        private void UpdateGifButtonVisibility()
        {
            GifPlayPauseButton.Visibility = (_gifAnimator != null && !_isFullscreen)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // --- OCR: распознавание текста на фото ---

        private async void OcrButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0) return;
            var path = _folderFiles[_currentIndex];

            OcrButton.IsEnabled = false;
            OcrButton.Content = Loc.T("Scanning...", "Распознавание...", "Reconociendo...");

            try
            {
                var variants = await OcrService.RecognizeTextAsync(path);
                var found = variants.Where(v => !string.IsNullOrWhiteSpace(v.Text)).ToList();

                if (found.Count == 0)
                {
                    MessageBox.Show(Loc.T("No text was found on this image.", "На этом изображении текст не найден.", "No se encontró texto en esta imagen."),
                        "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var window = new OcrResultWindow(found) { Owner = Window.GetWindow(this) };
                    window.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Text recognition failed", "Не удалось распознать текст", "Error en el reconocimiento de texto") + $": {ex.Message}",
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                OcrButton.Content = Loc.T("Scan text", "Распознать текст", "Reconocer texto");
                OcrButton.IsEnabled = true;
            }
        }

        // --- Поворот ---

        private void RotateButton_Click(object sender, RoutedEventArgs e) => RotateCurrent();

        private void RotateCurrent()
        {
            if (_currentIndex < 0) return;
            var path = _folderFiles[_currentIndex];

            try
            {
                RotateAndSaveService.RotateAndSave(path, 90);
                ShowCurrent();
            }
            catch (NotSupportedException)
            {
                MessageBox.Show(
                    Loc.T("Rotation saving isn't supported for this file format.", "Сохранение поворота не поддерживается для этого формата файла.", "Guardar el giro no es compatible con este formato de archivo."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Rotation failed", "Не удалось повернуть", "Error al girar") + $": {ex.Message}",
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- WebP → JPG ---

        private void ConvertToJpgButton_Click(object sender, RoutedEventArgs e)
{
    if (_currentIndex < 0) return;
    var path = _folderFiles[_currentIndex];

    try
    {
        var newPath = ConvertWebpToJpgService.ConvertToJpg(path);
        _folderFiles[_currentIndex] = newPath;
        ReapplySort();
        ShowCurrent();
    }
    catch (Exception ex)
    {
        MessageBox.Show(Loc.T("Conversion failed", "Не удалось конвертировать", "Error de conversión") + $": {ex.Message}",
            "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteCurrentFile();

private void DeleteCurrentFile()
{
    if (_currentIndex < 0) return;
    var path = _folderFiles[_currentIndex];

    try
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
    catch
    {
        // Удаление не удалось (например, файл занят другим процессом) —
        // молча ничего не делаем, без всплывающих окон, как и договаривались
        return;
    }

    int removedIndex = _currentIndex;
    _folderFiles.RemoveAt(removedIndex);

    if (_folderFiles.Count == 0)
    {
        _currentIndex = -1;
        _gifAnimator?.Stop();
        _gifAnimator = null;
        MainImage.Source = null;
        FileNameDisplay.Text = Loc.T("No file opened", "Файл не открыт", "Ningún archivo abierto");
        FileMetaText.Text = "";
        GifPlayPauseButton.Visibility = Visibility.Collapsed;
        ConvertToJpgButton.Visibility = Visibility.Collapsed;
        return;
    }

    _currentIndex = Math.Min(removedIndex, _folderFiles.Count - 1);
    ShowCurrent();
}

private static string GetAvailablePath(string path)
{
    if (!File.Exists(path)) return path;

    var dir = Path.GetDirectoryName(path)!;
    var name = Path.GetFileNameWithoutExtension(path);
    var ext = Path.GetExtension(path);
    int counter = 1;

    string candidate;
    do
    {
        candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
        counter++;
    } while (File.Exists(candidate));

    return candidate;
}

private void FileNameDisplay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (_isCropMode) return;
    if (_currentIndex < 0) return;

    var path = _folderFiles[_currentIndex];
    string baseName = Path.GetFileNameWithoutExtension(path);
    var clickPos = e.GetPosition(FileNameDisplay);

    FileNameEditBox.Text = baseName;
    FileNameDisplay.Visibility = Visibility.Collapsed;
    FileNameEditBox.Visibility = Visibility.Visible;
    FileNameEditBox.Focus();

    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
    {
        int idx = FileNameEditBox.GetCharacterIndexFromPoint(clickPos, true);
        FileNameEditBox.CaretIndex = idx >= 0 ? idx : FileNameEditBox.Text.Length;
    }));

    e.Handled = true;
}

private void FileNameEditBox_PreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Enter)
    {
        Focus();
        e.Handled = true;
    }
    else if (e.Key == Key.Escape)
    {
        FileNameEditBox.Visibility = Visibility.Collapsed;
        FileNameDisplay.Visibility = Visibility.Visible;
        Focus();
        e.Handled = true;
    }
}
private void PhotoView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
{
    if (FileNameEditBox.Visibility == Visibility.Visible)
    {
        if (!IsWithin(e.OriginalSource as DependencyObject, FileNameEditBox))
        {
            CommitRename();
            Focus();
        }
        return;
    }

    if (_isCropMode) return;
    if (ThumbnailOverlay.Visibility == Visibility.Visible) return;
    if (_currentIndex < 0) return;

    if (e.ChangedButton == MouseButton.Middle)
    {
        ResetTransform();
        e.Handled = true;
    }
    else if (e.ChangedButton == MouseButton.Right)
    {
        CopyCurrentImageToClipboard();
        e.Handled = true;
    }
}
private void CopyCurrentImageToClipboard()
{
    if (MainImage.Source is BitmapSource bitmapSource)
    {
        try
        {
            // Картинка кладётся в буфер как обычно (Ctrl+V работает везде),
            // но помечается флагами приватности: Windows не сохранит её
            // в историю буфера (Win+V) и не отправит в облачную синхронизацию
            // на другие устройства. Форматы - стандартные имена, которые
            // понимает сама Windows; значение DWORD 0 = "нельзя".
            var data = new DataObject();
            data.SetImage(bitmapSource);
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(data, true);
        }
        catch
        {
            // буфер обмена занят другим процессом - молча игнорируем, без окон
        }
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

private void FileNameEditBox_LostFocus(object sender, RoutedEventArgs e)
{
    CommitRename();
}

private void CommitRename()
{
    if (FileNameEditBox.Visibility != Visibility.Visible) return; // уже применено/не редактируется - выходим
    if (_currentIndex < 0)
    {
        FileNameEditBox.Visibility = Visibility.Collapsed;
        FileNameDisplay.Visibility = Visibility.Visible;
        return;
    }

    var oldPath = _folderFiles[_currentIndex];
    string newBaseName = FileNameEditBox.Text.Trim();
    string oldBaseName = Path.GetFileNameWithoutExtension(oldPath);
    string ext = Path.GetExtension(oldPath);

    FileNameEditBox.Visibility = Visibility.Collapsed;
    FileNameDisplay.Visibility = Visibility.Visible;

    if (string.IsNullOrEmpty(newBaseName) || newBaseName == oldBaseName)
    {
        RefreshFileLabels(oldPath);
        return;
    }

    foreach (char c in Path.GetInvalidFileNameChars())
        newBaseName = newBaseName.Replace(c, '_');

    string folder = Path.GetDirectoryName(oldPath)!;
    string newPath = GetAvailablePath(Path.Combine(folder, newBaseName + ext));

    try
    {
        File.Move(oldPath, newPath);
        _folderFiles[_currentIndex] = newPath;
        ReapplySort();
    }
    catch
    {
        RefreshFileLabels(oldPath);
    }
}
        // --- Полноэкранный режим ---

        private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        // --- Локализация (EN/RU) ---

        private void LangButton_Click(object sender, RoutedEventArgs e) => Loc.Toggle();

        private void ApplyLocalization()
        {
            LangButton.Content = Loc.Code;

            PhotoModeToggleBtn.Content = Loc.T("Photo", "Фото", "Foto");
            MusicModeToggleBtn.Content = Loc.T("Music", "Музыка", "Música");
            OpenButton.Content = Loc.T("Open", "Открыть", "Abrir");
            RotateButton.Content = Loc.T("Rotate", "Повернуть", "Girar");
            CropButton.Content = _isCropMode
                ? Loc.T("Cancel crop", "Отменить обрезку", "Cancelar recorte")
                : Loc.T("Crop", "Обрезать", "Recortar");
            OcrButton.Content = OcrButton.IsEnabled
                ? Loc.T("Scan text", "Распознать текст", "Reconocer texto")
                : Loc.T("Scanning...", "Распознавание...", "Reconociendo...");
            StripExifButton.Content = Loc.T("Strip EXIF", "Убрать EXIF", "Quitar EXIF");
            BatchRenameButton.Content = Loc.T("Rename all", "Переименовать все", "Renombrar todo");
            DeleteButton.Content = Loc.T("Delete", "Удалить", "Eliminar");
            ConvertToJpgButton.Content = Loc.T("Convert to JPG", "В JPG", "A JPG");
            GridViewButton.Content = Loc.T("Grid", "Сетка", "Cuadrícula");
            FullscreenButton.Content = Loc.T("Fullscreen", "Во весь экран", "Pantalla completa");
            CropSaveButton.Content = Loc.T("Save", "Сохранить", "Guardar");
            CropCancelButton.Content = Loc.T("Cancel", "Отмена", "Cancelar");

            SetComboItemText(SortModeCombo, 0, Loc.T("Name", "Имя", "Nombre"));
            SetComboItemText(SortModeCombo, 1, Loc.T("Date modified", "Дата изменения", "Fecha de modificación"));
            SetComboItemText(SortModeCombo, 2, Loc.T("Size", "Размер", "Tamaño"));
            SetComboItemText(SortModeCombo, 3, Loc.T("Type", "Тип", "Tipo"));

            // Текст-заглушка виден, только пока файл не открыт
            if (_currentIndex < 0)
                FileNameDisplay.Text = Loc.T("No file opened", "Файл не открыт", "Ningún archivo abierto");
        }

        private static void SetComboItemText(ComboBox combo, int index, string text)
        {
            if (index < combo.Items.Count && combo.Items[index] is ComboBoxItem item)
                item.Content = text;
        }

        private void ToggleFullscreen()
{
    _isFullscreen = !_isFullscreen;
    FullscreenRequested?.Invoke(_isFullscreen);

    var visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
    BottomBar.Visibility = visibility;
    PrevButton.Visibility = visibility;
    NextButton.Visibility = visibility;
    UpdateGifButtonVisibility();

    // Кнопки, которые только что скрылись, могли держать фокус клавиатуры —
    // возвращаем фокус на сам PhotoView, иначе клавиатура перестанет отвечать
    Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Input, new Action(() => Focus()));
}

        // --- Открытие файла вручную ---

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                        Filter = "Images|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.webp;*.tiff;*.tif;*.gif;*.heic;*.cr2;*.nef;*.arw"
            };

            if (dialog.ShowDialog() == true)
    {
        PrivacyCleanupService.RemoveFromRecentItems(dialog.FileName);
        OpenFile(dialog.FileName);
    }
        }

        private void SortModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_folderFiles.Count == 0) return;

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
    if (_folderFiles.Count == 0) return;

    _sortDescending = !_sortDescending;
    SortDirectionButton.Content = _sortDescending ? "\u2193" : "\u2191";

    ReapplySort();
}

private void ReapplySort()
{
    string? currentPath = (_currentIndex >= 0 && _currentIndex < _folderFiles.Count)
        ? _folderFiles[_currentIndex]
        : null;

    SortFolderFiles();

    if (currentPath != null)
    {
        _currentIndex = _folderFiles.FindIndex(f =>
            string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase));

        if (_currentIndex >= 0)
        {
            RefreshFileLabels(_folderFiles[_currentIndex]);
        }
    }
}

        // --- Массовое переименование (нумерация) ---

        private void BatchRenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0) return;
            if (ThumbnailOverlay.Visibility == Visibility.Visible) return;
            CommitRename();

            var folder = Path.GetDirectoryName(_folderFiles[_currentIndex])!;

            var dialog = new BatchRenameDialog { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;

            List<BatchRenamePlanItem> plan;
            try
            {
                plan = BatchRenameService.BuildPlan(
                    folder, dialog.Recursive, dialog.StartNumber, dialog.ZeroPadding, SupportedExtensions);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Failed to prepare renaming", "Не удалось подготовить переименование", "No se pudo preparar el renombrado") + $": {ex.Message}",
                    Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (plan.Count == 0)
            {
                MessageBox.Show(Loc.T("Nothing to rename: no photo/GIF files with non-numeric names found.", "Нечего переименовывать: не найдено фото/GIF-файлов с нечисловыми именами.", "Nada que renombrar: no hay fotos/GIF con nombres no numéricos."),
                    Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var example = plan[0];
            var scope = dialog.Recursive ? Loc.T("this folder and its subfolders", "этой папке и её подпапках", "esta carpeta y sus subcarpetas") : Loc.T("this folder", "этой папке", "esta carpeta");
            var confirm = MessageBox.Show(
                Loc.T($"Rename {plan.Count} file(s) in {scope}?", $"Переименовать {plan.Count} файл(ов) в {scope}?", $"¿Renombrar {plan.Count} archivo(s) en {scope}?") + "\n\n" +
                Loc.T("Example", "Пример", "Ejemplo") + $": {Path.GetFileName(example.OldPath)} -> {Path.GetFileName(example.NewPath)}\n\n" +
                Loc.T("Files whose names are already numbers are skipped.\n", "Файлы, чьи имена уже являются числами, пропускаются.\n", "Los archivos cuyos nombres ya son números se omiten.\n") +
                Loc.T("Existing files are never overwritten. This can't be undone automatically.", "Существующие файлы никогда не перезаписываются. Отменить это автоматически нельзя.", "Los archivos existentes nunca se sobrescriben. Esto no se puede deshacer automáticamente."),
                Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var result = BatchRenameService.Execute(plan);

            // Обновляем список файлов и остаёмся на текущем файле (возможно, под новым именем)
            var currentPath = _folderFiles[_currentIndex];
            if (result.OldToNew.TryGetValue(currentPath, out var renamedCurrent))
                currentPath = renamedCurrent;

            _folderFiles = Directory.EnumerateFiles(folder)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            SortFolderFiles();

            _currentIndex = _folderFiles.FindIndex(f =>
                string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase));
            if (_currentIndex < 0 && _folderFiles.Count > 0) _currentIndex = 0;

            if (_currentIndex >= 0) ShowCurrent();
            else ShowNoAccessibleFilesState();

            if (result.ErrorMessage != null)
            {
                MessageBox.Show(
                    Loc.T($"Renamed {result.RenamedCount} of {plan.Count} file(s).", $"Переименовано {result.RenamedCount} из {plan.Count} файл(ов).", $"Renombrados {result.RenamedCount} de {plan.Count} archivo(s).") + $"\n\n{result.ErrorMessage}",
                    Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(Loc.T($"Renamed {result.RenamedCount} file(s).", $"Переименовано файл(ов): {result.RenamedCount}.", $"Renombrados: {result.RenamedCount} archivo(s)."),
                    Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // --- Удаление метаданных (EXIF) ---

        private void StripExifButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < 0) return;
            if (ThumbnailOverlay.Visibility == Visibility.Visible) return;

            var path = _folderFiles[_currentIndex];

            if (!StripExifService.CanStrip(path))
            {
                MessageBox.Show(Loc.T("Metadata removal isn't supported for this file format.", "Удаление метаданных не поддерживается для этого формата файла.", "La eliminación de metadatos no es compatible con este formato de archivo."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var choice = MessageBox.Show(
                Loc.T("Remove all metadata (EXIF, GPS, camera info) from this file?", "Удалить все метаданные (EXIF, GPS, данные камеры) из этого файла?", "¿Eliminar todos los metadatos (EXIF, GPS, datos de la cámara) de este archivo?") + "\n\n" +
                Loc.T("The image will be re-encoded and the original file will be replaced.", "Изображение будет перекодировано, а исходный файл заменён.", "La imagen se recodificará y el archivo original será reemplazado."),
                Loc.T("Strip EXIF", "Удаление EXIF", "Quitar EXIF"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (choice != MessageBoxResult.Yes) return;

            try
            {
                StripExifService.StripMetadata(path);
                ShowCurrent();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Metadata removal failed", "Не удалось удалить метаданные", "Error al eliminar metadatos") + $": {ex.Message}",
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- Обрезка (Crop) ---

        private bool _isCropMode;
        private Rect _cropRectDisplay;   // рамка обрезки в координатах CropOverlay
        private Rect _cropImageBounds;   // границы отображаемого изображения в координатах CropOverlay

        private enum CropDragMode { None, Move, N, S, E, W, NW, NE, SW, SE }
        private CropDragMode _cropDragMode = CropDragMode.None;
        private Point _cropDragStartMouse;
        private Rect _cropDragStartRect;

        private System.Windows.Shapes.Path? _cropDimPath;
        private System.Windows.Shapes.Rectangle? _cropRectShape;
        private System.Windows.Shapes.Line[]? _cropGridLines;
        private System.Windows.Shapes.Rectangle[]? _cropHandles;

        private const double CropHandleSize = 10;
        private const double CropHitRadius = 14;
        private const double CropMinSize = 24;

        private void CropButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCropMode)
            {
                ExitCropMode();
                return;
            }

            if (_currentIndex < 0) return;
            if (ThumbnailOverlay.Visibility == Visibility.Visible) return;

            if (_gifAnimator != null)
            {
                MessageBox.Show(Loc.T("Cropping isn't supported for GIF files.", "Обрезка GIF-файлов не поддерживается.", "El recorte no es compatible con archivos GIF."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MainImage.Source is not BitmapSource) return;

            EnterCropMode();
        }

        private void EnterCropMode()
        {
            _isCropMode = true;
            ResetTransform();
            EnsureCropVisuals();

            CropOverlay.Visibility = Visibility.Visible;
            CropActionsPanel.Visibility = Visibility.Visible;
            CropButton.Content = Loc.T("Cancel crop", "Отменить обрезку", "Cancelar recorte");
            SetCropModeUi(true);

            // Ждём завершения layout, чтобы границы изображения были актуальны
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_isCropMode) return;
                UpdateCropImageBounds();
                _cropRectDisplay = _cropImageBounds;
                UpdateCropVisuals();
            }));
        }

        private void ExitCropMode()
        {
            _isCropMode = false;
            _cropDragMode = CropDragMode.None;
            CropOverlay.ReleaseMouseCapture();
            CropOverlay.Cursor = Cursors.Arrow;
            CropOverlay.Visibility = Visibility.Collapsed;
            CropActionsPanel.Visibility = Visibility.Collapsed;
            CropButton.Content = Loc.T("Crop", "Обрезать", "Recortar");
            SetCropModeUi(false);
            Focus();
        }

        private void SetCropModeUi(bool active)
        {
            bool enabled = !active;
            PhotoModeToggleBtn.IsEnabled = enabled;
            MusicModeToggleBtn.IsEnabled = enabled;
            SortModeCombo.IsEnabled = enabled;
            SortDirectionButton.IsEnabled = enabled;
            OpenButton.IsEnabled = enabled;
            RotateButton.IsEnabled = enabled;
            StripExifButton.IsEnabled = enabled;
            BatchRenameButton.IsEnabled = enabled;
            DeleteButton.IsEnabled = enabled;
            ConvertToJpgButton.IsEnabled = enabled;
            GridViewButton.IsEnabled = enabled;
            FullscreenButton.IsEnabled = enabled;

            var navVisibility = active ? Visibility.Collapsed : Visibility.Visible;
            PrevButton.Visibility = navVisibility;
            NextButton.Visibility = navVisibility;

            if (active) GifPlayPauseButton.Visibility = Visibility.Collapsed;
            else UpdateGifButtonVisibility();
        }

        private void EnsureCropVisuals()
        {
            if (_cropDimPath != null) return;

            _cropDimPath = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                IsHitTestVisible = false
            };
            CropOverlay.Children.Add(_cropDimPath);

            _cropRectShape = new System.Windows.Shapes.Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            CropOverlay.Children.Add(_cropRectShape);

            _cropGridLines = new System.Windows.Shapes.Line[4];
            for (int i = 0; i < 4; i++)
            {
                _cropGridLines[i] = new System.Windows.Shapes.Line
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                CropOverlay.Children.Add(_cropGridLines[i]);
            }

            _cropHandles = new System.Windows.Shapes.Rectangle[8];
            for (int i = 0; i < 8; i++)
            {
                _cropHandles[i] = new System.Windows.Shapes.Rectangle
                {
                    Width = CropHandleSize,
                    Height = CropHandleSize,
                    Fill = Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                CropOverlay.Children.Add(_cropHandles[i]);
            }
        }

        private void UpdateCropImageBounds()
        {
            if (MainImage.Source is not BitmapSource)
            {
                _cropImageBounds = Rect.Empty;
                return;
            }

            // Stretch=Uniform: размер элемента Image совпадает с видимым изображением
            Point topLeft = MainImage.TranslatePoint(new Point(0, 0), CropOverlay);
            _cropImageBounds = new Rect(topLeft,
                new Size(MainImage.ActualWidth, MainImage.ActualHeight));
        }

        private void UpdateCropVisuals()
        {
            if (_cropDimPath == null || _cropRectShape == null ||
                _cropGridLines == null || _cropHandles == null) return;

            var overlayRect = new Rect(0, 0, CropOverlay.ActualWidth, CropOverlay.ActualHeight);
            _cropDimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(overlayRect), new RectangleGeometry(_cropRectDisplay));

            Canvas.SetLeft(_cropRectShape, _cropRectDisplay.X);
            Canvas.SetTop(_cropRectShape, _cropRectDisplay.Y);
            _cropRectShape.Width = Math.Max(0, _cropRectDisplay.Width);
            _cropRectShape.Height = Math.Max(0, _cropRectDisplay.Height);

            // Сетка «правило третей» — как в штатном просмотрщике Windows
            double x1 = _cropRectDisplay.X + _cropRectDisplay.Width / 3;
            double x2 = _cropRectDisplay.X + _cropRectDisplay.Width * 2 / 3;
            double y1 = _cropRectDisplay.Y + _cropRectDisplay.Height / 3;
            double y2 = _cropRectDisplay.Y + _cropRectDisplay.Height * 2 / 3;
            SetCropLine(_cropGridLines[0], x1, _cropRectDisplay.Top, x1, _cropRectDisplay.Bottom);
            SetCropLine(_cropGridLines[1], x2, _cropRectDisplay.Top, x2, _cropRectDisplay.Bottom);
            SetCropLine(_cropGridLines[2], _cropRectDisplay.Left, y1, _cropRectDisplay.Right, y1);
            SetCropLine(_cropGridLines[3], _cropRectDisplay.Left, y2, _cropRectDisplay.Right, y2);

            Point[] centers = GetCropHandleCenters();
            for (int i = 0; i < 8; i++)
            {
                Canvas.SetLeft(_cropHandles[i], centers[i].X - CropHandleSize / 2);
                Canvas.SetTop(_cropHandles[i], centers[i].Y - CropHandleSize / 2);
            }
        }

        private static void SetCropLine(System.Windows.Shapes.Line line,
            double x1, double y1, double x2, double y2)
        {
            line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
        }

        private Point[] GetCropHandleCenters()
        {
            var r = _cropRectDisplay;
            double cx = r.X + r.Width / 2;
            double cy = r.Y + r.Height / 2;
            return new[]
            {
                new Point(r.Left, r.Top),     // NW
                new Point(cx, r.Top),         // N
                new Point(r.Right, r.Top),    // NE
                new Point(r.Right, cy),       // E
                new Point(r.Right, r.Bottom), // SE
                new Point(cx, r.Bottom),      // S
                new Point(r.Left, r.Bottom),  // SW
                new Point(r.Left, cy)         // W
            };
        }

        private CropDragMode GetCropDragModeAt(Point pos)
        {
            var centers = GetCropHandleCenters();
            CropDragMode[] modes =
            {
                CropDragMode.NW, CropDragMode.N, CropDragMode.NE, CropDragMode.E,
                CropDragMode.SE, CropDragMode.S, CropDragMode.SW, CropDragMode.W
            };

            for (int i = 0; i < centers.Length; i++)
            {
                if (Math.Abs(pos.X - centers[i].X) <= CropHitRadius &&
                    Math.Abs(pos.Y - centers[i].Y) <= CropHitRadius)
                    return modes[i];
            }

            return _cropRectDisplay.Contains(pos) ? CropDragMode.Move : CropDragMode.None;
        }

        private static Cursor GetCropCursor(CropDragMode mode) => mode switch
        {
            CropDragMode.NW or CropDragMode.SE => Cursors.SizeNWSE,
            CropDragMode.NE or CropDragMode.SW => Cursors.SizeNESW,
            CropDragMode.N or CropDragMode.S => Cursors.SizeNS,
            CropDragMode.E or CropDragMode.W => Cursors.SizeWE,
            CropDragMode.Move => Cursors.SizeAll,
            _ => Cursors.Arrow
        };

        private void CropOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isCropMode) return;

            var pos = e.GetPosition(CropOverlay);
            _cropDragMode = GetCropDragModeAt(pos);
            if (_cropDragMode == CropDragMode.None) return;

            _cropDragStartMouse = pos;
            _cropDragStartRect = _cropRectDisplay;
            CropOverlay.CaptureMouse();
            e.Handled = true;
        }

        private void CropOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isCropMode) return;

            var pos = e.GetPosition(CropOverlay);

            if (_cropDragMode == CropDragMode.None)
            {
                CropOverlay.Cursor = GetCropCursor(GetCropDragModeAt(pos));
                return;
            }

            double dx = pos.X - _cropDragStartMouse.X;
            double dy = pos.Y - _cropDragStartMouse.Y;
            var r = _cropDragStartRect;
            var bounds = _cropImageBounds;

            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

            if (_cropDragMode == CropDragMode.Move)
            {
                double moveX = Math.Clamp(dx, bounds.Left - r.Left, bounds.Right - r.Right);
                double moveY = Math.Clamp(dy, bounds.Top - r.Top, bounds.Bottom - r.Bottom);
                left += moveX; right += moveX;
                top += moveY; bottom += moveY;
            }
            else
            {
                if (_cropDragMode is CropDragMode.NW or CropDragMode.W or CropDragMode.SW)
                    left = Math.Clamp(r.Left + dx, bounds.Left, right - CropMinSize);
                if (_cropDragMode is CropDragMode.NE or CropDragMode.E or CropDragMode.SE)
                    right = Math.Clamp(r.Right + dx, left + CropMinSize, bounds.Right);
                if (_cropDragMode is CropDragMode.NW or CropDragMode.N or CropDragMode.NE)
                    top = Math.Clamp(r.Top + dy, bounds.Top, bottom - CropMinSize);
                if (_cropDragMode is CropDragMode.SW or CropDragMode.S or CropDragMode.SE)
                    bottom = Math.Clamp(r.Bottom + dy, top + CropMinSize, bounds.Bottom);
            }

            _cropRectDisplay = new Rect(new Point(left, top), new Point(right, bottom));
            UpdateCropVisuals();
        }

        private void CropOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_cropDragMode == CropDragMode.None) return;
            _cropDragMode = CropDragMode.None;
            CropOverlay.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ResizeCropRectByWheel(int delta)
        {
            if (!_isCropMode) return;
            var r = _cropRectDisplay;
            var bounds = _cropImageBounds;
            if (r.Width <= 0 || r.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

            double factor = delta > 0 ? 1 / 1.08 : 1.08;

            // Ограничиваем масштаб так, чтобы пропорции рамки сохранялись:
            // не больше изображения и не меньше минимального размера
            factor = Math.Min(factor, Math.Min(bounds.Width / r.Width, bounds.Height / r.Height));
            factor = Math.Max(factor, Math.Max(CropMinSize / r.Width, CropMinSize / r.Height));

            // Рамка уже упёрлась в предел - менять нечего
            if (Math.Abs(factor - 1) < 1e-9) return;

            double newW = Math.Min(r.Width * factor, bounds.Width);
            double newH = Math.Min(r.Height * factor, bounds.Height);

            // Растём/сжимаемся вокруг центра рамки
            double left = r.X + (r.Width - newW) / 2;
            double top = r.Y + (r.Height - newH) / 2;

            // Если упёрлись в край изображения - сдвигаем рамку внутрь.
            // Min/Max вместо Math.Clamp: из-за погрешности плавающей точки
            // верхняя граница может оказаться на ~1e-13 меньше нижней,
            // и Math.Clamp бросает ArgumentException (min > max)
            left = Math.Max(bounds.Left, Math.Min(left, bounds.Right - newW));
            top = Math.Max(bounds.Top, Math.Min(top, bounds.Bottom - newH));

            _cropRectDisplay = new Rect(left, top, newW, newH);
            UpdateCropVisuals();
        }

        private void CropOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isCropMode) return;

            var oldBounds = _cropImageBounds;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_isCropMode) return;
                UpdateCropImageBounds();
                RemapCropRect(oldBounds);
                UpdateCropVisuals();
            }));
        }

        private void RemapCropRect(Rect oldBounds)
        {
            if (oldBounds.Width <= 0 || oldBounds.Height <= 0 ||
                _cropImageBounds.Width <= 0 || _cropImageBounds.Height <= 0)
            {
                _cropRectDisplay = _cropImageBounds;
                return;
            }

            double nx = (_cropRectDisplay.X - oldBounds.X) / oldBounds.Width;
            double ny = (_cropRectDisplay.Y - oldBounds.Y) / oldBounds.Height;
            double nw = _cropRectDisplay.Width / oldBounds.Width;
            double nh = _cropRectDisplay.Height / oldBounds.Height;

            _cropRectDisplay = new Rect(
                _cropImageBounds.X + nx * _cropImageBounds.Width,
                _cropImageBounds.Y + ny * _cropImageBounds.Height,
                Math.Max(1, nw * _cropImageBounds.Width),
                Math.Max(1, nh * _cropImageBounds.Height));
        }

        private void CropSaveButton_Click(object sender, RoutedEventArgs e) => ApplyCrop();
        private void CropCancelButton_Click(object sender, RoutedEventArgs e) => ExitCropMode();

        private void ApplyCrop()
        {
            if (!_isCropMode || _currentIndex < 0) return;
            if (MainImage.Source is not BitmapSource source) return;
            if (_cropImageBounds.Width <= 0 || _cropImageBounds.Height <= 0) return;

            // Перевод рамки из экранных координат в пиксели исходника
            double sx = source.PixelWidth / _cropImageBounds.Width;
            double sy = source.PixelHeight / _cropImageBounds.Height;

            int px = (int)Math.Round((_cropRectDisplay.X - _cropImageBounds.X) * sx);
            int py = (int)Math.Round((_cropRectDisplay.Y - _cropImageBounds.Y) * sy);
            int pw = (int)Math.Round(_cropRectDisplay.Width * sx);
            int ph = (int)Math.Round(_cropRectDisplay.Height * sy);

            px = Math.Clamp(px, 0, source.PixelWidth - 1);
            py = Math.Clamp(py, 0, source.PixelHeight - 1);
            pw = Math.Clamp(pw, 1, source.PixelWidth - px);
            ph = Math.Clamp(ph, 1, source.PixelHeight - py);

            if (pw == source.PixelWidth && ph == source.PixelHeight)
            {
                // Рамка охватывает всё изображение — обрезать нечего
                ExitCropMode();
                return;
            }

            var path = _folderFiles[_currentIndex];
            bool replaceOriginal;

            if (CropAndSaveService.CanEncodeInPlace(path))
            {
                var choice = MessageBox.Show(
                    Loc.T("Replace the original file?", "Заменить исходный файл?", "¿Reemplazar el archivo original?") + "\n\n" +
                    Loc.T("Yes — overwrite the original", "Да — перезаписать оригинал", "Sí — sobrescribir el original") + "\n" +
                    Loc.T("No — save as a copy", "Нет — сохранить как копию", "No — guardar como copia") + "\n" +
                    Loc.T("Cancel — continue cropping", "Отмена — продолжить обрезку", "Cancelar — seguir recortando"),
                    Loc.T("Save cropped image", "Сохранение обрезки", "Guardar imagen recortada"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (choice == MessageBoxResult.Cancel) return;
                replaceOriginal = choice == MessageBoxResult.Yes;
            }
            else
            {
                var choice = MessageBox.Show(
                    Loc.T("This format can't be re-encoded in place,\nso the crop will be saved as a PNG copy. Continue?", "Этот формат нельзя перекодировать на месте,\nпоэтому обрезка будет сохранена как PNG-копия. Продолжить?", "Este formato no se puede recodificar en el mismo archivo,\nasí que el recorte se guardará como copia PNG. ¿Continuar?"),
                    Loc.T("Save cropped image", "Сохранение обрезки", "Guardar imagen recortada"), MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (choice != MessageBoxResult.OK) return;
                replaceOriginal = false;
            }

            try
            {
                string savedPath = CropAndSaveService.CropAndSave(
                    path, new Int32Rect(px, py, pw, ph), replaceOriginal);

                ExitCropMode();

                if (replaceOriginal)
                {
                    ShowCurrent();
                }
                else
                {
                    // Показываем созданную копию
                    if (!_folderFiles.Contains(savedPath, StringComparer.OrdinalIgnoreCase))
                        _folderFiles.Add(savedPath);

                    SortFolderFiles();
                    _currentIndex = _folderFiles.FindIndex(f =>
                        string.Equals(f, savedPath, StringComparison.OrdinalIgnoreCase));
                    if (_currentIndex < 0) _currentIndex = 0;
                    ShowCurrent();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("Crop failed", "Не удалось обрезать", "Error al recortar") + $": {ex.Message}",
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- Переключение режима ---

        private void PhotoModeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            PhotoModeToggleBtn.IsChecked = true;
            MusicModeToggleBtn.IsChecked = false;
        }

        private void MusicModeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            MusicModeToggleBtn.IsChecked = true;
            ModeSwitchRequested?.Invoke(true);
        }
    }
}