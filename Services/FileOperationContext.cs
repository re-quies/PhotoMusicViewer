using System;
using System.Threading;

namespace PhotoMusicViewer.Services
{
    public enum FileOperationStage { Preparing, Decoding, Encoding, Writing, Committing, Planning, Renaming, Refreshing }
    public sealed record FileOperationProgress(FileOperationStage Stage, bool CanCancel, int Processed, int Total);

    /// <summary>Отмена только на безопасных границах. Commit не прерывается;
    /// после него результат считается сохранённым независимо от позднего нажатия.</summary>
    public sealed class FileOperationContext : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<FileOperationProgress>? _observer;
        private FileOperationProgress _progress = new(FileOperationStage.Preparing, true, 0, 0);
        private bool _disposed;
        public FileOperationContext(Action<FileOperationProgress>? observer = null) { _observer = observer; }
        public CancellationToken Token => _cts.Token;
        public FileOperationProgress Progress { get { lock (_gate) return _progress; } }
        public bool Cancel()
        {
            lock (_gate)
            {
                if (_disposed || !_progress.CanCancel) return false;
                _cts.Cancel();
                return true;
            }
        }
        public void Checkpoint(FileOperationStage stage, int? processed = null, int? total = null)
        {
            FileOperationProgress progress;
            lock (_gate)
            {
                _cts.Token.ThrowIfCancellationRequested();
                _progress = progress = new(stage, true, processed ?? _progress.Processed, total ?? _progress.Total);
            }
            Notify(progress);
            _cts.Token.ThrowIfCancellationRequested();
        }
        public T Commit<T>(Func<T> action)
        {
            FileOperationProgress progress;
            lock (_gate)
            {
                _cts.Token.ThrowIfCancellationRequested();
                _progress = progress = _progress with { Stage = FileOperationStage.Committing, CanCancel = false };
            }
            Notify(progress);
            // Никаких проверок отмены внутри/после этой секции: файл уже мог измениться.
            return action();
        }
        public void Commit(Action action) => Commit(() => { action(); return true; });
        public void Finalizing(FileOperationStage stage, int? processed = null)
        {
            FileOperationProgress progress;
            lock (_gate)
            {
                _progress = progress = _progress with { Stage = stage, CanCancel = false,
                    Processed = processed ?? _progress.Processed };
            }
            Notify(progress);
        }
        private void Notify(FileOperationProgress progress)
        {
            try { _observer?.Invoke(progress); }
            catch (Exception ex) { AppLog.Debug("FileOperationContext.Progress", ex); }
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _cts.Dispose(); }
        }
    }
}
