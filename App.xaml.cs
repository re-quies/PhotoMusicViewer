using System;
using System.Windows;
using System.Windows.Threading;
using PhotoMusicViewer.Services;

namespace PhotoMusicViewer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Ловим исключения из UI-потока (не приводят к мгновенному краху, но покажем причину)
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(Loc.T("Unhandled UI error", "Необработанная ошибка интерфейса", "Error de interfaz no controlado") + $":\n{args.Exception}",
                    Loc.T("PhotoMusicViewer - Error", "PhotoMusicViewer - ошибка", "PhotoMusicViewer - Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Ловим исключения из фоновых потоков (сюда чаще всего прилетает NAudio) —
            // это не остановит краш процесса (так работает .NET), но покажет причину перед падением
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                MessageBox.Show(Loc.T("Fatal background error", "Критическая фоновая ошибка", "Error fatal en segundo plano") + $":\n{args.ExceptionObject}",
                    Loc.T("PhotoMusicViewer - Fatal Error", "PhotoMusicViewer - критическая ошибка", "PhotoMusicViewer - Error fatal"), MessageBoxButton.OK, MessageBoxImage.Error);
            };

            var window = new MainWindow();

            if (e.Args.Length > 0)
            {
                window.OpenFileOnStartup(e.Args[0]);
            }

            window.Show();
        }
    }
}