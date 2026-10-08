using System;
using System.Threading.Tasks;
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

            AppLog.Info("App.Startup");

            // Остатки записи после аварии (диспетчер задач, пропало питание): пути в журнале
            // зашифрованы DPAPI; уборка — в фоне, запуск не задерживает.
            TempArtifactJournal.Protect = SecretProtector.Protect;
            TempArtifactJournal.Unprotect = SecretProtector.Unprotect;
            _ = Task.Run(() =>
            {
                try { TempArtifactJournal.CleanupAbandoned(); }
                catch (Exception ex) { AppLog.Warn("App.CleanupAbandoned", ex); }
            });

            // Язык — до создания окон, чтобы интерфейс сразу открылся на нужном языке.
            Loc.SetLanguage(Loc.ResolveStartup(AppPreferences.Current.Language));
            // Переключение кнопкой EN/RU/ES запоминается, если это включено в настройках.
            Loc.LanguageChanged += () => AppPreferences.RememberLanguage(Loc.ToStartup(Loc.Language));

            // Настройки перевода читаются с диска только если пользователь сам включил сохранение
            TranslationConfig.LoadFromDiskIfPresent();

            // Ловим исключения из UI-потока (не приводят к мгновенному краху, но покажем причину)
            DispatcherUnhandledException += (_, args) =>
            {
                AppLog.Error("DispatcherUnhandledException", args.Exception);
                MessageBox.Show(Loc.T("Unhandled UI error", "Необработанная ошибка интерфейса", "Error de interfaz no controlado") + $":\n{args.Exception}",
                    Loc.T("PhotoMusicViewer - Error", "PhotoMusicViewer - ошибка", "PhotoMusicViewer - Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Ловим исключения из фоновых потоков (сюда чаще всего прилетает NAudio) —
            // это не остановит краш процесса (так работает .NET), но покажет причину перед падением
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                AppLog.Error("UnhandledException", args.ExceptionObject as Exception,
                    args.IsTerminating ? "процесс завершается" : null);
                MessageBox.Show(Loc.T("Fatal background error", "Критическая фоновая ошибка", "Error fatal en segundo plano") + $":\n{args.ExceptionObject}",
                    Loc.T("PhotoMusicViewer - Fatal Error", "PhotoMusicViewer - критическая ошибка", "PhotoMusicViewer - Error fatal"), MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Исключение из «забытой» задачи (fire-and-forget) раньше исчезало бесследно
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Warn("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            var window = new MainWindow();

            if (e.Args.Length > 0)
            {
                // Самый частый способ открытия — двойной клик в Проводнике. Проводник сам
                // записывает файл в «Недавние», поэтому чистим так же, как после диалога
                // открытия и перетаскивания — если пользователь включил уборку в настройках
                // (по умолчанию выключена, тогда вызов ничего не делает).
                RecentTracesService.EraseAfterShellLaunch(e.Args[0]);
                window.OpenFileOnStartup(e.Args[0]);
            }

            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Скопированное приложением (и картинка, и текст) не переживает выход,
            // если пользователь с тех пор не скопировал в буфер что-то своё.
            PrivacyClipboard.ClearOwnedContent();
            RecentTracesService.FinishPendingShellLaunchErase();
            base.OnExit(e);
        }
    }
}