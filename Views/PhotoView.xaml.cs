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

    /// <summary>Сторона, с которой декодирована текущая миниатюра (для HiDPI/ресайза окна).</summary>
    public int ThumbnailSide { get; set; }

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
        // Сторона декодирования миниатюры — по фактическому размеру ячейки в пикселях
        // экрана (с учётом масштаба Windows), а не фиксированные 220 px: на 4K/200%
        // ячейка ~400 px, и 220-пиксельная миниатюра растягивалась вдвое.
        // Значения округляются вверх до ступеней, чтобы кэш переиспользовался.
        private static readonly int[] ThumbnailSides = { 160, 220, 320, 440, 640 };
        private int _thumbnailDecodeSide = 220;
        private static bool IsThumbnailSide(int side) => side > 0 && side <= ThumbnailDiskCache.MaxSide;

        // Общий ограничитель одновременных декодов миниатюр


        private void GridViewButton_Click(object sender, RoutedEventArgs e)
{
    if (ThumbnailOverlay.Visibility == Visibility.Visible)
        CloseThumbnailGrid();
    else
        OpenThumbnailGrid();
}

private void OpenThumbnailGrid()
{
    CancelMirrorButtonGesture();
    if (_fileOperation != null) return;
    string? currentFolder = _currentIndex >= 0 && _currentIndex < _folderFiles.Count
        ? Path.GetDirectoryName(_folderFiles[_currentIndex]) : _gridCurrentFolder;
    if (string.IsNullOrEmpty(currentFolder) || !Directory.Exists(currentFolder)) return;
    ThumbnailOverlay.Visibility = Visibility.Visible;
    LoadGridFolder(currentFolder);
}
private int _gridGeneration;
private Dictionary<string, FolderEntry> _gridMeta = new(StringComparer.OrdinalIgnoreCase);
private CancellationTokenSource? _gridScanCts;
private bool _gridFolderLoading;

/// <summary>
/// Открывает папку в сетке. Листинг и сортировка идут в фоне: на сетевой папке или
/// каталоге с тысячами файлов окно больше не замирает. Пока список собирается,
/// видна только кнопка «назад».
/// </summary>
private void LoadGridFolder(string folderPath) => _ = LoadGridFolderAsync(folderPath);
private async Task LoadGridFolderAsync(string folderPath)
{
    if (_fileOperation != null) return;
    _gridScanCts?.Cancel(); _gridScanCts?.Dispose();
    _gridScanCts = new CancellationTokenSource();
    var token = _gridScanCts.Token;
    _gridFolderLoading = true;
    _thumbnailLoadCts?.Cancel();
    _visibleThumbnailCts?.Cancel();
    _gridCurrentFolder = folderPath;
    UpdateGridFolderStatus();
    int generation = ++_gridGeneration;
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
    BuildThumbnailRows();
    ThumbnailItemsControl.ItemsSource = _thumbnailRows;

    var mode = _sortMode; bool descending = _sortDescending;
    List<string> subfolders, imageFiles;
    Dictionary<string, FolderEntry> meta;
    try
    {
        (subfolders, imageFiles, meta) = await Task.Run(() =>
        {
            List<string> dirs;
            try { dirs = FolderScanner.ScanDirectories(folderPath, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("PhotoView.EnumerateDirectories", ex); dirs = new List<string>(); }
            List<FolderEntry> entries;
            try { entries = FolderScanner.ScanFiles(folderPath, IsSupportedImagePath, token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Warn("PhotoView.EnumerateFiles", ex); entries = new List<FolderEntry>(); }
            token.ThrowIfCancellationRequested();
            ImageSaveWriter.IndexFolder(folderPath);
            token.ThrowIfCancellationRequested();
            var lookup = FolderScanner.ToLookup(entries);
            return (dirs, SortFileSnapshot(entries.Select(e => e.Path).ToList(), mode, descending, lookup), lookup);
        }, token);
    }
    catch (OperationCanceledException) { if (generation == _gridGeneration) _gridFolderLoading = false; return; }
    catch (Exception ex)
    {
        AppLog.Warn("PhotoView.LoadGridFolder", ex);
        subfolders = new List<string>(); imageFiles = new List<string>(); meta = new(StringComparer.OrdinalIgnoreCase);
    }

    // Пока шёл листинг, пользователь мог закрыть сетку или уйти в другую папку
    if (generation != _gridGeneration || token.IsCancellationRequested || ThumbnailOverlay.Visibility != Visibility.Visible) return;
    _gridFolderLoading = false;
    if (mode != _sortMode || descending != _sortDescending)
        imageFiles = SortFileSnapshot(imageFiles, _sortMode, _sortDescending, meta);
    _gridMeta = meta;

    foreach (var folder in subfolders)
        _thumbnailItems.Add(new ThumbnailItem { Kind = ItemKind.Folder, Path = folder });

    foreach (var file in imageFiles)
        _thumbnailItems.Add(new ThumbnailItem { Kind = ItemKind.Image, Path = file });

    UpdateGridFolderStatus();
    BuildThumbnailRows();
    ThumbnailItemsControl.ItemsSource = _thumbnailRows;

    _thumbnailLoadCts = new CancellationTokenSource();

    // Сетка открывается на текущем фото, а не в начале папки
    ScrollThumbnailsToCurrent();

    // Грузим только то, что видно: остальное подтянется при прокрутке
    QueueVisibleThumbnails();

    // Первый раз шаблон ещё не построен и высота окна неизвестна -
    // повторяем позиционирование после компоновки
    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
        new Action(() =>
        {
            if (generation != _gridGeneration) return;
            ScrollThumbnailsToCurrent();
            QueueVisibleThumbnails();
        }));
}
private void UpdateGridFolderStatus()
{
    if (GridFolderStatus == null || string.IsNullOrEmpty(_gridCurrentFolder)) return;
    int count = _thumbnailItems.Count(i => i.Kind == ItemKind.Image);
    GridFolderStatus.Text = _gridCurrentFolder + "\n" + (_gridFolderLoading
        ? Loc.T("Loading…", "Загрузка…", "Cargando…")
        : count == 0
            ? Loc.T("No supported photos in this folder.", "В этой папке нет поддерживаемых фотографий.", "No hay fotos compatibles en esta carpeta.")
            : Loc.T($"Photos: {count}", $"Фотографий: {count}", $"Fotos: {count}"));
}
private void CloseThumbnailGrid()
{
    ++_gridGeneration;
    _gridFolderLoading = false;
    _gridScanCts?.Cancel(); _gridScanCts?.Dispose(); _gridScanCts = null;
    _thumbnailLoadCts?.Cancel(); _visibleThumbnailCts?.Cancel();
    foreach (var item in _thumbnailItems) { item.Thumbnail = null; item.LoadRequested = false; }
    ThumbnailOverlay.Visibility = Visibility.Collapsed;
}

// Общая bounded-очередь и LRU для просмотра, соседей и миниатюр.
// Несколько потоков декодирования (ядра − 1, от 2 до 4); один всегда оставлен под то,
// что пользователь ждёт сейчас. Кэш — по объёму памяти машины: полноразмерный
// 24-Мп снимок (96 МБ в BGRA32) теперь в него помещается, повторный зум не декодирует заново.
private static readonly ImageLoadCoordinator<DecodedImage> ImageLoads = new(
    DecodeFullImage, image => image.EstimatedBytes, budget: ImageCacheBudget(), maxPending: 24,
    workers: ImageLoadCoordinator<DecodedImage>.DefaultWorkerCount);

/// <summary>Бюджет кэша изображений: десятая часть доступной памяти, 256–768 МиБ
/// (в 32-битном процессе — 192 МиБ: адресное пространство всего 2–4 ГБ).</summary>
internal static long ImageCacheBudget()
{
    const long MiB = 1024L * 1024;
    if (!Environment.Is64BitProcess) return 192 * MiB;
    long total = 0;
    try { total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
    catch (Exception ex) { AppLog.Debug("PhotoView.ImageCacheBudget", ex); }
    return total <= 0 ? 256 * MiB : Math.Clamp(total / 10, 256 * MiB, 768 * MiB);
}
private CancellationTokenSource? _prefetchCts;

/// <summary>Готовое к показу изображение плюс размеры и вес оригинала.</summary>
private sealed class DecodedImage
{
    public BitmapSource Image = null!;
    public long SizeBytes;
    // Размеры оригинала (с учётом EXIF-ориентации). Могут быть больше,
    // чем у Image: для показа фото декодируется с ограничением по стороне.
    public int NaturalWidth;
    public int NaturalHeight;
    public long EstimatedBytes;
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
private static DecodedImage DecodeFullImage(string path, int maxSide, CancellationToken token = default)
{
    bool thumbnail = IsThumbnailSide(maxSide);
    // Миниатюра из дискового кэша (если пользователь его включил)
    if (thumbnail && ThumbnailDiskCache.TryLoad(path, maxSide, token) is { } cached)
        return new DecodedImage { Image = cached.Image, SizeBytes = cached.FileBytes,
            NaturalWidth = cached.NaturalWidth, NaturalHeight = cached.NaturalHeight,
            EstimatedBytes = PixelBytes(cached.Image) };

    // Пиксели копируются в собственный BGRA32-буфер внутри декодера (в пределах общего
    // бюджета памяти): вес в кэше — фактический (ширина × высота × 4), а не прежние
    // «16 байт на пиксель оригинала», из-за которых 24 Мп (384 МБ по оценке) не
    // помещались в кэш и каждое приближение декодировало файл заново.
    var decoded = SafeImageDecoder.LoadDecoded(path, maxSide, token: token,
        preferEmbeddedThumbnail: thumbnail, detachToBgra32: true);
    if (thumbnail)
        ThumbnailDiskCache.Store(path, maxSide, decoded.Image, decoded.NaturalWidth, decoded.NaturalHeight, decoded.FileBytes);
    return new DecodedImage { Image = decoded.Image, SizeBytes = decoded.FileBytes,
        NaturalWidth = decoded.NaturalWidth, NaturalHeight = decoded.NaturalHeight,
        EstimatedBytes = PixelBytes(decoded.Image) };
}

private static long PixelBytes(BitmapSource image) =>
    checked((long)image.PixelWidth * image.PixelHeight * Math.Max(4, (image.Format.BitsPerPixel + 7) / 8) + 1024);

private void CancelPrefetch()
{
    _prefetchCts?.Cancel(); _prefetchCts?.Dispose(); _prefetchCts = null;
}
/// <summary>Сколько соседних фото готовить заранее по направлению листания и назад.</summary>
private const int PrefetchAhead = 2, PrefetchBehind = 1;
private bool _lastNavigationForward = true;

private void PrefetchNeighbors()
{
    CancelPrefetch();
    if (_currentIndex < 0 || _folderFiles.Count < 2 || _fileOperation != null) return;
    _prefetchCts = new CancellationTokenSource();
    var token = _prefetchCts.Token;
    // Порядок важен: очередь отдаёт фоновые задания по порядку, ближайшее по ходу — первым
    int step = _lastNavigationForward ? 1 : -1, count = _folderFiles.Count;
    var order = new List<int>();
    for (int i = 1; i <= PrefetchAhead; i++) order.Add(_currentIndex + step * i);
    for (int i = 1; i <= PrefetchBehind; i++) order.Add(_currentIndex - step * i);
    foreach (string path in order.Select(i => _folderFiles[((i % count) + count) % count])
                 .Where(p => !string.Equals(p, _folderFiles[_currentIndex], StringComparison.OrdinalIgnoreCase))
                 .Distinct(StringComparer.OrdinalIgnoreCase))
        if (!string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
            _ = PrefetchOneAsync(path, token);
}
private static async Task PrefetchOneAsync(string path, CancellationToken token)
{
    try { await ImageLoads.RequestAsync(path, _decodeSideCap, foreground: false, token).ConfigureAwait(false); }
    catch (OperationCanceledException) { }
    catch (Exception ex) { AppLog.Debug("PhotoView.Prefetch", ex); }
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
private CancellationTokenSource? _visibleThumbnailCts;
private void QueueVisibleThumbnails()
{
    _visibleThumbnailCts?.Cancel(); _visibleThumbnailCts?.Dispose(); _visibleThumbnailCts = null;
    if (_thumbnailRows.Count == 0 || ThumbnailOverlay.Visibility != Visibility.Visible) return;
    var gridToken = _thumbnailLoadCts?.Token ?? new CancellationToken(true);
    if (gridToken.IsCancellationRequested) return;
    _visibleThumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(gridToken);
    int visibleRows = _thumbnailVisibleRows > 0 ? (int)Math.Ceiling(_thumbnailVisibleRows) : 6;
    int firstRow = (int)Math.Floor(_thumbnailFirstVisibleRow);
    int from = Math.Max(0, firstRow - ThumbnailPreloadRows);
    int to = Math.Min(_thumbnailRows.Count - 1, firstRow + visibleRows + ThumbnailPreloadRows);
    // Сначала видимые строки, потом запас сверху и снизу
    int lastVisible = Math.Min(_thumbnailRows.Count - 1, firstRow + visibleRows);
    var ordered = new List<ThumbnailItem>();
    for (int r = Math.Max(0, firstRow); r <= lastVisible; r++) ordered.AddRange(_thumbnailRows[r].Items);
    for (int r = from; r <= to; r++) if (r < firstRow || r > lastVisible) ordered.AddRange(_thumbnailRows[r].Items);
    var wanted = new HashSet<ThumbnailItem>(ordered);
    // Binding references must not retain every thumbnail ever scrolled past outside the LRU budget.
    foreach (var item in _thumbnailItems)
        if (!wanted.Contains(item)) { item.Thumbnail = null; item.LoadRequested = false; item.ThumbnailSide = 0; }
    _ = LoadVisibleThumbnailsAsync(ordered, _thumbnailDecodeSide, _visibleThumbnailCts.Token);
}
private async Task LoadVisibleThumbnailsAsync(IReadOnlyList<ThumbnailItem> items, int side, CancellationToken token)
{
    // Один производитель на видимую область, но несколько миниатюр в работе сразу —
    // по числу фоновых потоков очереди (раньше строго по одной).
    int parallel = Math.Max(1, ImageLoads.Workers - 1);
    var running = new List<Task>(parallel);
    foreach (var item in items)
    {
        if (token.IsCancellationRequested) break;
        if (item.Kind != ItemKind.Image || (item.Thumbnail != null && item.ThumbnailSide >= side)) continue;
        item.LoadRequested = true;
        running.Add(LoadThumbnailAsync(item, side, token));
        if (running.Count >= parallel) running.Remove(await Task.WhenAny(running));
    }
    await Task.WhenAll(running);
}

private async Task LoadThumbnailAsync(ThumbnailItem item, int side, CancellationToken token)
{
    try
    {
        var decoded = await ImageLoads.RequestAsync(item.Path, side, foreground: false, token);
        if (token.IsCancellationRequested) { item.LoadRequested = false; return; }
        item.Thumbnail = decoded.Image;
        item.ThumbnailSide = side;
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
    if (_fileOperation != null) return;
    var folder = Path.GetDirectoryName(path)!;
    // Список папки уже собран сеткой (в фоне) — повторно диск не обходим
    var files = _thumbnailItems.Where(i => i.Kind == ItemKind.Image).Select(i => i.Path).ToList();
    _folderMeta = _gridMeta;
    _folderFiles = SortFileSnapshot(files, _sortMode, _sortDescending, _folderMeta); // текущий режим сортировки
    _folderListLoading = false;

    _currentIndex = _folderFiles.FindIndex(f =>
        string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    if (_currentIndex < 0)
    {
        _folderFiles = new List<string> { path };
        _currentIndex = 0;
    }

    CloseThumbnailGrid();
    ShowCurrent();
    WatchFolder(folder);
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

    // Сторона декодирования по реальным пикселям ячейки
    double scale = 1;
    try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; }
    catch (Exception ex) { AppLog.Debug("PhotoView.ThumbnailDpi", ex); }
    int needed = (int)Math.Ceiling(itemSize * Math.Max(1, scale));
    int side = ThumbnailSides.FirstOrDefault(s => s >= needed);
    if (side == 0) side = ThumbnailSides[^1];
    if (side != _thumbnailDecodeSide)
    {
        bool larger = side > _thumbnailDecodeSide;
        _thumbnailDecodeSide = side;
        // Ячейки выросли (развернули окно, перенесли на 4K) — видимые миниатюры
        // перезагружаются в большем размере; при уменьшении хватает уже загруженных.
        if (larger && ThumbnailOverlay.Visibility == Visibility.Visible) QueueVisibleThumbnails();
    }
}
       internal static readonly string[] SupportedExtensions =
{
    ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tiff", ".tif", ".gif",
    ".heic", ".heif", ".hif", ".cr2", ".nef", ".arw"
};

        private List<string> _folderFiles = new();
        private int _currentIndex = -1;
        private bool _isFullscreen;
        private bool _windowHooked;
        
        // Тот же порядок, что FileSortKey и пункты выпадающего списка.
        private enum SortMode { Name = 0, DateModified = 1, Size = 2, Type = 3 }
        private SortMode _sortMode = SortMode.DateModified;
        private bool _sortDescending = true;
        private bool _applyingSortPreference, _sortUiReady;

        /// <summary>Ставит сортировку из настроек и показывает её на панели, ничего не запоминая.</summary>
        private (FileSortKey Key, bool Descending)? _appliedSortPreference;

        private bool ApplySortPreference(bool onlyIfChanged = false)
        {
            var prefs = AppPreferences.Current;
            var wanted = (prefs.PhotoSort, prefs.PhotoSortDescending);
            if (onlyIfChanged && _appliedSortPreference == wanted) return false;
            _appliedSortPreference = wanted;
            _sortMode = (SortMode)(int)prefs.PhotoSort;
            _sortDescending = prefs.PhotoSortDescending;
            _applyingSortPreference = true;
            try { SortModeCombo.SelectedIndex = (int)_sortMode; }
            finally { _applyingSortPreference = false; }
            RefreshSortDirectionIcon();
            return true;
        }
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
        public event Action<IReadOnlyCollection<string>>? MediaRenameStarting;
        public event Action<IReadOnlyDictionary<string, string>>? MediaRenameCompleted;

        public PhotoView()
{
    InitializeComponent();
    _openButtonTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
    _openButtonTimer.Tick += OpenButtonSingleTick;
    _mirrorButtonTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
    _mirrorButtonTimer.Tick += MirrorButtonSingleTick;
    Unloaded += (_, _) => CancelOpenButtonGesture();

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

    // Сортировка из настроек (по умолчанию — по дате, новые первыми); после сохранения
    // окна настроек она сразу применяется к открытой папке.
    ApplySortPreference();
    _sortUiReady = true;
    AppPreferences.Changed += () =>
    {
        // Только если сортировку в настройках действительно поменяли: иначе OK в окне
        // настроек сбрасывал бы временный выбор на панели (при выключенном запоминании).
        if (_fileOperation != null || !ApplySortPreference(onlyIfChanged: true)) return;
        if (_sortUiReady) ReapplySort();
    };

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
                window.Deactivated += (_, __) => { CancelOpenButtonGesture(); CommitRename(); };
                window.Closing += OperationWindowClosing;
                window.Closing += (_, __) => CancelOpenButtonGesture();
                _windowHooked = true;
            }
        }
    };
}

        public void OpenFolder(string path)
{
    CancelOpenButtonGesture();
    if (_fileOperation != null) return;
    string folder;
    try { folder = Path.GetFullPath(path); if (!Directory.Exists(folder)) return; }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
    { AppLog.Warn("PhotoView.OpenFolder", ex); return; }
    CommitRename();
    _folderScanCts?.Cancel();
    ++_folderListGeneration;
    StopWatchingFolder();
    ResetCurrentImageState();
    _folderFiles = new List<string>();
    _folderMeta = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
    _folderListLoading = false;
    ApplySortPreference();
    FileNameDisplay.Text = folder;
    FileMetaText.Text = Loc.T("Choose a photo in the grid", "Выберите фотографию в сетке", "Elija una foto en la cuadrícula");
    ThumbnailOverlay.Visibility = Visibility.Visible;
    LoadGridFolder(folder);
}

        public void OpenFile(string path)
{
    CancelOpenButtonGesture();
    if (_fileOperation != null) return;
    if (Directory.Exists(path)) { OpenFolder(path); return; }
    if (!File.Exists(path)) return;

    // Файл может прийти через drag&drop в любом состоянии интерфейса - приводим его в порядок
    if (_isCropMode) ExitCropMode();
    if (ThumbnailOverlay.Visibility == Visibility.Visible) CloseThumbnailGrid();
    CommitRename();

    var folder = Path.GetDirectoryName(path)!;

    // Каждая новая папка открывается в сортировке из настроек (по умолчанию —
    // по дате, новые первыми; с запоминанием — последняя выбранная на панели).
    ApplySortPreference();

    // Фото показывается сразу, а список папки собирается и сортируется в фоне.
    // Раньше листинг и сортировка по дате (по запросу к диску на каждый файл) шли
    // прямо в потоке интерфейса: на сетевой папке или каталоге с тысячами файлов
    // окно замирало до конца обхода.
    _folderFiles = new List<string> { path };
    _folderMeta = new Dictionary<string, FolderEntry>(StringComparer.OrdinalIgnoreCase);
    _currentIndex = 0;
    _folderListLoading = true;
    ShowCurrent();

    WatchFolder(folder);
    _ = ReloadFolderListAsync(folder);
}

// ------------------------------------------------------------------ список папки

private Dictionary<string, FolderEntry> _folderMeta = new(StringComparer.OrdinalIgnoreCase);
private bool _folderListLoading;
private int _folderListGeneration;
private CancellationTokenSource? _folderScanCts;
private FileSystemWatcher? _folderWatcher;
private string? _watchedFolder;
private System.Windows.Threading.DispatcherTimer? _folderRescanTimer;

private static readonly HashSet<string> SupportedExtensionSet = new(SupportedExtensions, StringComparer.OrdinalIgnoreCase);
private static bool IsSupportedImagePath(string path) => SupportedExtensionSet.Contains(Path.GetExtension(path));

/// <summary>
/// Фоновый листинг и сортировка папки; результат применяется к списку, если за это
/// время пользователь не открыл другую папку. Текущий файл сохраняется на месте.
/// Во время файловой операции применение откладывается до её завершения.
/// </summary>
private async Task ReloadFolderListAsync(string folder)
{
    int generation = ++_folderListGeneration;
    _folderScanCts?.Cancel(); _folderScanCts?.Dispose();
    _folderScanCts = new CancellationTokenSource();
    var token = _folderScanCts.Token;
    var mode = _sortMode; bool descending = _sortDescending;
    List<string> sorted; Dictionary<string, FolderEntry> meta;
    try
    {
        (sorted, meta) = await Task.Run(() =>
        {
            var entries = FolderScanner.ScanFiles(folder, IsSupportedImagePath, token);
            ImageSaveWriter.IndexFolder(folder); // «Вернуть оригинал» и после перезапуска
            var lookup = FolderScanner.ToLookup(entries);
            return (SortFileSnapshot(entries.Select(e => e.Path).ToList(), mode, descending, lookup), lookup);
        }, token);
    }
    catch (OperationCanceledException) { return; }
    catch (Exception ex)
    {
        // Папка недоступна (сеть пропала и т. п.): остаётся то, что уже показано
        AppLog.Warn("PhotoView.ReloadFolderList", ex);
        if (generation == _folderListGeneration) { _folderListLoading = false; RefreshCurrentLabels(); }
        return;
    }

    if (generation != _folderListGeneration || token.IsCancellationRequested) return;
    if (!string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
    if (_fileOperation != null) { ScheduleFolderRescan(); return; }

    if (mode != _sortMode || descending != _sortDescending)
        sorted = SortFileSnapshot(sorted, _sortMode, _sortDescending, meta);
    string? current = _currentIndex >= 0 && _currentIndex < _folderFiles.Count ? _folderFiles[_currentIndex] : null;
    if (current == null)
    {
        // Сейчас ничего не показано (экран «нет доступных файлов») — сами файлы не открываем
        _folderListLoading = false;
        return;
    }
    int index = FolderScanner.MergeKeepingCurrent(sorted, current,
        list => SortFileSnapshot(list, _sortMode, _sortDescending, meta), out var merged);
    _folderFiles = merged;
    _folderMeta = meta;
    _currentIndex = index >= 0 ? index : (merged.Count > 0 ? 0 : -1);
    _folderListLoading = false;
    RefreshCurrentLabels();
    RefreshRotationUi(); // резервные копии найдены при сканировании — показать «Вернуть оригинал»
    PrefetchNeighbors();
}

private void RefreshCurrentLabels()
{
    if (_currentIndex >= 0 && _currentIndex < _folderFiles.Count) RefreshFileLabels(_folderFiles[_currentIndex]);
}

/// <summary>
/// Следит за папкой открытого фото: новые, удалённые и переименованные снаружи файлы
/// попадают в список (раньше список устаревал до повторного открытия). События
/// склеиваются в одну пересборку через 0,5 с. Если наблюдение невозможно (некоторые
/// сетевые ресурсы), всё работает как раньше, без автообновления.
/// </summary>
private void WatchFolder(string folder)
{
    if (_folderWatcher != null && string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
    StopWatchingFolder();
    _watchedFolder = folder;
    try
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += FolderWatcher_Changed;
        watcher.Deleted += FolderWatcher_Changed;
        watcher.Changed += FolderWatcher_Changed;
        watcher.Renamed += FolderWatcher_Changed;
        // Переполнение буфера событий или обрыв связи — просто пересобираем список целиком
        watcher.Error += (_, e) =>
        {
            AppLog.Debug("PhotoView.FolderWatcher", e.GetException());
            Dispatcher.BeginInvoke(new Action(ScheduleFolderRescan));
        };
        watcher.EnableRaisingEvents = true;
        _folderWatcher = watcher;
    }
    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        AppLog.Debug("PhotoView.WatchFolder", ex);
    }
}

private void StopWatchingFolder()
{
    var watcher = _folderWatcher;
    _folderWatcher = null;
    _watchedFolder = null;
    _folderRescanTimer?.Stop();
    if (watcher == null) return;
    try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
    catch (Exception ex) { AppLog.Debug("PhotoView.StopWatchingFolder", ex); }
}

// Вызывается в потоке пула: только фильтр и передача в UI-поток
private void FolderWatcher_Changed(object sender, FileSystemEventArgs e)
{
    bool relevant = IsSupportedImagePath(e.FullPath) ||
                    (e is RenamedEventArgs renamed && IsSupportedImagePath(renamed.OldFullPath));
    if (relevant) Dispatcher.BeginInvoke(new Action(ScheduleFolderRescan));
}

private void ScheduleFolderRescan()
{
    if (_watchedFolder == null) return;
    if (_folderRescanTimer == null)
    {
        _folderRescanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _folderRescanTimer.Tick += (_, _) =>
        {
            // Во время файловой операции список не трогаем — повторим после неё
            if (_fileOperation != null) return;
            _folderRescanTimer!.Stop();
            if (_watchedFolder != null) _ = ReloadFolderListAsync(_watchedFolder);
        };
    }
    _folderRescanTimer.Stop();
    _folderRescanTimer.Start();
}

private void SortFolderFiles()
{
    _folderFiles = SortFileSnapshot(_folderFiles, _sortMode, _sortDescending, _folderMeta);
}

/// <summary>
/// Сортировка снимка списка. meta — размер и дата из листинга папки: с ними сортировка
/// не обращается к диску (раньше — запрос на каждый файл, в потоке интерфейса).
/// Для файлов без метаданных (добавлены операциями) — обычный запрос.
/// </summary>
private static List<string> SortFileSnapshot(List<string> files, SortMode mode, bool descending,
    IReadOnlyDictionary<string, FolderEntry>? meta = null)
{
    DateTime Written(string f) => meta != null && meta.TryGetValue(f, out var e) ? e.LastWriteUtc : SafeLastWrite(f);
    long Length(string f) => meta != null && meta.TryGetValue(f, out var e) ? e.Length : SafeFileLength(f);
    IEnumerable<string> query = mode switch
    {
        // Естественный порядок (img2 < img10), как в Проводнике. Равные дата/размер —
        // тоже по имени, иначе порядок зависел бы от порядка листинга папки.
        SortMode.Name => files.OrderBy(f => f, NaturalStringComparer.FileName),
        SortMode.DateModified => files.OrderBy(Written).ThenBy(f => f, NaturalStringComparer.FileName),
        SortMode.Size => files.OrderBy(Length).ThenBy(f => f, NaturalStringComparer.FileName),
        SortMode.Type => files.OrderBy(f => Path.GetExtension(f).ToLowerInvariant(), StringComparer.Ordinal)
                              .ThenBy(f => f, NaturalStringComparer.FileName),
        _ => files
    };
    return (descending ? query.Reverse() : query).ToList();
}
private static DateTime SafeLastWrite(string path)
{
    try { return File.GetLastWriteTimeUtc(path); }
    catch (Exception ex) { AppLog.Debug("PhotoView.SafeLastWrite", ex); return DateTime.MinValue; }
}

private static long SafeFileLength(string path)
{
    try { return new FileInfo(path).Length; }
    catch (Exception ex) { AppLog.Debug("PhotoView.SafeFileLength", ex); return 0; }
}
        private void ShowCurrent()
        {
            CancelMirrorButtonGesture();
            if (_currentIndex < 0 || _currentIndex >= _folderFiles.Count) return;

            var path = _folderFiles[_currentIndex];
            if (!string.Equals(_viewRotationPath, path, StringComparison.OrdinalIgnoreCase))
            { _viewRotationDegrees = 0; _viewReflected = false; _viewRotationPath = path; }
            _viewBase = null;
            UpdateOperationControls();

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
                CancelPrefetch();
                MainImage.Source = null;
                ShowLoadingLabels(path);
                _ = LoadGifAsync(path, generation, token);
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
            _viewBase = decoded;
            var shown = WithViewRotation(decoded.Image);
            MainImage.Source = shown;
            _displayPixelWidth = shown.PixelWidth;
            _currentFileSizeBytes = decoded.SizeBytes;
            bool swap = CurrentViewTransform.SwapsDimensions;
            _currentWidth = swap ? decoded.NaturalHeight : decoded.NaturalWidth;
            _currentHeight = swap ? decoded.NaturalWidth : decoded.NaturalHeight;
            RefreshRotationUi();

            UpdateGifButtonVisibility();
            RefreshFileLabels(path);
            UpdateZoomText();
        }

/// <summary>
/// Декодирует фото в фоновом потоке. Поколение и токен защищают от гонки:
/// результат применяется только если пользователь всё ещё смотрит этот файл.
/// </summary>
private async Task LoadImageAsync(string path, int generation, CancellationToken token)
{
    try
    {
        var request = ImageLoads.RequestAsync(path, _decodeSideCap, foreground: true, token);
        CancelPrefetch();
        var decoded = await request;
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
        ShowOpenError(path, ex);
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
        RefreshGifPlaybackIcon();

        // Для координат обрезки и метаданных нужны исходные размеры,
        // а для качества масштабирования — размеры показанной копии.
        _currentWidth = animator.NaturalWidth;
        _currentHeight = animator.NaturalHeight;
        if (MainImage.Source is BitmapSource gifSource)
        {
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
    catch (GifUnsupportedException ex)
    {
        // Внутри файла не GIF (WebP, PNG, MP4 под чужим именем) либо GIF повреждён.
        // Это не сбой приложения: показываем файл обычной картинкой, как любое другое фото.
        AppLog.Warn("PhotoView.LoadGif: не анимация", ex, AppLog.Describe(path));
        if (token.IsCancellationRequested || generation != _showGeneration) return;

        if (ImageFormatSniffer.CanTryStaticImage(ex.ActualFormat, _currentFileSizeBytes))
        {
            await LoadImageAsync(path, generation, token);
            return;
        }

        // Видео или архив под именем .gif: показывать нечем, объясняем причину
        ShowOpenError(path, ex);
    }
    catch (Exception ex)
    {
        AppLog.Error("PhotoView.LoadGif", ex, AppLog.Describe(path));
        if (generation != _showGeneration) return;
        ShowOpenError(path, ex);
    }
}

private void ShowOpenError(string path, Exception ex)
{
    // Если содержимое не совпадает с расширением, это почти всегда и есть причина:
    // «испорченный» GIF чаще всего оказывается WebP или MP4 под чужим именем
    string hint = ImageFormatSniffer.DescribeMismatch(path);

    FileNameDisplay.Text = Loc.T("Failed to open", "Не удалось открыть", "No se pudo abrir") + $": {Path.GetFileName(path)} ({ex.Message}){hint}";
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
    double renderedWidth = GetRenderedImageWidthDip() * VisualTreeHelper.GetDpi(MainImage).DpiScaleX;
    if (renderedWidth <= 0) return;

    // Запас 5%: не дёргаемся на границе
    if (renderedWidth * scale <= _displayPixelWidth * 1.05) return;

    _fullResRequested = true;
    // Для 100+ Мп и широких панорам «полный размер» для экрана ограничен стороной
    // MaxZoomDecodeSide: больше не нужно для резкости и не влезает в текстуру GPU.
    int side = Math.Max(_currentWidth, _currentHeight) > MaxZoomDecodeSide ? MaxZoomDecodeSide : 0;
    _ = LoadFullResolutionAsync(_folderFiles[_currentIndex], _showGeneration, side,
        _imageLoadCts?.Token ?? CancellationToken.None);
}

/// <summary>Потолок стороны при приближении (типичный предел текстуры Direct3D 11).</summary>
private const int MaxZoomDecodeSide = 16384;

private async Task LoadFullResolutionAsync(string path, int generation, int side, CancellationToken token)
{
    try
    {
        var decoded = await ImageLoads.RequestAsync(path, side, foreground: true, token);
        if (token.IsCancellationRequested || generation != _showGeneration) return;

        ApplyDecodedImage(decoded, path);
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

    // Пока список папки собирается в фоне, общее число ещё неизвестно
    string position = _folderListLoading ? $"[{_currentIndex + 1}/\u2026]" : $"[{_currentIndex + 1}/{_folderFiles.Count}]";
    FileMetaText.Text = _currentWidth > 0
        ? $"({_currentWidth}\u00d7{_currentHeight}, {sizeText})   {position}"
        : $"({sizeText})   {position}";
}

        // --- Навигация ---

        private void PrevButton_Click(object sender, RoutedEventArgs e) => ShowPrev();
        private void NextButton_Click(object sender, RoutedEventArgs e) => ShowNext();

        private void ShowPrev() => NavigateSkippingMissing(forward: false);
private void ShowNext() => NavigateSkippingMissing(forward: true);

private void NavigateSkippingMissing(bool forward)
{
    if (_fileOperation != null) return;
    if (_folderFiles.Count == 0) return;
    _lastNavigationForward = forward;

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
    if (_fileOperation != null) return;
    if (_folderFiles.Count == 0) return;
    _currentIndex = -1; // следующий кандидат при поиске вперёд - индекс 0
    NavigateSkippingMissing(forward: true);
}

private void GoToLast()
{
    if (_fileOperation != null) return;
    if (_folderFiles.Count == 0) return;
    _currentIndex = 0; // предыдущий кандидат при поиске назад - последний индекс
    NavigateSkippingMissing(forward: false);
}

// Любой переход к пустому экрану инвалидирует ВСЕ результаты старого поколения.
private void ResetCurrentImageState()
{
    ++_showGeneration;
    _viewRotationDegrees = 0; _viewReflected = false; _viewRotationPath = null; _viewBase = null;
    RefreshRotationUi();
    CancelPrefetch();
    _imageLoadCts?.Cancel();
    _imageLoadCts?.Dispose();
    _imageLoadCts = null;
    _ocrCts?.Cancel();
    _ocrCts = null; // источник OCR освобождает владеющая им операция в finally
    _thumbnailLoadCts?.Cancel();
    _visibleThumbnailCts?.Cancel();
    CloseThumbnailGrid();
    _gifAnimator?.Stop();
    _gifAnimator = null;
    ExitCropMode();
    ResetTransform();
    _isPanning = false;
    _isDragCandidate = false;
    MainImage.ReleaseMouseCapture();
    MainImage.Cursor = Cursors.Arrow;
    MainImage.Source = null;
    _currentWidth = _currentHeight = _displayPixelWidth = 0;
    _currentFileSizeBytes = 0;
    _fullResRequested = false;
    _currentIndex = -1;
    RefreshOcrIcon();
    OcrButton.IsEnabled = true;
    GifPlayPauseButton.Visibility = Visibility.Collapsed;
    ConvertToJpgButton.Visibility = Visibility.Collapsed;
    RefreshRotationUi();
}

private void ShowNoAccessibleFilesState()
{
    ResetCurrentImageState();
    FileNameDisplay.Text = Loc.T("No accessible files in this folder", "В этой папке нет доступных файлов", "No hay archivos accesibles en esta carpeta");
    FileMetaText.Text = "";
    GifPlayPauseButton.Visibility = Visibility.Collapsed;
    ConvertToJpgButton.Visibility = Visibility.Collapsed;
}

        private void PhotoView_PreviewKeyDown(object sender, KeyEventArgs e)
{
    if (_fileOperation != null && e.Key != Key.F11 && e.Key != Key.Space)
    {
        if (e.Key == Key.Escape) CancelOperationButton_Click(sender, e);
        e.Handled = true;
        return;
    }


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
            // Автоповтор зажатого Enter не должен сразу «нажимать» и окно сохранения.
            if (!e.IsRepeat) ApplyCrop();
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
            // Зажатый Delete раньше через автоповтор удалял фото одно за другим.
            if (!e.IsRepeat) DeleteCurrentFile();
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

        // --- Зум колесом мыши + сброс средней кнопкой ---

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

    double fitPixelScale = GetFitPixelScale();
    if (fitPixelScale <= 0) return;
    double oldScale = ImageScaleTransform.ScaleX;
    double factor = e.Delta > 0 ? 1.1 : 0.9;
    double newScale = ImagePixelZoom.ClampWheelScale(oldScale * factor, oldScale, fitPixelScale);

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

        private void ZoomText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isCropMode || _fileOperation != null || _currentIndex < 0 ||
                ThumbnailOverlay.Visibility == Visibility.Visible ||
                FileNameEditBox.Visibility == Visibility.Visible) return;
            double scale = ImagePixelZoom.OneToOneRelativeScale(GetFitPixelScale());
            if (scale <= 0) return;

            // Exact physical 1:1, independently of the fitted opening size.
            // Do not clamp to the wheel's fitted-relative minimum: small images
            // must also be able to reach their true original pixel size.
            ImageScaleTransform.ScaleX = scale;
            ImageScaleTransform.ScaleY = scale;
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
            UpdateZoomText();
            MaybeUpgradeToFullResolution(scale);
            e.Handled = true;
        }

        private double GetRenderedImageWidthDip()
        {
            if (MainImage.Source is not BitmapSource source) return 0;
            return ImagePixelZoom.RenderedWidth(MainImage.ActualWidth, MainImage.ActualHeight,
                source.Width, source.Height);
        }

        private double GetFitPixelScale()
        {
            if (MainImage.Source == null || _currentWidth <= 0) return 0;
            return ImagePixelZoom.FitPixelScale(GetRenderedImageWidthDip(), _currentWidth,
                VisualTreeHelper.GetDpi(MainImage).DpiScaleX);
        }

        private void MainImage_LayoutUpdated(object? sender, EventArgs e)
        {
            // Recalculate after decoding, resize, fullscreen, rotation, and
            // monitor DPI changes. No transform is changed by this event.
            UpdateZoomText();
        }

        private void UpdateZoomText()
        {
            double fitPixelScale = GetFitPixelScale();
            string text = fitPixelScale > 0
                ? $"{fitPixelScale * ImageScaleTransform.ScaleX * 100:0.##}%"
                : "—";
            if (ZoomText != null && ZoomText.Text != text) ZoomText.Text = text;
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
                RefreshGifPlaybackIcon();
            }
            else
            {
                _gifAnimator.Start();
                RefreshGifPlaybackIcon();
            }
        }

        private void UpdateGifButtonVisibility()
        {
            MirrorButton.IsEnabled = CanMirror;
            GifPlayPauseButton.Visibility = (_gifAnimator != null && !_isFullscreen)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // --- OCR: распознавание текста на фото ---

        private async void OcrButton_Click(object sender, RoutedEventArgs e)
        {
    if (_fileOperation != null || HasViewTransform) return;
            if (_currentIndex < 0) return;
            var path = _folderFiles[_currentIndex];

            // Повторное нажатие или переход к другому фото отменяют прошлый запуск
            _ocrCts?.Cancel();
            var ocrCts = new CancellationTokenSource();
            _ocrCts = ocrCts;
            var ocrToken = ocrCts.Token;
            int ocrGeneration = _showGeneration;

            OcrButton.IsEnabled = false;
            RefreshOcrIcon();

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

                if (ocrToken.IsCancellationRequested || ocrGeneration != _showGeneration) return;
                if (ocrError != null)
                {
                    MessageBox.Show(Loc.T("Local text recognition is unavailable", "Локальное распознавание недоступно", "El reconocimiento local no está disponible")
                        + ": " + ocrError + Environment.NewLine + Environment.NewLine
                        + Loc.T("You can still send the photo to Google or Qwen.", "Фото всё равно можно отправить в Google или Qwen.", "Aún puede enviar la foto a Google o Qwen."),
                        "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Окно открываем всегда: даже если текст не найден, фото можно отправить в Google
                if (ocrToken.IsCancellationRequested || ocrGeneration != _showGeneration) return;
                var window = new OcrResultWindow(found, path) { Owner = Window.GetWindow(this) };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLog.Error("PhotoView.OcrButton_Click", ex);
                if (ocrToken.IsCancellationRequested || ocrGeneration != _showGeneration) return;
                MessageBox.Show(Loc.T("Text recognition failed", "Не удалось распознать текст", "Error en el reconocimiento de texto") + $": {ex.Message}",
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_ocrCts, ocrCts))
                {
                    _ocrCts = null;
                    RefreshOcrIcon();
                    OcrButton.IsEnabled = true;
                }
                ocrCts.Dispose();
            }
        }

        private FileOperationContext? _fileOperation;
        private Window? _operationOwner;
        private bool _closeAfterOperation;
        private string _operationTitle = "";

        private async Task RunFileOperationAsync(string title, Func<FileOperationContext, Task> body)
        {
            if (_fileOperation != null) return;
            CancelMirrorButtonGesture();
            var operation = new FileOperationContext();
            _fileOperation = operation;
            _operationOwner = Window.GetWindow(this);
            _operationTitle = title;
            _closeAfterOperation = false;
            ++_showGeneration;
            InvalidateOperationCaches();
            _imageLoadCts?.Cancel();
            _ocrCts?.Cancel();
            _ocrCts = null;
            RefreshOcrIcon();
            _thumbnailLoadCts?.Cancel();
            OperationStatusPanel.Visibility = Visibility.Visible;
            UpdateOperationControls();
            UpdateOperationStatus();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => UpdateOperationStatus();
            timer.Start();
            try { await body(operation); }
            catch (OperationCanceledException)
            {
                AppLog.Debug("PhotoView.FileOperation.Cancelled");
            }
            catch (Exception ex)
            {
                AppLog.Error("PhotoView.FileOperation", ex);
                if (!_closeAfterOperation)
                    MessageBox.Show(_operationOwner, title + ": " + ex.Message, "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                timer.Stop();
                _fileOperation = null;
                operation.Dispose();
                OperationStatusPanel.Visibility = Visibility.Collapsed;
                UpdateOperationControls();
                InvalidateOperationCaches();
                if (_closeAfterOperation) _operationOwner?.Close();
                else if (_currentIndex >= 0 && _currentIndex < _folderFiles.Count) ShowCurrent();
                else if (_folderFiles.Count == 0) ShowNoAccessibleFilesState();
                _operationOwner = null;
            }
        }

        private void InvalidateOperationCaches()
        {
            CancelPrefetch(); ImageLoads.Invalidate();
        }

        private void UpdateOperationControls()
        {
            SetCropModeUi(_isCropMode);
            bool available = _fileOperation == null;
            CropButton.IsEnabled = available;
            OcrButton.IsEnabled = available;
            CropSaveButton.IsEnabled = available;
            CropCancelButton.IsEnabled = available;
            CropOverlay.IsHitTestVisible = available;
            FileNameDisplay.IsHitTestVisible = available;
            if (!available)
            {
                foreach (var control in new Control[] { OpenButton, PrevButton, NextButton, RotateButton, MirrorButton,
                    ConvertToJpgButton, StripExifButton, BatchRenameButton, DeleteButton, GridViewButton,
                    SortModeCombo, SortDirectionButton, FileNameEditBox }) control.IsEnabled = false;
            }
            else { PrevButton.IsEnabled = NextButton.IsEnabled = FileNameEditBox.IsEnabled = true; }
            RefreshRotationUi();
        }

        private void UpdateOperationStatus()
        {
            if (_fileOperation == null) return;
            var progress = _fileOperation.Progress;
            string stage = progress.Stage switch
            {
                FileOperationStage.Decoding => Loc.T("Reading image", "Чтение изображения", "Leyendo imagen"),
                FileOperationStage.Encoding => Loc.T("Encoding", "Кодирование", "Codificando"),
                FileOperationStage.Writing => Loc.T("Writing / encoding", "Запись / кодирование", "Escribiendo / codificando"),
                FileOperationStage.Committing => Loc.T("Finishing file — cannot cancel", "Завершение записи — отмена недоступна", "Finalizando archivo — no se puede cancelar"),
                FileOperationStage.Planning => Loc.T("Scanning / planning", "Обход папок / подготовка плана", "Explorando / planificando"),
                FileOperationStage.Renaming => Loc.T("Renaming", "Переименование", "Renombrando"),
                FileOperationStage.Refreshing => Loc.T("Refreshing", "Обновление списка", "Actualizando"),
                _ => Loc.T("Preparing", "Подготовка", "Preparando")
            };
            if (_fileOperation.Token.IsCancellationRequested)
                stage = Loc.T("Cancellation requested — waiting for a safe boundary",
                    "Отмена запрошена — ожидание безопасной границы", "Cancelación solicitada — esperando un punto seguro");
            if (_closeAfterOperation)
                stage = Loc.T("Finishing before closing: ", "Завершение перед выходом: ", "Finalizando antes de cerrar: ") + stage;
            OperationStatusText.Text = _operationTitle + " — " + stage +
                (progress.Total > 0 ? $" ({progress.Processed}/{progress.Total})" : "");
            SetPhotoButtonLabel(CancelOperationButton, Loc.T("Cancel", "Отменить", "Cancelar"));
            CancelOperationButton.IsEnabled = progress.CanCancel && !_fileOperation.Token.IsCancellationRequested;
        }
        private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
        {
            _fileOperation?.Cancel();
            UpdateOperationStatus();
        }
        private void OperationWindowClosing(object? sender, CancelEventArgs e)
        {
            if (_fileOperation == null)
            {
                ++_showGeneration; _imageLoadCts?.Cancel(); _ocrCts?.Cancel();
                _thumbnailLoadCts?.Cancel(); CancelPrefetch(); ImageLoads.Invalidate();
                return;
            }
            e.Cancel = true;
            _closeAfterOperation = true;
            _fileOperation.Cancel();
            UpdateOperationStatus();
        }

        // --- Поворот ---

        private void RotateButton_Click(object sender, RoutedEventArgs e) => RotateCurrent();

        private int _viewRotationDegrees;
        private bool _viewReflected;
        private ImageViewTransformState CurrentViewTransform => new(_viewRotationDegrees, _viewReflected);
        private bool HasViewTransform => !CurrentViewTransform.IsIdentity;
        private string? _viewRotationPath;
        private DecodedImage? _viewBase;
        private BitmapSource WithViewRotation(BitmapSource image)
        {
            return ImageViewTransform.Apply(image, CurrentViewTransform);
        }
        private void RefreshRotationUi()
        {
            SetPhotoButtonLabel(SaveRotationButton, _viewReflected ? Loc.T("Save changes", "Сохранить изменения", "Guardar cambios") : Loc.T("Save rotation", "Сохранить поворот", "Guardar giro"));
            SetPhotoButtonLabel(MirrorButton, Loc.T("Mirror view", "Зеркальное отражение", "Reflejar vista"),
                Loc.T("Click: reflect horizontally. Double-click: reflect vertically. View only — the file is unchanged until you save changes.",
                    "Щелчок — отражение по горизонтали. Двойной щелчок — по вертикали. Только просмотр: файл не меняется до сохранения изменений.",
                    "Clic: reflejar horizontalmente. Doble clic: verticalmente. Solo vista: el archivo no cambia hasta guardar."));
            MirrorButton.IsEnabled = CanMirror;
            SetPhotoButtonLabel(RestoreOriginalButton, Loc.T("Restore original", "Вернуть оригинал", "Restaurar original"));
            bool idle = _fileOperation == null && !_isCropMode;
            SaveRotationButton.Visibility = HasViewTransform ? Visibility.Visible : Visibility.Collapsed;
            SaveRotationButton.IsEnabled = idle;
            string? path = _currentIndex >= 0 && _currentIndex < _folderFiles.Count ? _folderFiles[_currentIndex] : null;
            SetPhotoButtonLabel(DiscardBackupButton, Loc.T("Delete backup", "Удалить резервную копию", "Eliminar copia"), Loc.T("Keep the changes and move the backup of the original to the Recycle Bin",
                "Оставить изменения, а копию оригинала переместить в корзину", "Conservar los cambios y mover la copia del original a la papelera"));
            bool hasBackup = path != null && ImageSaveWriter.HasBackup(path);
            RestoreOriginalButton.Visibility = hasBackup ? Visibility.Visible : Visibility.Collapsed;
            RestoreOriginalButton.IsEnabled = idle;
            DiscardBackupButton.Visibility = RestoreOriginalButton.Visibility;
            DiscardBackupButton.IsEnabled = idle;
            RotationActionsPanel.Visibility = SaveRotationButton.Visibility == Visibility.Visible || hasBackup ? Visibility.Visible : Visibility.Collapsed;
            if (HasViewTransform) { CropButton.IsEnabled = false; OcrButton.IsEnabled = false; StripExifButton.IsEnabled = false; }
        }
        private void RotateCurrent()
        {
            if (_fileOperation != null || _isCropMode || _currentIndex < 0 || _viewBase == null || _gifAnimator != null) return;
            CancelMirrorButtonGesture();
            var next = CurrentViewTransform.RotateRight();
            _viewRotationDegrees = next.Degrees; _viewReflected = next.Reflected;
            ApplyDecodedImage(_viewBase, _folderFiles[_currentIndex]);
            ResetTransform();
            UpdateOperationControls();
        }
        private void SaveRotationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null || _currentIndex < 0 || !HasViewTransform) return;
            CancelMirrorButtonGesture();
            string path = _folderFiles[_currentIndex]; var transform = CurrentViewTransform;
            var options = new ImageSaveOptionsWindow(Window.GetWindow(this), path, strip: false);
            if (options.ShowDialog() != true) return;
            bool saveCopy = options.Copy, reencode = options.Reencode;
            _ = RunFileOperationAsync(Loc.T("Save changes", "Сохранение изменений", "Guardar cambios"), async operation =>
            {
                string saved = await BackgroundFileWorker.Run(() => RotateAndSaveService.SaveViewTransform(path, transform, saveCopy, reencode, operation));
                CompleteImageSave(saved);
            });
        }
        private void CompleteImageSave(string saved)
        {
            _viewRotationDegrees = 0; _viewReflected = false; _viewRotationPath = saved; _viewBase = null;
            if (!_folderFiles.Contains(saved, StringComparer.OrdinalIgnoreCase)) _folderFiles.Add(saved);
            _currentIndex = _folderFiles.FindIndex(f => string.Equals(f, saved, StringComparison.OrdinalIgnoreCase));
        }
        private void RestoreOriginalButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null || _currentIndex < 0) return;
            string path = _folderFiles[_currentIndex];
            if (MessageBox.Show(Window.GetWindow(this), Loc.T(
                    "Restore the exact original from the backup next to the photo? Unsaved rotation/reflection is discarded. If the file was changed by another program after editing, automatic restore is refused.\n\nAfterwards the backups that are no longer needed (and the undone edit) are moved to the Recycle Bin.",
                    "Вернуть точные байты оригинала из резервной копии рядом с фото? Несохранённый поворот и отражение будут сброшены. Если после правки файл изменила другая программа, автоматическое восстановление отклоняется.\n\nПосле этого ненужные резервные копии (и отменённая правка) перемещаются в корзину.",
                    "¿Restaurar el original exacto desde la copia junto a la foto? Se descartan el giro y el reflejo no guardados. Si otro programa cambió el archivo, se rechaza.\n\nDespués, las copias innecesarias (y la edición deshecha) van a la papelera."),
                    "PhotoMusicViewer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _ = RunFileOperationAsync(Loc.T("Restore original", "Восстановление оригинала", "Restaurar original"), async operation =>
            {
                var result = await BackgroundFileWorker.Run(() => ImageSaveWriter.Restore(path, operation));
                operation.Finalizing(FileOperationStage.Refreshing);
                CompleteImageSave(path);
                int left = RecycleBackups(result.RedundantBackups);
                if (left > 0)
                    MessageBox.Show(_operationOwner, Loc.T(
                        $"The original is back. {left} backup file(s) could not be moved to the Recycle Bin and remain next to the photo.",
                        $"Оригинал возвращён. Резервные копии ({left}) не удалось переместить в корзину — они остались рядом с фото.",
                        $"El original está restaurado. {left} copia(s) no se pudieron mover a la papelera."),
                        "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        /// <summary>Резервные копии — в корзину (не безвозвратно). Возвращает, сколько осталось на месте.</summary>
        private int RecycleBackups(IEnumerable<string> backups)
        {
            var owner = _operationOwner ?? Window.GetWindow(this);
            IntPtr hwnd = owner != null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero;
            var removed = new List<string>(); int left = 0;
            foreach (var backup in backups)
            {
                try
                {
                    if (!File.Exists(backup) || RecycleBinService.Recycle(backup, hwnd) == RecycleResult.Removed) removed.Add(backup);
                    else left++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    AppLog.Warn("PhotoView.RecycleBackup", ex, AppLog.Describe(backup));
                    left++;
                }
            }
            ImageSaveWriter.ForgetBackups(removed);
            return left;
        }

        private void DiscardBackupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null || _currentIndex < 0) return;
            string path = _folderFiles[_currentIndex];
            var owner = Window.GetWindow(this);
            List<ImageSaveWriter.DiskBackup> mine, all;
            try
            {
                mine = ImageSaveWriter.FindBackups(path);
                all = ImageSaveWriter.FindFolderBackups(Path.GetDirectoryName(path)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(owner, ex.Message, "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (mine.Count == 0)
            {
                ImageSaveWriter.IndexFolder(Path.GetDirectoryName(path)!);
                UpdateOperationControls();
                return;
            }
            if (MessageBox.Show(owner, Loc.T(
                    $"Keep the changes and move the backup of the original ({mine.Count} file(s)) to the Recycle Bin?\n\n\"Restore original\" will no longer be available for this photo.",
                    $"Оставить изменения, а резервную копию оригинала ({mine.Count} файл(ов)) переместить в корзину?\n\n«Вернуть оригинал» для этого фото станет недоступна.",
                    $"¿Conservar los cambios y mover la copia del original ({mine.Count} archivo(s)) a la papelera?\n\n«Restaurar original» dejará de estar disponible."),
                    "PhotoMusicViewer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var targets = mine.Select(b => b.Path).ToList();
            int others = all.Count - mine.Count;
            if (others > 0 && MessageBox.Show(owner, Loc.T(
                    $"This folder has {others} more backup file(s) of other photos. Move them to the Recycle Bin too?",
                    $"В этой папке есть ещё резервные копии других фото: {others}. Переместить в корзину и их?",
                    $"Esta carpeta tiene {others} copia(s) más de otras fotos. ¿Moverlas también a la papelera?"),
                    "PhotoMusicViewer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                targets = all.Select(b => b.Path).ToList();
            int left = RecycleBackups(targets);
            UpdateOperationControls();
            if (left > 0)
                MessageBox.Show(owner, Loc.T($"{left} backup file(s) could not be moved to the Recycle Bin.",
                    $"Не удалось переместить в корзину резервных копий: {left}.", $"No se pudieron mover {left} copia(s)."),
                    "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ConvertToJpgButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null || _currentIndex < 0) return;
            string path = _folderFiles[_currentIndex];
            var owner = Window.GetWindow(this);
            const string caption = "PhotoMusicViewer";

            WebpInfo info;
            FileVersion inspectedVersion;
            try
            {
                inspectedVersion = FileVersion.Read(path);
                info = ConvertWebpToJpgService.Inspect(path);
                if (FileVersion.Read(path) != inspectedVersion)
                    throw new IOException(Loc.T("The WebP changed during inspection. Open it again.",
                        "WebP изменился во время проверки. Откройте его заново.",
                        "El WebP cambió durante la inspección. Ábralo de nuevo."));
            }
            catch (Exception ex)
            {
                AppLog.Warn("PhotoView.InspectWebp", ex, AppLog.Describe(path));
                MessageBox.Show(owner, Loc.T("Convert to JPG", "Конвертация в JPG", "Convertir a JPG") + ": " + ex.Message,
                    caption, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Анимация в JPG/PNG не переносится: только по явному согласию и только копией
            if (info.IsAnimated)
            {
                string frames = info.AnimationFrames > 1 ? $" ({info.AnimationFrames})" : "";
                var answer = MessageBox.Show(owner,
                    Loc.T($"This WebP is animated{frames}. JPG and PNG keep only the first frame, so the animation would be lost.\n\nSave the first frame as a separate file? The original WebP stays untouched.",
                          $"Этот WebP анимированный{frames}. В JPG и PNG сохранится только первый кадр — анимация будет потеряна.\n\nСохранить первый кадр отдельным файлом? Исходный WebP останется нетронутым.",
                          $"Este WebP es animado{frames}. JPG y PNG solo conservan el primer fotograma: la animación se perdería.\n\n¿Guardar el primer fotograma como archivo aparte? El WebP original no se modifica."),
                    caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            // JPG не умеет прозрачность: предлагаем PNG (по умолчанию) или белый фон
            var target = WebpConvertTarget.Jpg;
            if (info.MayHaveAlpha)
            {
                var answer = MessageBox.Show(owner,
                    Loc.T("This WebP may contain transparency, which JPG cannot store.\n\nYes — save as PNG (keeps transparency)\nNo — save as JPG on a white background\nCancel — do nothing",
                          "В этом WebP может быть прозрачность, а JPG её не поддерживает.\n\nДа — сохранить как PNG (прозрачность сохранится)\nНет — сохранить как JPG на белом фоне\nОтмена — ничего не делать",
                          "Este WebP puede tener transparencia, y JPG no la admite.\n\nSí — guardar como PNG (conserva la transparencia)\nNo — guardar como JPG con fondo blanco\nCancelar — no hacer nada"),
                    caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None) return;
                target = answer == MessageBoxResult.Yes ? WebpConvertTarget.Png : WebpConvertTarget.Jpg;
            }

            bool offerRemoveOriginal = !info.IsAnimated;
            var mode = _sortMode; bool descending = _sortDescending;
            _ = RunFileOperationAsync(Loc.T("Convert to JPG", "Конвертация в JPG", "Convertir a JPG"), async operation =>
            {
                var converted = await BackgroundFileWorker.Run(() =>
                {
                    operation.Checkpoint(FileOperationStage.Decoding);
                    var snapshot = SourceFileSnapshot.Capture(path, operation.Token);
                    if (snapshot.Version != inspectedVersion)
                        throw new IOException(Loc.T("The WebP changed. Open it again before converting.",
                            "WebP изменился. Откройте его заново перед конвертацией.",
                            "El WebP cambió. Ábralo de nuevo antes de convertir."));
                    string result = ConvertWebpToJpgService.ConvertVerified(path, target, snapshot, operation);
                    return (Saved: result, Snapshot: snapshot);
                });
                string saved = converted.Saved;
                operation.Finalizing(FileOperationStage.Refreshing);

                // Сначала фиксируем завершённое сохранение: новый файл появляется рядом с исходником.
                var files = _folderFiles.ToList();
                if (!files.Contains(saved, StringComparer.OrdinalIgnoreCase)) files.Add(saved);
                _folderFiles = files;
                _currentIndex = files.FindIndex(f => string.Equals(f, saved, StringComparison.OrdinalIgnoreCase));

                // Исходник удаляется только по явному «Да» (по умолчанию — «Нет»),
                // и только через окна Windows, которые спросят о безвозвратном удалении.
                if (offerRemoveOriginal && !_closeAfterOperation)
                {
                    bool unchanged = await BackgroundFileWorker.Run(() => converted.Snapshot.Matches(path));
                    if (!unchanged)
                        MessageBox.Show(_operationOwner, Loc.T(
                            "The original WebP changed or is unavailable. It will not be deleted.",
                            "Исходный WebP изменился или недоступен. Он не будет удалён.",
                            "El WebP original cambió o no está disponible. No se eliminará."),
                            caption, MessageBoxButton.OK, MessageBoxImage.Information);
                    var answer = !unchanged ? MessageBoxResult.No : MessageBox.Show(_operationOwner,
                        Loc.T($"Saved: {Path.GetFileName(saved)}\n\nMove the original WebP to the Recycle Bin?",
                              $"Сохранено: {Path.GetFileName(saved)}\n\nПереместить исходный WebP в корзину?",
                              $"Guardado: {Path.GetFileName(saved)}\n\n¿Mover el WebP original a la papelera?"),
                        caption, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (answer == MessageBoxResult.Yes)
                    {
                        IntPtr hwnd = _operationOwner != null
                            ? new System.Windows.Interop.WindowInteropHelper(_operationOwner).Handle : IntPtr.Zero;
                        // Повторная проверка ПОСЛЕ диалога. Shell получает только отдельно
                        // изолированный и проверенный файл, а не исходный путь синхронизации.
                        var removed = await BackgroundFileWorker.Run(() => VerifiedSourceRemoval.Recycle(
                            path, converted.Snapshot, staged => RecycleBinService.TryDeleteWithConfirmation(staged, hwnd)));
                        if (removed == SourceRemovalResult.Removed && !File.Exists(path))
                            files.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                        else if (removed == SourceRemovalResult.SourceChanged)
                            MessageBox.Show(_operationOwner, Loc.T(
                                "The original changed while confirmation was open. It was not deleted.",
                                "Исходник изменился, пока было открыто подтверждение. Он не удалён.",
                                "El original cambió durante la confirmación. No se eliminó."),
                                caption, MessageBoxButton.OK, MessageBoxImage.Information);
                        else if (File.Exists(path))
                            MessageBox.Show(_operationOwner,
                                Loc.T("The original WebP was kept.", "Исходный WebP оставлен на месте.", "Se conservó el WebP original."),
                                caption, MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }

                _folderFiles = await Task.Run(() => SortFileSnapshot(files, mode, descending));
                _currentIndex = _folderFiles.FindIndex(f => string.Equals(f, saved, StringComparison.OrdinalIgnoreCase));
            });
        }

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteCurrentFile();

private void DeleteCurrentFile()
{
    if (_fileOperation != null) return;
    if (_currentIndex < 0) return;
    var path = _folderFiles[_currentIndex];
    var owner = Window.GetWindow(this);

    // Подтверждение (по умолчанию включено, отключается в настройках). Если корзина для
    // этого файла недоступна, Windows дополнительно спросит про безвозвратное удаление.
    if (AppPreferences.Current.ConfirmDelete &&
        MessageBox.Show(owner,
            Loc.T($"Move \"{Path.GetFileName(path)}\" to the Recycle Bin?",
                  $"Переместить «{Path.GetFileName(path)}» в корзину?",
                  $"¿Mover «{Path.GetFileName(path)}» a la papelera?") + "\n\n" +
            Loc.T("If this drive has no Recycle Bin (USB stick, network drive) or the file is too large, Windows will ask before deleting it permanently.",
                  "Если на этом диске нет корзины (флешка, сетевой диск) или файл слишком большой, Windows спросит перед безвозвратным удалением.",
                  "Si esta unidad no tiene papelera (USB, red) o el archivo es demasiado grande, Windows preguntará antes de borrarlo definitivamente."),
            Loc.T("Delete", "Удаление", "Eliminar"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
        return;

    try
    {
        IntPtr hwnd = owner != null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero;
        if (RecycleBinService.Recycle(path, hwnd) == RecycleResult.Declined) return; // отказ в окне Windows — файл на месте
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        // Раньше ошибка попадала только в журнал (по умолчанию выключенный): файл
        // оставался на месте, а пользователь думал, что удалил его.
        AppLog.Warn("PhotoView.DeleteCurrentFile", ex, AppLog.Describe(path));
        if (ex is not RecycleException { ReportedByWindows: true } and not FileNotFoundException)
            MessageBox.Show(owner, Loc.T("The file was not deleted: ", "Файл не удалён: ", "No se eliminó el archivo: ") + ex.Message,
                "PhotoMusicViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (ex is not FileNotFoundException) return; // файл на месте
        // Файла уже нет — просто убираем его из списка, как после удаления.
    }

    int removedIndex = _currentIndex;
    _folderFiles.RemoveAt(removedIndex);

    if (_folderFiles.Count == 0)
    {
        ResetCurrentImageState();
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
    if (_fileOperation != null) return;
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

    int copyGeneration = _showGeneration;
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
                var fullSize = (await ImageLoads.RequestAsync(path, 0, foreground: true,
                    _imageLoadCts?.Token ?? CancellationToken.None)).Image;

                // Пока шло декодирование, пользователь мог пролистать дальше
                if (_currentIndex >= 0 && _currentIndex < _folderFiles.Count &&
                    string.Equals(_folderFiles[_currentIndex], path, StringComparison.OrdinalIgnoreCase))
                    bitmapSource = WithViewRotation(fullSize);
            }
            catch (Exception ex) { AppLog.Warn("PhotoView.ClipboardFullDecode", ex); /* кладём то, что уже показано */ }
        }

        // Картинка кладётся в буфер как обычно (Ctrl+V работает везде), с флагами
        // приватности: без истории буфера (Win+V) и облачной синхронизации.
        // Как и скопированный текст, она доступна, пока приложение открыто,
        // и убирается из буфера при выходе (PrivacyClipboard.ClearOwnedContent).
        if (copyGeneration != _showGeneration) return;
        var image = bitmapSource;

        // Буфер обмена - общий ресурс: пока его держит другое приложение, запись
        // не проходит. Windows советует повторить попытку через короткую паузу.
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            if (copyGeneration != _showGeneration) return;
            try
            {
                PrivacyClipboard.SetImage(image);
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
    if (_fileOperation != null) return;
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


        /// <summary>Открывает меню настроек перевода (ключи API, модель, промты, языки).</summary>
        private void TranslateSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = new TranslationSettingsWindow { Owner = Window.GetWindow(this) };
            settings.ShowDialog();
        }

        private void ApplyLocalization()
        {
            SetPhotoButtonLabel(PhotoModeToggleBtn, Loc.T("Photo", "Фото", "Foto"));
            SetPhotoButtonLabel(MusicModeToggleBtn, Loc.T("Music", "Музыка", "Música"));
            SetPhotoButtonLabel(OpenButton, Loc.T("Open", "Открыть", "Abrir"),
                Loc.T("Click: open an image. Double-click: open a folder.", "Щелчок — открыть фото. Двойной щелчок — открыть папку.", "Clic: abrir imagen. Doble clic: abrir carpeta."));
            UpdateGridFolderStatus();
            SetPhotoButtonLabel(RotateButton, Loc.T("Rotate view", "Повернуть вид", "Girar vista"),
                Loc.T("View only — the file is unchanged. Use Save changes to write a copy.", "Только просмотр — файл не меняется. Для записи используйте кнопку сохранения изменений.", "Solo vista: archivo sin cambios. Use Guardar cambios para crear copia."));
            RefreshRotationUi();
            RefreshCropIcon();
            RefreshOcrIcon();
            RefreshFullscreenIcon();
            RefreshSortDirectionIcon();
            RefreshGifPlaybackIcon();
            SetPhotoButtonLabel(StripExifButton, Loc.T("Strip EXIF", "Убрать EXIF", "Quitar EXIF"));
            SetPhotoButtonLabel(BatchRenameButton, Loc.T("Rename all", "Переименовать все", "Renombrar todo"));
            SetPhotoButtonLabel(DeleteButton, Loc.T("Delete", "Удалить", "Eliminar"));
            SetPhotoButtonLabel(ConvertToJpgButton, Loc.T("Convert to JPG", "В JPG", "A JPG"));
            SetPhotoButtonLabel(GridViewButton, Loc.T("Grid", "Сетка", "Cuadrícula"));
            SetPhotoButtonLabel(TranslateSettingsButton, Loc.T("Settings…", "Настройки…", "Ajustes…"));
            SetPhotoButtonLabel(CropSaveButton, Loc.T("Save", "Сохранить", "Guardar"));
            SetPhotoButtonLabel(CropCancelButton, Loc.T("Cancel", "Отмена", "Cancelar"));
            SetPhotoButtonLabel(CancelOperationButton, Loc.T("Cancel", "Отменить", "Cancelar"));
            SetPhotoButtonLabel(PrevButton, Loc.T("Previous image", "Предыдущее фото", "Imagen anterior"));
            SetPhotoButtonLabel(NextButton, Loc.T("Next image", "Следующее фото", "Imagen siguiente"));
            SetPhotoButtonLabel(SortModeCombo, Loc.T("Sort images", "Сортировка фотографий", "Ordenar imágenes"));
            ZoomText.ToolTip = new ToolTip
            {
                FontSize = 13,
                Content = Loc.T("Click: 100% — one image pixel per screen pixel. Middle click: fit to window.",
                    "Щелчок — 100%, пиксель в пиксель. Нажатие колёсика — вписать в окно.",
                    "Clic: 100%, píxel por píxel. Botón central: ajustar a la ventana.")
            };

            SetComboItemText(SortModeCombo, 0, Loc.T("Name", "Имя", "Nombre"));
            SetComboItemText(SortModeCombo, 1, Loc.T("Date modified", "Дата изменения", "Fecha de modificación"));
            SetComboItemText(SortModeCombo, 2, Loc.T("Size", "Размер", "Tamaño"));
            SetComboItemText(SortModeCombo, 3, Loc.T("Type", "Тип", "Tipo"));

            // Текст-заглушка виден, только пока файл не открыт
            if (_currentIndex < 0)
            {
                FileNameDisplay.Text = _gridCurrentFolder ?? Loc.T("No file opened", "Файл не открыт", "Ningún archivo abierto");
                if (ThumbnailOverlay.Visibility == Visibility.Visible)
                    FileMetaText.Text = Loc.T("Choose a photo in the grid", "Выберите фотографию в сетке", "Elija una foto en la cuadrícula");
            }
        }

        private void PhotoToolbarBody_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Status belongs to the right-hand group. Keep metadata and zoom
            // accessible in the filename tooltip at exceptionally small widths.
            if (PhotoSortPanel == null || FileMetaText == null || ZoomText == null) return;
            PhotoSortPanel.Visibility = e.NewSize.Width >= 1000 ? Visibility.Visible : Visibility.Collapsed;
            FileMetaText.MaxWidth = e.NewSize.Width >= 1000 ? 320 : 240;
            FileMetaText.Visibility = e.NewSize.Width >= 700 ? Visibility.Visible : Visibility.Collapsed;
            ZoomText.Visibility = e.NewSize.Width >= 700 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetPhotoButtonLabel(Control control, string label, string? explanation = null)
        {
            string text = string.IsNullOrWhiteSpace(explanation) ? label : label + Environment.NewLine + explanation;
            control.ToolTip = new ToolTip
            {
                Style = (Style)FindResource("PhotoIconToolTipStyle"),
                Content = new TextBlock { Text = text, Foreground = (Brush)FindResource("ForegroundBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }
            };
            System.Windows.Automation.AutomationProperties.SetName(control, label);
            System.Windows.Automation.AutomationProperties.SetHelpText(control, text);
        }
        private void SetPhotoIcon(Image image, string name) => image.Source = (ImageSource)FindResource("PhotoIcon." + name);
        private void RefreshCropIcon()
        {
            SetPhotoIcon(CropIcon, _isCropMode ? "Cancel" : "Crop");
            SetPhotoButtonLabel(CropButton, _isCropMode ? Loc.T("Cancel crop", "Отменить обрезку", "Cancelar recorte") : Loc.T("Crop", "Обрезать", "Recortar"));
        }
        private void RefreshOcrIcon()
        {
            bool scanning = _ocrCts != null;
            SetPhotoIcon(OcrIcon, scanning ? "Busy" : "Scan");
            SetPhotoButtonLabel(OcrButton, scanning ? Loc.T("Scanning...", "Распознавание...", "Reconociendo...") : Loc.T("Scan text", "Распознать текст", "Reconocer texto"));
        }
        private void RefreshFullscreenIcon()
        {
            SetPhotoIcon(FullscreenIcon, _isFullscreen ? "ExitFullscreen" : "Fullscreen");
            SetPhotoButtonLabel(FullscreenButton, _isFullscreen ? Loc.T("Exit fullscreen", "Выйти из полного экрана", "Salir de pantalla completa") : Loc.T("Fullscreen", "Во весь экран", "Pantalla completa"));
        }
        private void RefreshSortDirectionIcon()
        {
            SetPhotoIcon(SortDirectionIcon, _sortDescending ? "SortDown" : "SortUp");
            SetPhotoButtonLabel(SortDirectionButton, _sortDescending ? Loc.T("Descending", "По убыванию", "Descendente") : Loc.T("Ascending", "По возрастанию", "Ascendente"));
        }
        private void RefreshGifPlaybackIcon()
        {
            bool playing = _gifAnimator?.IsPlaying == true;
            SetPhotoIcon(GifPlaybackIcon, playing ? "Pause" : "Play");
            SetPhotoButtonLabel(GifPlayPauseButton, playing ? Loc.T("Pause GIF", "Пауза GIF", "Pausar GIF") : Loc.T("Play GIF", "Продолжить GIF", "Reproducir GIF"));
        }

        private static void SetComboItemText(ComboBox combo, int index, string text)
        {
            if (index < combo.Items.Count && combo.Items[index] is ComboBoxItem item)
                item.Content = text;
        }

        private void ToggleFullscreen()
{
    CancelMirrorButtonGesture();
    _isFullscreen = !_isFullscreen;
    RefreshFullscreenIcon();
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

        // --- Reflection: same click state machine and 150 ms interval as Open. ---
        private readonly OpenButtonGesture _mirrorButtonGesture = new();
        private readonly System.Windows.Threading.DispatcherTimer _mirrorButtonTimer;
        private bool _mirrorMouseActivation;
        private string? _mirrorTargetPath;
        private int _mirrorTargetGeneration;
        private bool CanMirror => _fileOperation == null && !_isCropMode && ThumbnailOverlay.Visibility != Visibility.Visible && _viewBase != null && _gifAnimator == null && _currentIndex >= 0 && _currentIndex < _folderFiles.Count;
        private void CancelMirrorButtonGesture()
        {
            _mirrorButtonTimer?.Stop();
            _mirrorButtonGesture.Cancel();
            _mirrorMouseActivation = false;
            _mirrorTargetPath = null;
        }
        private void CaptureMirrorTarget()
        {
            _mirrorTargetPath = CanMirror ? _folderFiles[_currentIndex] : null;
            _mirrorTargetGeneration = _showGeneration;
        }
        private void MirrorButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!CanMirror) { CancelMirrorButtonGesture(); e.Handled = true; return; }
            _mirrorButtonTimer.Stop();
            if (!_mirrorButtonGesture.SinglePending) CaptureMirrorTarget();
            _mirrorButtonGesture.MouseDown(e.ClickCount);
            _mirrorMouseActivation = true;
        }
        private void MirrorButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!MirrorButton.IsMouseOver) CancelMirrorButtonGesture();
        }
        private void MirrorButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanMirror) { CancelMirrorButtonGesture(); return; }
            bool mouse = _mirrorMouseActivation && InputManager.Current.MostRecentInputDevice is MouseDevice;
            _mirrorMouseActivation = false;
            _mirrorButtonTimer.Stop();
            if (!mouse) CaptureMirrorTarget();
            var action = mouse ? _mirrorButtonGesture.MouseClick() : _mirrorButtonGesture.KeyboardClick();
            if (_mirrorButtonGesture.SinglePending)
            {
                _mirrorButtonTimer.Interval = TimeSpan.FromMilliseconds(OpenButtonGesture.DelayMilliseconds(GetDoubleClickTime()));
                _mirrorButtonTimer.Start();
            }
            else RunMirrorButtonAction(action);
        }
        private void MirrorButtonSingleTick(object? sender, EventArgs e)
        {
            _mirrorButtonTimer.Stop();
            var action = _mirrorButtonGesture.SingleExpired();
            if (!IsLoaded || !IsVisible || Window.GetWindow(this)?.IsActive != true) { CancelMirrorButtonGesture(); return; }
            RunMirrorButtonAction(action);
        }
        private void RunMirrorButtonAction(OpenButtonAction action)
        {
            bool sameTarget = CanMirror && _mirrorTargetGeneration == _showGeneration &&
                string.Equals(_mirrorTargetPath, _folderFiles[_currentIndex], StringComparison.OrdinalIgnoreCase);
            CancelMirrorButtonGesture();
            if (!sameTarget || action == OpenButtonAction.None) return;
            var next = action == OpenButtonAction.Folder ? CurrentViewTransform.ReflectVertical() : CurrentViewTransform.ReflectHorizontal();
            _viewRotationDegrees = next.Degrees; _viewReflected = next.Reflected;
            ApplyDecodedImage(_viewBase!, _folderFiles[_currentIndex]);
            ResetTransform();
            UpdateOperationControls();
        }

        // --- Открытие файла вручную ---

        private readonly OpenButtonGesture _openButtonGesture = new();
        private readonly System.Windows.Threading.DispatcherTimer _openButtonTimer;
        private bool _openMouseActivation;

        [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
        private static extern uint GetDoubleClickTime();

        private void CancelOpenButtonGesture()
        {
            CancelMirrorButtonGesture();
            _openButtonTimer.Stop();
            _openButtonGesture.Cancel();
            _openMouseActivation = false;
        }
        private void OpenButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_fileOperation != null) { CancelOpenButtonGesture(); e.Handled = true; return; }
            // Stop before the second press: a delayed file dialog must not appear
            // while the user is completing a double click or holding the button.
            _openButtonTimer.Stop();
            _openButtonGesture.MouseDown(e.ClickCount);
            _openMouseActivation = true;
        }
        private void OpenButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!OpenButton.IsMouseOver) CancelOpenButtonGesture();
        }
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null) { CancelOpenButtonGesture(); return; }
            bool mouse = _openMouseActivation && InputManager.Current.MostRecentInputDevice is MouseDevice;
            _openMouseActivation = false;
            _openButtonTimer.Stop();
            var action = mouse ? _openButtonGesture.MouseClick() : _openButtonGesture.KeyboardClick();
            if (_openButtonGesture.SinglePending)
            {
                _openButtonTimer.Interval = TimeSpan.FromMilliseconds(OpenButtonGesture.DelayMilliseconds(GetDoubleClickTime()));
                _openButtonTimer.Start();
            }
            else RunOpenButtonAction(action);
        }
        private void OpenButtonSingleTick(object? sender, EventArgs e)
        {
            _openButtonTimer.Stop();
            var action = _openButtonGesture.SingleExpired();
            if (!IsLoaded || !IsVisible || Window.GetWindow(this)?.IsActive != true) { CancelOpenButtonGesture(); return; }
            RunOpenButtonAction(action);
        }
        private void RunOpenButtonAction(OpenButtonAction action)
        {
            if (_fileOperation != null) return;
            // All gesture state is consumed BEFORE entering a modal dialog.
            if (action == OpenButtonAction.Image) OpenImagePicker();
            else if (action == OpenButtonAction.Folder) OpenFolderPicker();
        }

        private void OpenImagePicker()
        {
            if (_fileOperation != null) return;
            var dialog = new OpenFileDialog
            {
                Filter = "Images|" + string.Join(";", SupportedExtensions.Select(ext => "*" + ext))
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            RecentTracesService.Erase(dialog.FileName);
            OpenFile(dialog.FileName);
        }

        private void OpenFolderPicker()
        {
            if (_fileOperation != null) return;
            var dialog = new OpenFolderDialog
            {
                Title = Loc.T("Open photo folder", "Открыть папку с фотографиями", "Abrir carpeta de fotos"),
                Multiselect = false
            };
            string? currentFolder = _currentIndex >= 0 && _currentIndex < _folderFiles.Count
                ? Path.GetDirectoryName(_folderFiles[_currentIndex]) : _gridCurrentFolder;
            if (!string.IsNullOrEmpty(currentFolder) && Directory.Exists(currentFolder)) dialog.InitialDirectory = currentFolder;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            RecentTracesService.Erase(dialog.FolderName);
            OpenFolder(dialog.FolderName);
        }

        private void SortModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_applyingSortPreference) return;
    if (_fileOperation != null)
    {
        // Во время операции порядок не меняется — возвращаем выпадающий список как был.
        _applyingSortPreference = true;
        try { SortModeCombo.SelectedIndex = (int)_sortMode; } finally { _applyingSortPreference = false; }
        return;
    }

    // Режим запоминается и без открытой папки: раньше выбор на пустом экране
    // молча терялся, и список показывал не ту сортировку, что применялась.
    _sortMode = SortModeCombo.SelectedIndex switch
    {
        0 => SortMode.Name,
        1 => SortMode.DateModified,
        2 => SortMode.Size,
        3 => SortMode.Type,
        _ => SortMode.Name
    };
    if (_sortUiReady) AppPreferences.RememberPhotoSort((FileSortKey)(int)_sortMode, _sortDescending);

    if (_sortUiReady) ReapplySort();
}

private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
{
    if (_fileOperation != null) return;

    _sortDescending = !_sortDescending;
    RefreshSortDirectionIcon();
    AppPreferences.RememberPhotoSort((FileSortKey)(int)_sortMode, _sortDescending);

    if (_sortUiReady) ReapplySort();
}

private void ReapplyGridSort()
{
    if (ThumbnailOverlay.Visibility != Visibility.Visible || _gridFolderLoading) return;

    var ordered = ThumbnailGridOrder.Reorder(_thumbnailItems,
        item => item.Kind == ItemKind.Image, item => item.Path,
        paths => SortFileSnapshot(paths, _sortMode, _sortDescending, _gridMeta));
    _visibleThumbnailCts?.Cancel();
    _thumbnailItems.Clear();
    foreach (var item in ordered) _thumbnailItems.Add(item);
    _thumbnailFirstVisibleRow = 0;
    BuildThumbnailRows();
    var viewer = _thumbnailScrollViewer ??= FindScrollViewer(ThumbnailItemsControl);
    viewer?.ScrollToTop();
    QueueVisibleThumbnails();

    // Refresh visible rows after virtualization has consumed the new ordering.
    // A folder change/closed grid invalidates this deferred update.
    int generation = _gridGeneration;
    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
        new Action(() =>
        {
            if (generation != _gridGeneration || ThumbnailOverlay.Visibility != Visibility.Visible) return;
            viewer?.ScrollToTop();
            QueueVisibleThumbnails();
        }));
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
    ReapplyGridSort();
}

        // --- Массовое переименование (нумерация) ---

        private void BatchRenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null || _isCropMode || ThumbnailOverlay.Visibility == Visibility.Visible) return;
            CommitRename();
            string initialFolder = _currentIndex >= 0 && _currentIndex < _folderFiles.Count
                ? Path.GetDirectoryName(_folderFiles[_currentIndex])! : (_gridCurrentFolder ?? "");
            var dialog = new BatchRenameDialog(initialFolder) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            string folder = dialog.FolderPath;
            bool recursive = dialog.Recursive;
            bool renameNumericNames = dialog.RenameNumericNames;
            var categories = dialog.MediaCategories;
            bool includeCompanions = dialog.IncludeCompanions;
            long start = dialog.StartNumber; int padding = dialog.ZeroPadding;
            var nameScheme = dialog.NameScheme;
            // Корень диска, системные папки и (с подпапками) папки, внутри которых они лежат, — отказ сразу.
            if (BatchRenameService.ScopeProblem(folder, recursive) is { } scopeProblem)
            {
                MessageBox.Show(Window.GetWindow(this), scopeProblem, Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var mode = _sortMode; bool descending = _sortDescending;
            // Снимок метаданных текущей папки (на потоке интерфейса): нумерация идёт в том же
            // порядке, что и просмотр, и сортировка по дате/размеру не перечитывает диск.
            var metaSnapshot = new Dictionary<string, FolderEntry>(_folderMeta, StringComparer.OrdinalIgnoreCase);
            string orderText = SortOrderDescription(mode, descending);
            _ = RunFileOperationAsync(Loc.T("Batch rename", "Массовое переименование", "Renombrado masivo"), async operation =>
            {
                var plan = await Task.Run(() => BatchRenameService.BuildMediaPlan(folder, recursive, start,
                    padding, categories, operation,
                    files => SortFileSnapshot(files, mode, descending, metaSnapshot),
                    renameNumericNames: renameNumericNames, includeCompanions: includeCompanions, nameScheme: nameScheme));
                operation.Token.ThrowIfCancellationRequested();
                if (_closeAfterOperation) return;
                if (plan.Count == 0)
                {
                    MessageBox.Show(Loc.T("Nothing to rename.", "Нечего переименовывать.", "Nada que renombrar."), "PhotoMusicViewer");
                    return;
                }
                var example = plan[0];
                int mediaFiles = plan.Count(p => !p.Companion), companions = plan.Count - mediaFiles;
                var folders = plan.Select(p => Path.GetDirectoryName(p.OldPath)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string question = Loc.T($"Rename {mediaFiles} media file(s)?", $"Переименовать медиафайлы: {mediaFiles}?", $"¿Renombrar {mediaFiles} archivos multimedia?");
                var categoryNames = new List<string>();
                if (categories.HasFlag(RenameMediaCategories.Photos)) categoryNames.Add(Loc.T("photos/RAW", "фото/RAW", "fotos/RAW"));
                if (categories.HasFlag(RenameMediaCategories.Videos)) categoryNames.Add(Loc.T("videos", "видео", "vídeos"));
                if (categories.HasFlag(RenameMediaCategories.Music)) categoryNames.Add(Loc.T("music/audio", "музыка/аудио", "música/audio"));
                question += "\n" + Loc.T("Selected types: ", "Выбранные типы: ", "Tipos seleccionados: ") + string.Join(", ", categoryNames);
                question += "\n" + (renameNumericNames
                    ? Loc.T("Mode: all selected media, including numeric names. Existing numbers are skipped; numbering may contain gaps.",
                            "Режим: все выбранные медиа, включая числовые имена. Занятые номера пропускаются; нумерация может быть несплошной.",
                            "Modo: todos los medios seleccionados, incluidos nombres numéricos. Se saltan números ocupados; puede haber huecos.")
                    : Loc.T("Mode: only non-numeric names.", "Режим: только нечисловые имена.", "Modo: solo nombres no numéricos."));
                if (companions > 0)
                {
                    string kinds = string.Join(", ", plan.Where(p => p.Companion).Select(p => CompanionKind(p.OldPath)).Distinct());
                    question += "\n" + Loc.T($"Together with {companions} companion file(s) with the same name ({kinds}) — they get the same number.",
                        $"Вместе с ними — файлы-спутники с тем же именем ({kinds}): {companions}. Они получат тот же номер.",
                        $"Junto con {companions} archivo(s) asociados ({kinds}), con el mismo número.");
                }
                if (folders.Count > 1)
                {
                    var shown = folders.Take(6).Select(f => "  • " + (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase) ? "." : Path.GetRelativePath(folder, f)));
                    question += "\n\n" + Loc.T($"In {folders.Count} folders:", $"В папках ({folders.Count}):", $"En {folders.Count} carpetas:") + "\n" +
                        string.Join("\n", shown) + (folders.Count > 6 ? "\n  …" : "");
                }
                var exampleGroup = plan.Where(p => p.Group == example.Group).Select(p => Path.GetFileName(p.OldPath) + " → " + Path.GetFileName(p.NewPath)).Take(4);
                var confirm = MessageBox.Show(_operationOwner,
                    question +
                    "\n\n" + string.Join("\n", exampleGroup) + "\n" +
                    Loc.T("Numbering order: ", "Порядок нумерации: ", "Orden de numeración: ") + orderText + "\n\n" +
                    Loc.T("Existing files are never overwritten. Cancellation stops between groups (not inside a RAW+JPG pair); completed renames are not undone. If the current music track is included, playback stops before renaming.",
                          "Существующие файлы не перезаписываются. Отмена останавливает работу между группами (не внутри RAW+JPG); выполненные переименования не откатываются. Если текущий трек включён, воспроизведение остановится перед переименованием.",
                          "No se sobrescriben archivos. La cancelación se detiene entre grupos (no dentro de RAW+JPG); lo ya renombrado no se deshace. Si se incluye la pista actual, se detiene antes de renombrarla."),
                    "PhotoMusicViewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
                MediaRenameStarting?.Invoke(plan.Select(p => p.OldPath).ToArray());
                string? current = _currentIndex >= 0 && _currentIndex < _folderFiles.Count ? _folderFiles[_currentIndex] : null;
                var result = await Task.Run(() => BatchRenameService.Execute(plan, operation));
                MediaRenameCompleted?.Invoke(result.OldToNew);
                operation.Finalizing(FileOperationStage.Refreshing, result.RenamedCount);
                if (current != null && result.OldToNew.TryGetValue(current, out var renamed)) current = renamed;
                _folderFiles = _folderFiles.Select(f => result.OldToNew.TryGetValue(f, out var mapped) ? mapped : f).ToList();
                _currentIndex = _folderFiles.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
                // После частичной отмены результаты всё равно применяются. Refresh без отменённого токена.
                try
                {
                    string refreshFolder = current != null ? Path.GetDirectoryName(current)! : folder;
                    _folderFiles = await Task.Run(() => SortFileSnapshot(Directory.EnumerateFiles(refreshFolder)
                        .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList(), mode, descending));
                    _currentIndex = _folderFiles.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
                    if (_currentIndex < 0 && _folderFiles.Count > 0) _currentIndex = 0;
                }
                catch (Exception ex) { AppLog.Warn("PhotoView.BatchRename.Refresh", ex); }
                if (_closeAfterOperation) return;
                string summary = Loc.T($"Renamed {result.RenamedCount} of {plan.Count} file(s).",
                    $"Переименовано {result.RenamedCount} из {plan.Count} файл(ов).", $"Renombrados {result.RenamedCount} de {plan.Count} archivos.");
                if (result.Cancelled) summary += "\n" + Loc.T("Cancelled between files.", "Отменено между файлами.", "Cancelado entre archivos.");
                if (result.ErrorMessage != null) summary += "\n\n" + result.ErrorMessage;
                MessageBox.Show(_operationOwner, summary, "PhotoMusicViewer", MessageBoxButton.OK,
                    result.ErrorMessage != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
            });
        }

        /// <summary>Вид файла-спутника для подтверждения: RAW, .xmp, .AAE, .MOV, резервная копия.</summary>
        private static string CompanionKind(string path)
        {
            string name = Path.GetFileName(path);
            if (name.Contains(ImageSaveWriter.OriginalMarker, StringComparison.OrdinalIgnoreCase) || name.Contains(ImageSaveWriter.EditedMarker, StringComparison.OrdinalIgnoreCase))
                return Loc.T("backups", "резервные копии", "copias");
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext is ".xmp" or ".aae" or ".thm" or ".pp3" or ".dop" or ".on1" or ".arp" or ".mov" or ".mp4" or ".wav" || SupportedExtensions.Contains(ext)
                ? ext : "RAW";
        }

        /// <summary>Текущая сортировка словами — для подтверждения массового переименования.</summary>
        private static string SortOrderDescription(SortMode mode, bool descending)
        {
            string key = mode switch
            {
                SortMode.DateModified => Loc.T("date modified", "дата изменения", "fecha de modificación"),
                SortMode.Size => Loc.T("size", "размер", "tamaño"),
                SortMode.Type => Loc.T("type", "тип", "tipo"),
                _ => Loc.T("name", "имя", "nombre")
            };
            return key + (descending ? " ↓" : " ↑") + Loc.T(" (as in the viewer)", " (как в просмотре)", " (como en el visor)");
        }

        private void StripExifButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fileOperation != null || _currentIndex < 0 || ThumbnailOverlay.Visibility == Visibility.Visible) return;
            string path = _folderFiles[_currentIndex];
            if (!StripExifService.CanStrip(path))
            {
                MessageBox.Show(Loc.T("Metadata removal is not supported for this format.",
                    "Удаление метаданных не поддерживается для этого формата.", "No se admite eliminar metadatos en este formato."), "PhotoMusicViewer");
                return;
            }
            if (HasViewTransform) return;
            var options = new ImageSaveOptionsWindow(Window.GetWindow(this), path, strip: true);
            if (options.ShowDialog() != true) return;
            bool saveCopy = options.Copy, reencode = options.Reencode;
            _ = RunFileOperationAsync(Loc.T("Strip metadata", "Удаление метаданных", "Eliminar metadatos"), async operation =>
            {
                string saved = await BackgroundFileWorker.Run(() => StripExifService.SaveStripped(path, saveCopy, reencode, operation));
                CompleteImageSave(saved);
            });
        }

        // --- Обрезка (Crop) ---

        private bool _isCropMode;
        private FileVersion? _cropSourceVersion;
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
    if (_fileOperation != null || HasViewTransform) return;
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
            // Версия файла на момент начала обрезки: если его потом изменит синхронизация
            // или другая программа, рамка относится к другому снимку — сохранение откажет.
            _cropSourceVersion = null;
            if (_currentIndex >= 0)
            {
                try { _cropSourceVersion = FileVersion.Read(_folderFiles[_currentIndex]); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("PhotoView.CropVersion", ex); }
            }
            _isCropMode = true;
            ResetTransform();
            EnsureCropVisuals();

            CropOverlay.Visibility = Visibility.Visible;
            CropActionsPanel.Visibility = Visibility.Visible;
            RefreshCropIcon();
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
            RefreshCropIcon();
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
            MirrorButton.IsEnabled = enabled;
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
            RefreshRotationUi();
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
        private void CropCancelButton_Click(object sender, RoutedEventArgs e) { if (_fileOperation == null) ExitCropMode(); }

        private static Int32Rect MapCropToSource(Rect selection, Rect bounds, int width, int height)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || width <= 0 || height <= 0)
                throw new ArgumentException("Invalid crop image bounds.");
            double sx = width / bounds.Width, sy = height / bounds.Height;
            int x = Math.Clamp((int)Math.Round((selection.X - bounds.X) * sx), 0, width - 1);
            int y = Math.Clamp((int)Math.Round((selection.Y - bounds.Y) * sy), 0, height - 1);
            int w = Math.Clamp((int)Math.Round(selection.Width * sx), 1, width - x);
            int h = Math.Clamp((int)Math.Round(selection.Height * sy), 1, height - y);
            return new Int32Rect(x, y, w, h);
        }

        private void ApplyCrop()
        {
    if (_fileOperation != null) return;
            if (!_isCropMode || _currentIndex < 0) return;
            if (MainImage.Source is not BitmapSource source) return;
            if (_cropImageBounds.Width <= 0 || _cropImageBounds.Height <= 0) return;

            // На экране может показываться уменьшенная копия, а обрезается
            // оригинальный файл - пересчёт идёт в пикселях оригинала
            int sourceWidth = _currentWidth > 0 ? _currentWidth : source.PixelWidth;
            int sourceHeight = _currentHeight > 0 ? _currentHeight : source.PixelHeight;

            var cropRect = MapCropToSource(_cropRectDisplay, _cropImageBounds, sourceWidth, sourceHeight);
            int pw = cropRect.Width, ph = cropRect.Height;

            if (pw == sourceWidth && ph == sourceHeight)
            {
                // Рамка охватывает всё изображение — обрезать нечего
                ExitCropMode();
                return;
            }

            var path = _folderFiles[_currentIndex];

            // Обрезка JPEG без потерь: план считается по заголовку файла (быстро, без пикселей).
            JpegLossless.LosslessCropPlan? plan = null; string? unavailable = null;
            if (JpegLossless.IsJpeg(path))
            {
                if (!JpegLossless.HelperAvailable())
                    unavailable = Loc.T("the jpegtran helper (tools\\jpegtran, Windows x64 only) was not found.",
                                        "не найден помощник jpegtran (tools\\jpegtran, только Windows x64).",
                                        "no se encontró jpegtran (tools\\jpegtran, solo Windows x64).");
                else
                {
                    try
                    {
                        var p = JpegLossless.PlanCrop(path, new JpegLossless.PixelRect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height));
                        var (dw, dh) = JpegLossless.DisplaySize(p.Header);
                        if (dw == sourceWidth && dh == sourceHeight) plan = p;
                        else unavailable = Loc.T("the file size differs from the displayed image.", "размер файла не совпадает с показанным снимком.", "el tamaño no coincide con la imagen mostrada.");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                    {
                        AppLog.Debug("PhotoView.PlanLosslessCrop", ex);
                        unavailable = ex.Message;
                    }
                }
            }

            var dialog = new CropSaveDialog(Window.GetWindow(this), path, pw, ph, plan, unavailable);
            if (dialog.ShowDialog() != true) return; // «Отмена» — продолжаем обрезку
            bool replaceOriginal = dialog.ReplaceOriginal;
            var method = dialog.Method;
            var expectedVersion = _cropSourceVersion;

            var mode = _sortMode; bool descending = _sortDescending;
            _ = RunFileOperationAsync(Loc.T("Crop", "Обрезка", "Recortar"), async operation =>
            {
                string savedPath = await BackgroundFileWorker.Run(() => CropAndSaveService.Save(path, cropRect, replaceOriginal, method, expectedVersion, operation));
                operation.Finalizing(FileOperationStage.Refreshing);
                ExitCropMode();
                if (!replaceOriginal)
                {
                    if (!_folderFiles.Contains(savedPath, StringComparer.OrdinalIgnoreCase)) _folderFiles.Add(savedPath);
                    _currentIndex = _folderFiles.FindIndex(f => string.Equals(f, savedPath, StringComparison.OrdinalIgnoreCase));
                    var files = _folderFiles.ToList();
                    _folderFiles = await Task.Run(() => SortFileSnapshot(files, mode, descending));
                    _currentIndex = _folderFiles.FindIndex(f => string.Equals(f, savedPath, StringComparison.OrdinalIgnoreCase));
                }
            });
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