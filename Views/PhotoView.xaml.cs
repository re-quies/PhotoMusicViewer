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

    // Размер ячейки задаёт код (сетка всегда в ThumbnailColumns столбцов):
    // при виртуализации привязаться к панели по имени уже нельзя
    private double _cellSize = 100;
    public double CellSize
    {
        get => _cellSize;
        set
        {
            if (Math.Abs(_cellSize - value) < 0.5) return;
            _cellSize = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CellSize)));
        }
    }

    /// <summary>Миниатюра уже запрошена: при прокрутке не делаем ту же работу дважды.</summary>
    public bool LoadRequested { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Одна строка сетки миниатюр. Виртуализация идёт по строкам, поэтому
/// в папке на тысячи фото создаются только видимые ячейки.
/// </summary>
private sealed class ThumbnailRow
{
    public List<ThumbnailItem> Items { get; } = new();
}
        private readonly ObservableCollection<ThumbnailItem> _thumbnailItems = new();
        private readonly ObservableCollection<ThumbnailRow> _thumbnailRows = new();
        private CancellationTokenSource? _thumbnailLoadCts;
        private string? _gridCurrentFolder;
        private ScrollViewer? _thumbnailScrollViewer;
        private double _thumbnailFirstVisibleRow;
        private double _thumbnailVisibleRows;

        private const int ThumbnailColumns = 11;
        private const int ThumbnailPreloadRows = 2;   // запас строк выше/ниже видимой области
        private const int ThumbnailPixelWidth = 220;

        // Общий ограничитель одновременных декодов миниатюр
        private static readonly SemaphoreSlim ThumbnailDecodeGate =
            new(Math.Max(2, Environment.ProcessorCount));

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
        catch (Exception ex) { AppLog.Debug("PhotoView.GetParentFolder", ex); parent = null; }
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
    catch (Exception ex) { AppLog.Warn("PhotoView.EnumerateDirectories", ex); subfolders = new List<string>(); }

    List<string> imageFiles;
    try
    {
        imageFiles = Directory.EnumerateFiles(folderPath)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
    }
    catch (Exception ex) { AppLog.Warn("PhotoView.EnumerateFiles", ex); imageFiles = new List<string>(); }

    imageFiles = ApplySort(imageFiles);

    foreach (var folder in subfolders)
        _thumbnailItems.Add(new ThumbnailItem { Kind = ItemKind.Folder, Path = folder });

    foreach (var file in imageFiles)
        _thumbnailItems.Add(new ThumbnailItem { Kind = ItemKind.Image, Path = file });

    BuildThumbnailRows();
    ThumbnailItemsControl.ItemsSource = _thumbnailRows;

    _thumbnailLoadCts = new CancellationTokenSource();

    // Сетка открывается на текущем фото, а не в начале папки
    ScrollThumbnailsToCurrent();

    // Грузим только то, что видно: остальное подтянется при прокрутке
    QueueVisibleThumbnails();

    // Первый раз шаблон ещё не построен и высота окна неизвестна -
    // повторяем позиционирование после компоновки
    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
        new Action(() =>
        {
            ScrollThumbnailsToCurrent();
            QueueVisibleThumbnails();
        }));
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
    catch (Exception ex) { AppLog.Debug("PhotoView.SafeWriteTimeTicks", ex); return 0; }
}

// --- Упреждающее декодирование соседних фото ---
// Кэш только в памяти (на диск ничего не пишется, как и кэш миниатюр).
// Запись инвалидируется по времени изменения файла: после Rotate/Crop/Strip EXIF
// или замены файла извне устаревшая копия не используется.
private static readonly object PrefetchLock = new();
private static readonly Dictionary<string, (long WriteTimeTicks, DecodedImage Decoded)> PrefetchCache =
    new(StringComparer.OrdinalIgnoreCase);
private const int PrefetchCacheLimit = 4; // текущие соседи + небольшой запас

/// <summary>Готовое к показу изображение плюс размеры и вес оригинала.</summary>
private sealed class DecodedImage
{
    public BitmapSource Image = null!;
    public long SizeBytes;
    // Размеры оригинала (с учётом EXIF-ориентации). Могут быть больше,
    // чем у Image: для показа фото декодируется с ограничением по стороне.
    public int NaturalWidth;
    public int NaturalHeight;
}

// Ограничение большей стороны при декодировании для показа. Раньше
// 24-мегапиксельный снимок разворачивался в памяти целиком (~96 МБ),
// хотя на экран попадает в разы меньше пикселей.
// Уточняется в конструкторе по размеру экрана.
private static int _decodeSideCap = 3072;

/// <summary>
/// Декодирует фото для показа. maxSide ограничивает большую сторону
/// (0 - без ограничения, полный размер). EXIF-ориентация применяется на лету,
/// сам файл не меняется.
/// </summary>
private static DecodedImage DecodeFullImage(string path, int maxSide)
{
    byte[] fileBytes = File.ReadAllBytes(path);

    int rawWidth = 0, rawHeight = 0, orientation = 1;
    try
    {
        // Заголовки без распаковки пикселей: нужны размер и ориентация
        using var metaStream = new MemoryStream(fileBytes);
        var metaDecoder = BitmapDecoder.Create(metaStream,
            BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var metaFrame = metaDecoder.Frames[0];
        rawWidth = metaFrame.PixelWidth;
        rawHeight = metaFrame.PixelHeight;
        orientation = ExifOrientationService.GetOrientation(metaFrame);
    }
    catch (Exception ex)
    {
        // не удалось прочитать заголовки - декодируем как есть, без ограничения
        AppLog.Debug("PhotoView.ReadImageHeaders", ex);
    }

    // DecodePixelWidth/Height выполняет уменьшение внутри декодера: лишние
    // пиксели не выделяются вообще. Задаём только одну сторону - вторая
    // считается сама, пропорции сохраняются.
    int decodeWidth = 0, decodeHeight = 0;
    if (maxSide > 0 && rawWidth > 0 && rawHeight > 0)
    {
        if (rawWidth >= rawHeight && rawWidth > maxSide) decodeWidth = maxSide;
        else if (rawHeight > rawWidth && rawHeight > maxSide) decodeHeight = maxSide;
    }

    // SafeImageDecoder переживает повреждённый ICC-профиль/метаданные
    // (ArgumentException в ColorContext.GetColorContextsHelper): файл открывается без них
    BitmapSource bmp = SafeImageDecoder.DecodeScaled(fileBytes, decodeWidth, decodeHeight);

    BitmapSource display = ExifOrientationService.ApplyOrientation(bmp, orientation);
    if (display.CanFreeze) display.Freeze();

    // Поворот на 90/270 меняет стороны местами
    bool swapped = orientation is 5 or 6 or 7 or 8;
    int naturalWidth = swapped ? rawHeight : rawWidth;
    int naturalHeight = swapped ? rawWidth : rawHeight;
    if (naturalWidth <= 0 || naturalHeight <= 0)
    {
        naturalWidth = display.PixelWidth;
        naturalHeight = display.PixelHeight;
    }

    return new DecodedImage
    {
        Image = display,
        SizeBytes = fileBytes.LongLength,
        NaturalWidth = naturalWidth,
        NaturalHeight = naturalHeight
    };
}

private static bool TryGetPrefetched(string path, out DecodedImage decoded)
{
    decoded = null!;

    long ticks = SafeWriteTimeTicks(path);
    lock (PrefetchLock)
    {
        if (PrefetchCache.TryGetValue(path, out var entry) && entry.WriteTimeTicks == ticks)
        {
            decoded = entry.Decoded;
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
                var decoded = DecodeFullImage(path, _decodeSideCap);
                if (!decoded.Image.IsFrozen) return; // незамороженный объект нельзя передавать между потоками

                lock (PrefetchLock)
                {
                    // Кэш крошечный, а соседи перечитываются мгновенно -
                    // при переполнении просто начинаем заново
                    if (PrefetchCache.Count >= PrefetchCacheLimit)
                        PrefetchCache.Clear();

                    PrefetchCache[path] = (SafeWriteTimeTicks(path), decoded);
                }
            }
            catch (Exception ex)
            {
                // повреждённый/занятый файл - ошибку пользователь увидит при обычном открытии
                AppLog.Debug("PhotoView.Prefetch", ex, AppLog.Describe(path));
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
        catch (Exception ex)
        {
            // не удалось прочитать ориентацию - покажем миниатюру как есть
            AppLog.Debug("PhotoView.DecodeThumbnail orientation", ex);
        }

        // тот же устойчивый декодер: миниатюра не должна пропадать из-за битого профиля
        BitmapSource bmp = SafeImageDecoder.DecodeScaled(bytes, pixelWidth, 0);

        return ExifOrientationService.ApplyOrientation(bmp, orientation);
    }
    catch (Exception ex)
    {
        AppLog.Debug("PhotoView.DecodeThumbnail", ex);
        return null;
    }
}

/// <summary>Собирает плоский список ячеек в строки по ThumbnailColumns штук.</summary>
private void BuildThumbnailRows()
{
    _thumbnailRows.Clear();

    ThumbnailRow? row = null;
    for (int i = 0; i < _thumbnailItems.Count; i++)
    {
        if (i % ThumbnailColumns == 0)
        {
            row = new ThumbnailRow();
            _thumbnailRows.Add(row);
        }
        row!.Items.Add(_thumbnailItems[i]);
    }

    UpdateThumbnailItemSize();
}

/// <summary>
/// Ставит сетку на строку с текущим фото и по возможности центрирует её,
/// чтобы после перехода из просмотра было видно, где именно в папке находишься.
/// </summary>
private void ScrollThumbnailsToCurrent()
{
    if (_thumbnailRows.Count == 0) return;
    if (_currentIndex < 0 || _currentIndex >= _folderFiles.Count) return;

    // В сетке может быть открыта другая папка - тогда скроллить некуда
    string currentPath = _folderFiles[_currentIndex];
    int index = -1;
    for (int i = 0; i < _thumbnailItems.Count; i++)
    {
        if (_thumbnailItems[i].Kind == ItemKind.Image &&
            string.Equals(_thumbnailItems[i].Path, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            index = i;
            break;
        }
    }

    if (index < 0) return;

    var viewer = _thumbnailScrollViewer ??= FindScrollViewer(ThumbnailItemsControl);
    if (viewer == null) return;

    int targetRow = index / ThumbnailColumns;

    // Прокрутка идёт по строкам (CanContentScroll=True): ViewportHeight - число видимых строк
    double viewportRows = viewer.ViewportHeight > 0 ? viewer.ViewportHeight : _thumbnailVisibleRows;
    double offset = viewportRows > 1
        ? targetRow - Math.Floor((viewportRows - 1) / 2)
        : targetRow;

    double maxOffset = Math.Max(0, _thumbnailRows.Count - Math.Max(1, viewportRows));
    offset = Math.Max(0, Math.Min(offset, maxOffset));

    viewer.ScrollToVerticalOffset(offset);
    _thumbnailFirstVisibleRow = offset;
}

/// <summary>Ищет ScrollViewer в шаблоне сетки: до первой прокрутки события ScrollChanged ещё не было.</summary>
private static ScrollViewer? FindScrollViewer(DependencyObject? root)
{
    if (root == null) return null;
    if (root is ScrollViewer sv) return sv;

    int count = VisualTreeHelper.GetChildrenCount(root);
    for (int i = 0; i < count; i++)
    {
        var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
        if (found != null) return found;
    }

    return null;
}

private void ThumbnailScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
{
    if (e.OriginalSource is ScrollViewer viewer) _thumbnailScrollViewer = viewer;

    // Прокрутка идёт по строкам (CanContentScroll=True): смещение - номер
    // первой видимой строки, ViewportHeight - сколько строк влезает на экран
    _thumbnailFirstVisibleRow = e.VerticalOffset;
    _thumbnailVisibleRows = e.ViewportHeight;

    QueueVisibleThumbnails();
}

/// <summary>
/// Ставит в очередь декодирование миниатюр только для видимых строк (плюс небольшой
/// запас). Раньше при открытии папки декодировались миниатюры всех файлов сразу.
/// </summary>
private void QueueVisibleThumbnails()
{
    if (_thumbnailRows.Count == 0) return;
    if (ThumbnailOverlay.Visibility != Visibility.Visible) return;

    var token = _thumbnailLoadCts?.Token ?? CancellationToken.None;
    if (token.IsCancellationRequested) return;

    int visibleRows = _thumbnailVisibleRows > 0 ? (int)Math.Ceiling(_thumbnailVisibleRows) : 6;
    int firstRow = (int)Math.Floor(_thumbnailFirstVisibleRow);

    int from = Math.Max(0, firstRow - ThumbnailPreloadRows);
    int to = Math.Min(_thumbnailRows.Count - 1, firstRow + visibleRows + ThumbnailPreloadRows);

    for (int r = from; r <= to; r++)
        foreach (var item in _thumbnailRows[r].Items)
            StartThumbnailLoad(item, token);
}

private void StartThumbnailLoad(ThumbnailItem item, CancellationToken token)
{
    if (item.Kind != ItemKind.Image || item.LoadRequested || item.Thumbnail != null) return;

    if (TryGetCachedThumbnail(item.Path, out var cached))
    {
        item.LoadRequested = true;
        item.Thumbnail = cached; // мгновенно, без обращения к диску
        return;
    }

    item.LoadRequested = true;
    _ = LoadThumbnailAsync(item, token);
}

private async Task LoadThumbnailAsync(ThumbnailItem item, CancellationToken token)
{
    try
    {
        // Продолжения возвращаются в UI-поток, поэтому Thumbnail ставим напрямую
        await ThumbnailDecodeGate.WaitAsync(token);
        try
        {
            if (token.IsCancellationRequested) return;

            var thumb = await Task.Run(() => DecodeThumbnail(item.Path, ThumbnailPixelWidth), token);
            if (thumb == null || token.IsCancellationRequested) return;

            CacheThumbnail(item.Path, thumb);
            item.Thumbnail = thumb;
        }
        finally
        {
            ThumbnailDecodeGate.Release();
        }
    }
    catch (Exception ex)
    {
        // отмена или ошибка - повторим, если ячейка снова окажется видимой
        AppLog.Debug("PhotoView.LoadThumbnail", ex, AppLog.Describe(item.Path));
        item.LoadRequested = false;
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
    // 24 - поля сетки и место под полосу прокрутки, 4 - отступы ячейки (Margin="2")
    double available = ThumbnailOverlay.ActualWidth - 24;
    if (available <= 0) return;

    double itemSize = available / ThumbnailColumns - 4;
    if (itemSize <= 0) return;

    foreach (var item in _thumbnailItems)
        item.CellSize = itemSize;
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
        private bool _sortDescending = true;
        private long _currentFileSizeBytes;
        private int _currentWidth;
        private int _currentHeight;
        private GifAnimator? _gifAnimator;

        // Асинхронный показ текущего фото: отмена предыдущей загрузки плюс защита
        // от запоздавшего результата, если пользователь успел пролистать дальше
        private CancellationTokenSource? _imageLoadCts;
        private int _showGeneration;

        // Распознавание текста и копирование в буфер тоже уходят в фон:
        // и то, и другое раньше декодировало снимок прямо в UI-потоке
        private CancellationTokenSource? _ocrCts;
        private bool _clipboardCopyInProgress;
        private int _displayPixelWidth;   // ширина показанной копии в пикселях
        private bool _fullResRequested;

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

    // Потолок декодирования по размеру экрана: с запасом на зум и HiDPI,
    // но без холостых сотен мегабайт на каждый снимок
    try
    {
        double screenSide = Math.Max(SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);
        if (screenSide > 0)
            _decodeSideCap = (int)Math.Clamp(Math.Round(screenSide * 1.5), 2560, 3840);
    }
    catch (Exception ex)
    {
        // не удалось узнать размер экрана - остаётся значение по умолчанию
        AppLog.Debug("PhotoView.DetectScreenSize", ex);
    }

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

    // Сбрасываем сортировку на "Дата изменения, по убыванию" (новые первыми) для каждой новой открытой папки
    _sortMode = SortMode.DateModified;
    _sortDescending = true;
    SortModeCombo.SelectedIndex = 1;
    SortDirectionButton.Content = "\u2193";

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
    catch (Exception ex) { AppLog.Debug("PhotoView.SafeFileLength", ex); return 0; }
}
        private void ShowCurrent()
        {
            if (_currentIndex < 0 || _currentIndex >= _folderFiles.Count) return;

            var path = _folderFiles[_currentIndex];

            // Прошлая загрузка больше не нужна: при быстром листании фоновые
            // декоды отменяются и не занимают процессор
            _imageLoadCts?.Cancel();
            _imageLoadCts = new CancellationTokenSource();
            var token = _imageLoadCts.Token;
            int generation = ++_showGeneration;

            // Распознавание предыдущего снимка тоже больше не нужно
            _ocrCts?.Cancel();

            _gifAnimator?.Stop();
            _gifAnimator = null;
            ResetTransform();

            _fullResRequested = false;
            _displayPixelWidth = 0;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            ConvertToJpgButton.Visibility = ext == ".webp" ? Visibility.Visible : Visibility.Collapsed;

            _currentWidth = 0;
            _currentHeight = 0;
            _currentFileSizeBytes = SafeFileLength(path);

            if (ext == ".gif")
            {
                MainImage.Source = null;
                ShowLoadingLabels(path);
                _ = LoadGifAsync(path, generation, token);
                return;
            }

            // Упреждающе декодированная копия ставится сразу - листание остаётся мгновенным
            if (TryGetPrefetched(path, out var prefetched))
            {
                ApplyDecodedImage(prefetched, path);
                PrefetchNeighbors();
                return;
            }

            // Иначе декодируем в фоновом потоке: интерфейс не замирает даже
            // на 50-мегапиксельном снимке или медленном диске
            MainImage.Source = null;
            ShowLoadingLabels(path);
            _ = LoadImageAsync(path, generation, token);
        }

        /// <summary>Пока фото декодируется, вместо размеров показываем «Загрузка».</summary>
        private void ShowLoadingLabels(string path)
        {
            FileNameDisplay.Text = Path.GetFileName(path);
            FileMetaText.Text = Loc.T("Loading\u2026", "Загрузка\u2026", "Cargando\u2026") +
                $"   [{_currentIndex + 1}/{_folderFiles.Count}]";
            UpdateGifButtonVisibility();
        }

        private void ApplyDecodedImage(DecodedImage decoded, string path)
        {
            MainImage.Source = decoded.Image;
            _displayPixelWidth = decoded.Image.PixelWidth;
            _currentFileSizeBytes = decoded.SizeBytes;
            _currentWidth = decoded.NaturalWidth;
            _currentHeight = decoded.NaturalHeight;

            UpdateGifButtonVisibility();
            RefreshFileLabels(path);
        }

/// <summary>
/// Декодирует фото в фоновом потоке. Поколение и токен защищают от гонки:
/// результат применяется только если пользователь всё ещё смотрит этот файл.
/// </summary>
private async Task LoadImageAsync(string path, int generation, CancellationToken token,
    SniffedFormat disguisedAs = SniffedFormat.Unknown)
{
    try
    {
        var decoded = await Task.Run(() => DecodeFullImage(path, _decodeSideCap), token);
        if (token.IsCancellationRequested || generation != _showGeneration) return;

        ApplyDecodedImage(decoded, path);

        // Фоном декодируем соседние фото, чтобы следующее листание было мгновенным
        PrefetchNeighbors();
    }
    catch (OperationCanceledException)
    {
        // пользователь пролистал дальше - штатный сценарий
        AppLog.Debug("PhotoView.ShowCurrent отменено");
    }
    catch (Exception ex)
    {
        AppLog.Error("PhotoView.ShowCurrent", ex, AppLog.Describe(path));
        if (generation != _showGeneration) return;

        // disguisedAs заполнено, когда сюда пришёл «гиф», оказавшийся другим форматом:
        // сообщение договаривает, что внутри на самом деле
        ShowOpenError(path, ex, disguisedAs);
    }
}

/// <summary>Собирает кадры GIF в фоне: интерфейс не блокируется на тяжёлых анимациях.</summary>
private async Task LoadGifAsync(string path, int generation, CancellationToken token)
{
    try
    {
        var animator = await GifAnimator.LoadAsync(path, MainImage, token);
        if (token.IsCancellationRequested || generation != _showGeneration)
        {
            animator.Stop();
            return;
        }

        _gifAnimator = animator;
        _gifAnimator.Start();
        GifPlayPauseButton.Content = "\u2759\u2759";

        // Первый кадр уже установлен аниматором - берём размеры из него
        if (MainImage.Source is BitmapSource gifSource)
        {
            _currentWidth = gifSource.PixelWidth;
            _currentHeight = gifSource.PixelHeight;
            _displayPixelWidth = gifSource.PixelWidth;
        }

        UpdateGifButtonVisibility();
        RefreshFileLabels(path);
    }
    catch (OperationCanceledException)
    {
        // отмена при листании - штатный сценарий
        AppLog.Debug("PhotoView.LoadGif отменено");
    }
    catch (GifAnimator.GifUnsupportedException ex)
    {
        // Файл назван .gif, но внутри другой формат (WebP/PNG/JPEG/MP4...) либо GIF оборван
        // или повреждён. Это не ошибка приложения, поэтому Warn, а не Error
        AppLog.Warn("PhotoView.LoadGif", ex,
            $"{AppLog.Describe(path)}, actual: {ex.ActualFormat}");
        if (token.IsCancellationRequested || generation != _showGeneration) return;

        // Видео, архив, SVG и т.п. показать нечем, а DecodeFullImage читает файл целиком
        // в память: на многогигабайтном видео это хуже самой ошибки. Отсекаем сразу
        if (!ImageFormatSniffer.CanTryStaticImage(ex.ActualFormat))
        {
            ShowOpenError(path, ex, ex.ActualFormat);
            return;
        }

        // Остальное открываем обычным путём: там уже есть выбор кодека по содержимому,
        // EXIF-ориентация и уменьшение внутри декодера. Оборванный GIF покажется первым кадром
        await LoadImageAsync(path, generation, token, ex.ActualFormat);
    }
    catch (Exception ex)
    {
        // Отдельное имя операции: сбой аниматора не должен быть неотличим от сбоя обычного показа
        AppLog.Error("PhotoView.LoadGif", ex, AppLog.Describe(path));
        if (generation != _showGeneration) return;
        ShowOpenError(path, ex);
    }
}

private void ShowOpenError(string path, Exception ex, SniffedFormat actualFormat = SniffedFormat.Unknown)
{
    string failed = Loc.T("Failed to open", "Не удалось открыть", "No se pudo abrir");
    string name = Path.GetFileName(path);

    // Файл выдаёт себя не за тот формат (например, MP4 под именем .gif): говорим, что внутри.
    // Gif и Unknown сюда не попадают: «на самом деле GIF» ничего не объясняет
    if (actualFormat != SniffedFormat.Unknown && actualFormat != SniffedFormat.Gif)
    {
        string actual = string.Format(
            Loc.T("it is actually {0}", "на самом деле это {0}", "en realidad es {0}"),
            ImageFormatSniffer.Describe(actualFormat));

        // Для видео/архива причина исчерпана сама по себе; если же картинку не смог открыть
        // декодер (например, нет кодека HEIC), его сообщение тоже полезно
        FileNameDisplay.Text = ex is GifAnimator.GifUnsupportedException
            ? $"{failed}: {name} — {actual}"
            : $"{failed}: {name} — {actual} ({ex.Message})";
    }
    else
    {
        FileNameDisplay.Text = $"{failed}: {name} ({ex.Message})";
    }

    FileMetaText.Text = "";
}

/// <summary>
/// Если пользователь приблизил сильнее, чем позволяет уменьшенная копия,
/// один раз подгружаем фото в полном разрешении - резкость не теряется.
/// </summary>
private void MaybeUpgradeToFullResolution(double scale)
{
    if (_fullResRequested) return;
    if (_gifAnimator != null) return;
    if (_currentIndex < 0 || _currentIndex >= _folderFiles.Count) return;
    if (_displayPixelWidth <= 0 || _currentWidth <= _displayPixelWidth) return;
    if (MainImage.ActualWidth <= 0) return;

    // Запас 5%: не дёргаемся на границе
    if (MainImage.ActualWidth * scale <= _displayPixelWidth * 1.05) return;

    _fullResRequested = true;
    _ = LoadFullResolutionAsync(_folderFiles[_currentIndex], _showGeneration);
}

private async Task LoadFullResolutionAsync(string path, int generation)
{
    try
    {
        var decoded = await Task.Run(() => DecodeFullImage(path, 0));
        if (generation != _showGeneration) return;

        MainImage.Source = decoded.Image;
        _displayPixelWidth = decoded.Image.PixelWidth;
    }
    catch (Exception ex)
    {
        // остаётся уменьшенная копия - показ не ломается
        AppLog.Debug("PhotoView.UpgradeToFullRes", ex);
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
            if (_currentIndex >= 0) _ = CopyCurrentImageToClipboardAsync();
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

    // При сильном приближении подгружается полное разрешение
    MaybeUpgradeToFullResolution(newScale);
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
    catch (Exception ex)
    {
        // перетаскивание прервано/не удалось - окно не показываем, но пишем в журнал
        AppLog.Warn("PhotoView.StartImageDrag", ex);
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

            // Повторное нажатие или переход к другому фото отменяют прошлый запуск
            _ocrCts?.Cancel();
            _ocrCts = new CancellationTokenSource();
            var ocrToken = _ocrCts.Token;

            OcrButton.IsEnabled = false;
            OcrButton.Content = Loc.T("Scanning...", "Распознавание...", "Reconociendo...");

            try
            {
                var found = new List<OcrService.OcrVariant>();
                string? ocrError = null;

                try
                {
                    var variants = await OcrService.RecognizeTextAsync(path, ocrToken);
                    found = variants.Where(v => !string.IsNullOrWhiteSpace(v.Text)).ToList();
                }
                catch (OperationCanceledException)
                {
                    // Пользователь ушёл на другой снимок - окно не показываем
                    AppLog.Debug("PhotoView.Ocr отменено");
                    return;
                }
                catch (Exception ocrEx)
                {
                    AppLog.Warn("OcrService.RecognizeTextAsync", ocrEx);
                    // Локальный OCR может быть недоступен (нет языковых пакетов Windows) —
                    // это не повод закрывать окно: фото можно прочитать через Google.
                    ocrError = ocrEx.Message;
                }

                if (ocrError != null)
                {
                    MessageBox.Show(Loc.T("Local text recognition is unavailable", "Локальное распознавание недоступно", "El reconocimiento local no está disponible")
                        + ": " + ocrError + Environment.NewLine + Environment.NewLine
                        + Loc.T("You can still send the photo to Google or Qwen.", "Фото всё равно можно отправить в Google или Qwen.", "Aún puede enviar la foto a Google o Qwen."),
                        "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Окно открываем всегда: даже если текст не найден, фото можно отправить в Google
                var window = new OcrResultWindow(found, path) { Owner = Window.GetWindow(this) };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLog.Error("PhotoView.OcrButton_Click", ex);
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
            catch (NotSupportedException ex)
            {
                AppLog.Info("PhotoView.RotateCurrent", ex);
                MessageBox.Show(
                    Loc.T("Rotation saving isn't supported for this file format.", "Сохранение поворота не поддерживается для этого формата файла.", "Guardar el giro no es compatible con este formato de archivo."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error("PhotoView.RotateCurrent", ex);
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
        AppLog.Error("PhotoView.ConvertToJpg", ex);
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
    catch (Exception ex)
    {
        // Удаление не удалось (например, файл занят другим процессом) —
        // окно не показываем, но причина теперь видна в журнале
        AppLog.Warn("PhotoView.DeleteCurrentFile", ex, AppLog.Describe(path));
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
        _ = CopyCurrentImageToClipboardAsync();
        e.Handled = true;
    }
}
private async Task CopyCurrentImageToClipboardAsync()
{
    // Второе нажатие Ctrl+C во время работы не запускает параллельное декодирование
    if (_clipboardCopyInProgress) return;
    if (MainImage.Source is not BitmapSource bitmapSource) return;

    _clipboardCopyInProgress = true;
    try
    {
        // Если на экране уменьшенная копия, в буфер кладётся полный размер.
        // Полноразмерный снимок - это сотни мегабайт и секунды работы, поэтому
        // декодирование идёт в фоне: раньше окно на это время замирало.
        if (_gifAnimator == null && _currentIndex >= 0 && _currentIndex < _folderFiles.Count &&
            _displayPixelWidth > 0 && _currentWidth > _displayPixelWidth)
        {
            var path = _folderFiles[_currentIndex];
            try
            {
                var fullSize = await Task.Run(() =>
                {
                    var image = DecodeFullImage(path, 0).Image;
                    // Передать BitmapSource в UI-поток можно только замороженным
                    if (image.CanFreeze) image.Freeze();
                    return image;
                });

                // Пока шло декодирование, пользователь мог пролистать дальше
                if (_currentIndex >= 0 && _currentIndex < _folderFiles.Count &&
                    string.Equals(_folderFiles[_currentIndex], path, StringComparison.OrdinalIgnoreCase))
                    bitmapSource = fullSize;
            }
            catch (Exception ex) { AppLog.Warn("PhotoView.ClipboardFullDecode", ex); /* кладём то, что уже показано */ }
        }

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

        // Буфер обмена - общий ресурс: пока его держит другое приложение, запись
        // не проходит. Windows советует повторить попытку через короткую паузу.
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (Exception ex) when (attempt < 5)
            {
                AppLog.Debug("PhotoView.Clipboard повтор", ex, $"попытка {attempt}");
                await Task.Delay(60);
            }
            catch (Exception ex)
            {
                // окно не показываем, но после пяти попыток запись остаётся в журнале
                AppLog.Warn("PhotoView.Clipboard", ex);
                return;
            }
        }
    }
    finally
    {
        _clipboardCopyInProgress = false;
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
    catch (Exception ex)
    {
        AppLog.Warn("PhotoView.CommitRename", ex, AppLog.Describe(oldPath));
        RefreshFileLabels(oldPath);
    }
}
        // --- Полноэкранный режим ---

        private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        // --- Локализация (EN/RU) ---

        private void LangButton_Click(object sender, RoutedEventArgs e) => Loc.Toggle();

        /// <summary>Открывает меню настроек перевода (ключи API, модель, промты, языки).</summary>
        private void TranslateSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = new TranslationSettingsWindow { Owner = Window.GetWindow(this) };
            settings.ShowDialog();
        }

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
            TranslateSettingsButton.Content = Loc.T("Translation…", "Перевод…", "Traducción…");
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
                AppLog.Error("PhotoView.BatchRename prepare", ex);
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
                AppLog.Error("PhotoView.StripMetadata", ex);
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

            // На экране может показываться уменьшенная копия, а обрезается
            // оригинальный файл - пересчёт идёт в пикселях оригинала
            int sourceWidth = _currentWidth > 0 ? _currentWidth : source.PixelWidth;
            int sourceHeight = _currentHeight > 0 ? _currentHeight : source.PixelHeight;

            // Перевод рамки из экранных координат в пиксели исходника
            double sx = sourceWidth / _cropImageBounds.Width;
            double sy = sourceHeight / _cropImageBounds.Height;

            int px = (int)Math.Round((_cropRectDisplay.X - _cropImageBounds.X) * sx);
            int py = (int)Math.Round((_cropRectDisplay.Y - _cropImageBounds.Y) * sy);
            int pw = (int)Math.Round(_cropRectDisplay.Width * sx);
            int ph = (int)Math.Round(_cropRectDisplay.Height * sy);

            px = Math.Clamp(px, 0, sourceWidth - 1);
            py = Math.Clamp(py, 0, sourceHeight - 1);
            pw = Math.Clamp(pw, 1, sourceWidth - px);
            ph = Math.Clamp(ph, 1, sourceHeight - py);

            if (pw == sourceWidth && ph == sourceHeight)
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
                AppLog.Error("PhotoView.CropAndSave", ex);
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