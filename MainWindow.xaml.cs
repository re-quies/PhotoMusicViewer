using System;
using System.IO;
using System.Linq;
using System.Windows;
using PhotoMusicViewer.Views;
using PhotoMusicViewer.Services;
using System.Windows.Interop;
using System.Windows.Media;

namespace PhotoMusicViewer
{
    public partial class MainWindow : Window
    {
        private readonly PhotoView _photoView = new();
        private readonly MusicView _musicView = new();
        private string? _pendingFilePath;

        private static readonly string[] AudioExtensions =
            { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".aac", ".m4a" };

        private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;
        private WindowState _preFullscreenState;
        private ResizeMode _preFullscreenResizeMode;
        public MainWindow()
        {
            InitializeComponent();

            _photoView.FullscreenRequested += OnFullscreenRequested;
            _photoView.ModeSwitchRequested += isMusic =>
            {
                if (isMusic) SwitchToMusic();
            };
            _musicView.ModeSwitchRequested += isMusic =>
            {
                if (!isMusic) SwitchToPhoto();
            };

            ModeContent.Content = _photoView;
        }

        private void SwitchToPhoto()
{
    ModeContent.Content = _photoView;
    _photoView.Focus();
}

private void SwitchToMusic()
{
    ModeContent.Content = _musicView;
    _musicView.Focus();
}

        public void OpenFileOnStartup(string path)
{
    _pendingFilePath = path;
    Loaded += (_, _) =>
    {
        if (_pendingFilePath == null) return;

        var ext = Path.GetExtension(_pendingFilePath).ToLowerInvariant();

        if (AudioExtensions.Contains(ext))
        {
            SwitchToMusic();
            _musicView.OpenFile(_pendingFilePath);
        }
        else
        {
            _photoView.OpenFile(_pendingFilePath);
        }
    };
}

        // --- Drag&drop файлов в окно ---

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppableFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            var path = GetDroppableFile(e);
            if (path == null) return;
            e.Handled = true;

            // Перетаскивание тоже может оставить след в "Недавних файлах" - чистим, как и после диалога открытия
            PrivacyCleanupService.RemoveFromRecentItems(path);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (AudioExtensions.Contains(ext))
            {
                SwitchToMusic();
                _musicView.OpenFile(path);
            }
            else
            {
                SwitchToPhoto();
                _photoView.OpenFile(path);
            }
        }

        private static string? GetDroppableFile(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;

            var path = files[0];
            if (!File.Exists(path)) return null;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return AudioExtensions.Contains(ext) || PhotoView.SupportedExtensions.Contains(ext)
                ? path
                : null;
        }

        private void OnFullscreenRequested(bool enable)
{
    if (enable)
    {
        _preFullscreenLeft = Left;
        _preFullscreenTop = Top;
        _preFullscreenWidth = Width;
        _preFullscreenHeight = Height;
        _preFullscreenState = WindowState;
        _preFullscreenResizeMode = ResizeMode;

        ResizeMode = ResizeMode.NoResize; // убирает невидимую зону resize по краям окна
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;

        var bounds = GetCurrentScreenBoundsInDips();
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }
    else
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        Left = _preFullscreenLeft;
        Top = _preFullscreenTop;
        Width = _preFullscreenWidth;
        Height = _preFullscreenHeight;
        WindowState = _preFullscreenState;
        ResizeMode = _preFullscreenResizeMode;
    }
}

// --- WinAPI для определения границ текущего монитора
// (замена System.Windows.Forms.Screen - убирает зависимость от WinForms) ---

private const int MONITOR_DEFAULTTONEAREST = 2;

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
private struct NativeRect { public int Left, Top, Right, Bottom; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
private struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public int Flags;
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

private static NativeRect GetMonitorBoundsInPixels(IntPtr hwnd)
{
    var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    var info = new MonitorInfo
    {
        Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>()
    };

    if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        return info.Monitor;

    // не удалось - считаем как основной монитор
    return new NativeRect
    {
        Left = 0,
        Top = 0,
        Right = (int)SystemParameters.PrimaryScreenWidth,
        Bottom = (int)SystemParameters.PrimaryScreenHeight
    };
}

private Rect GetCurrentScreenBoundsInDips()
{
    var hwnd = new WindowInteropHelper(this).Handle;
    var bounds = GetMonitorBoundsInPixels(hwnd); // физические пиксели монитора, на котором сейчас окно

    var source = PresentationSource.FromVisual(this);
    Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

    var topLeft = transform.Transform(new Point(bounds.Left, bounds.Top));
    var bottomRight = transform.Transform(new Point(bounds.Right, bounds.Bottom));

    return new Rect(topLeft, bottomRight);
}
    }
}