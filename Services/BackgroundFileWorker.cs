using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoMusicViewer.Services
{
    /// <summary>Отдельный STA-поток для WIC/энкодеров и Windows Shell (корзина).
    /// Результат возвращается Task; продолжение await выполняется на потоке вызывающего UI.</summary>
    internal static class BackgroundFileWorker
    {
        internal static Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(work()); }
                catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }) { IsBackground = true, Name = "PhotoMusic file operation" };
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            try { thread.Start(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            return completion.Task;
        }
        internal static Task Run(Action work) => Run(() => { work(); return true; });
    }
}
