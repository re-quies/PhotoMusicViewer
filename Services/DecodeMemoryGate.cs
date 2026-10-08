using System;
using System.Collections.Generic;
using System.Threading;

namespace PhotoMusicViewer.Services
{
    /// <summary>
    /// Общий бюджет памяти для одновременных декодирований вместо прежней блокировки
    /// «строго по одному». Миниатюры (сотни КБ) и превью идут параллельно на разных
    /// ядрах; несколько полноразмерных буферов одновременно не создаются — тяжёлое
    /// декодирование ждёт, пока освободится его доля памяти.
    ///
    /// Очередь честная (FIFO): большое декодирование не «голодает» за потоком мелких.
    /// Запрос больше всего бюджета выполняется, когда занятых долей нет (один).
    /// </summary>
    internal sealed class DecodeMemoryGate
    {
        private readonly object _gate = new();
        private readonly LinkedList<long> _waiters = new();
        private long _inUse;
        private int _holders;

        internal DecodeMemoryGate(long capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        /// <summary>Общий экземпляр: бюджет = предел рабочей памяти изображений.</summary>
        internal static DecodeMemoryGate Shared { get; } = new(ImageSafetyPolicy.MaxWorkingBytes);

        internal long Capacity { get; }
        internal long InUse { get { lock (_gate) return _inUse; } }
        internal int Holders { get { lock (_gate) return _holders; } }

        internal Lease Acquire(long bytes, CancellationToken token = default)
        {
            long share = Math.Clamp(bytes, 1, Capacity);
            lock (_gate)
            {
                var node = _waiters.AddLast(share);
                try
                {
                    while (!ReferenceEquals(_waiters.First, node) || (_holders > 0 && _inUse + share > Capacity))
                    {
                        token.ThrowIfCancellationRequested();
                        Monitor.Wait(_gate, 50); // проверка отмены без отдельной регистрации
                    }
                    token.ThrowIfCancellationRequested();
                    _inUse += share;
                    _holders++;
                }
                finally
                {
                    _waiters.Remove(node);
                    Monitor.PulseAll(_gate); // следующий в очереди мог стать первым
                }
            }
            return new Lease(this, share);
        }

        private void Release(long share)
        {
            lock (_gate)
            {
                _inUse -= share;
                _holders--;
                Monitor.PulseAll(_gate);
            }
        }

        internal sealed class Lease : IDisposable
        {
            private DecodeMemoryGate? _owner;
            private readonly long _share;
            internal Lease(DecodeMemoryGate owner, long share) { _owner = owner; _share = share; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_share);
        }
    }
}
