using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace PhotoMusicViewer.Services
{
    /// <summary>Несколько workers, bounded priority queue, объединение одинаковых path/size,
    /// общая отмена только когда нет потребителей, LRU с бюджетом в байтах.
    /// Фоновые задания (миниатюры, соседние фото) занимают не больше workers − 1 потоков:
    /// один всегда свободен для того, что пользователь ждёт прямо сейчас.</summary>
    internal sealed class ImageLoadCoordinator<T> : IDisposable where T : class
    {
        internal readonly record struct Key(string Path, int Side);
        internal readonly record struct Statistics(int Active, int Pending, int Cached, long CacheBytes);
        private sealed class Consumer
        {
            internal readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenRegistration Registration;
            internal bool Finished;
        }
        private sealed class Job
        {
            internal required Key Key;
            internal readonly CancellationTokenSource Cancellation = new();
            internal readonly List<Consumer> Consumers = new();
            internal LinkedListNode<Job>? Node;
            internal bool Foreground, Active;
            internal long Epoch;
        }
        private sealed record Entry(T Value, FileVersion Version, long Bytes, LinkedListNode<Key> Node);
        private readonly object _gate = new();
        private readonly Func<string, int, CancellationToken, T> _decode;
        private readonly Func<T, long> _weight;
        private readonly Func<string, FileVersion> _version;
        private readonly long _budget;
        private readonly int _maxPending;
        private readonly Dictionary<Key, Job> _jobs = new();
        private readonly LinkedList<Job> _pending = new();
        private readonly Dictionary<Key, Entry> _cache = new();
        private readonly LinkedList<Key> _lru = new();
        private readonly Task[] _workerTasks;
        private readonly List<Job> _active = new();
        private readonly int _workers;
        private int _activeBackground;
        private bool _disposed;
        private long _epoch, _bytes;
        internal ImageLoadCoordinator(Func<string, int, CancellationToken, T> decode, Func<T, long> weight,
            long budget = 128L * 1024 * 1024, int maxPending = 8, Func<string, FileVersion>? version = null,
            int workers = 1)
        {
            if (budget < 0 || maxPending < 1) throw new ArgumentOutOfRangeException(nameof(budget));
            if (workers < 1 || workers > 64) throw new ArgumentOutOfRangeException(nameof(workers));
            _decode = decode; _weight = weight; _budget = budget; _maxPending = maxPending; _version = version ?? FileVersion.Read;
            _workers = workers;
            _workerTasks = new Task[workers];
            for (int i = 0; i < workers; i++)
                _workerTasks[i] = Task.Factory.StartNew(WorkerLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Сколько потоков разумно отдать декодированию на этой машине:
        /// ядра минус одно под интерфейс и звук, от 2 до 4.</summary>
        internal static int DefaultWorkerCount => Math.Clamp(Environment.ProcessorCount - 1, 2, 4);

        internal int Workers => _workers;
        private static Key Normalize(string path, int side)
        {
            if (side < 0) throw new ArgumentOutOfRangeException(nameof(side));
            string full = Path.GetFullPath(path);
            // Windows paths are case-insensitive; Linux test paths retain case.
            return new Key(OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full, side);
        }
        internal Task<T> RequestAsync(string path, int side, bool foreground, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
            Key key = Normalize(path, side);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_jobs.TryGetValue(key, out var job) || job.Cancellation.IsCancellationRequested)
                {
                    if (_pending.Count >= _maxPending)
                    {
                        var victim = _pending.Last;
                        while (victim != null && victim.Value.Foreground) victim = victim.Previous;
                        if (victim == null && !foreground) return Task.FromCanceled<T>(new CancellationToken(true));
                        victim ??= _pending.Last;
                        CancelJob(victim!.Value);
                    }
                    job = new Job { Key = key, Foreground = foreground, Epoch = _epoch };
                    job.Node = foreground ? _pending.AddFirst(job) : _pending.AddLast(job);
                    _jobs[key] = job;
                    Monitor.PulseAll(_gate);
                }
                else if (foreground && !job.Active && !job.Foreground)
                {
                    job.Foreground = true;
                    _pending.Remove(job.Node!); job.Node = _pending.AddFirst(job);
                    Monitor.PulseAll(_gate);
                }
                var consumer = new Consumer();
                job.Consumers.Add(consumer);
                if (token.CanBeCanceled)
                    consumer.Registration = token.Register(() => CancelConsumer(job, consumer, token));
                return consumer.Completion.Task;
            }
        }
        private void CancelConsumer(Job job, Consumer consumer, CancellationToken token)
        {
            lock (_gate)
            {
                if (consumer.Finished) return;
                consumer.Finished = true; job.Consumers.Remove(consumer);
                consumer.Registration.Unregister(); consumer.Completion.TrySetCanceled(token);
                if (job.Consumers.Count == 0) CancelJob(job);
            }
        }
        private void CancelJob(Job job)
        {
            job.Cancellation.Cancel();
            if (job.Node != null) { _pending.Remove(job.Node); job.Node = null; }
            if (_jobs.TryGetValue(job.Key, out var registered) && ReferenceEquals(registered, job)) _jobs.Remove(job.Key);
            foreach (var consumer in job.Consumers)
            {
                consumer.Finished = true; consumer.Registration.Unregister();
                consumer.Completion.TrySetCanceled(new CancellationToken(true));
            }
            job.Consumers.Clear();
            if (!job.Active) job.Cancellation.Dispose();
        }
        /// <summary>Следующее задание для свободного потока или null. Срочные стоят в начале
        /// очереди; фоновое берётся, только если после этого останется поток для срочного.</summary>
        private Job? TakeNext()
        {
            var first = _pending.First;
            if (first == null) return null;
            var job = first.Value;
            if (!job.Foreground && _workers > 1 && _activeBackground >= _workers - 1) return null;
            _pending.RemoveFirst(); job.Node = null;
            job.Active = true; _active.Add(job);
            if (!job.Foreground) _activeBackground++;
            return job;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                Job? job;
                bool background;
                lock (_gate)
                {
                    while (true)
                    {
                        if (_disposed) return;
                        job = TakeNext();
                        if (job != null) break;
                        Monitor.Wait(_gate);
                    }
                    background = !job.Foreground;
                }
                T? value = null; Exception? error = null; FileVersion version = default;
                try
                {
                    var token = job.Cancellation.Token;
                    token.ThrowIfCancellationRequested();
                    version = _version(job.Key.Path);
                    lock (_gate)
                    {
                        if (_cache.TryGetValue(job.Key, out var hit))
                        {
                            if (hit.Version == version) { value = hit.Value; _lru.Remove(hit.Node); _lru.AddFirst(hit.Node); }
                            else RemoveEntry(job.Key);
                        }
                    }
                    if (value == null) value = _decode(job.Key.Path, job.Key.Side, token);
                    token.ThrowIfCancellationRequested();
                    // Проверка версии и при попадании в кэш: не присваиваем старым пикселям новую дату.
                    if (_version(job.Key.Path) != version) throw new IOException("Image changed during decoding; try opening it again.");
                }
                catch (Exception ex) { error = ex; }
                lock (_gate)
                {
                    if (_jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)) _jobs.Remove(job.Key);
                    _active.Remove(job);
                    if (background) _activeBackground--;
                    if (!_disposed && error == null && value != null && job.Epoch == _epoch && !job.Cancellation.IsCancellationRequested)
                    {
                        long cost;
                        try { cost = _weight(value); }
                        catch (Exception ex) { cost = -1; error = ex; }
                        if (cost < 0) error = new InvalidOperationException("Invalid image cache weight.");
                        else if (cost <= _budget)
                        {
                            RemoveEntry(job.Key);
                            while (_bytes > _budget - cost && _lru.Last != null) RemoveEntry(_lru.Last.Value);
                            var node = _lru.AddFirst(job.Key); _cache[job.Key] = new Entry(value, version, cost, node); _bytes += cost;
                        }
                    }
                    foreach (var consumer in job.Consumers)
                    {
                        consumer.Finished = true; consumer.Registration.Unregister();
                        if (error is OperationCanceledException || job.Cancellation.IsCancellationRequested || job.Epoch != _epoch)
                            consumer.Completion.TrySetCanceled(new CancellationToken(true));
                        else if (error != null) consumer.Completion.TrySetException(error);
                        else consumer.Completion.TrySetResult(value!);
                    }
                    job.Consumers.Clear(); job.Cancellation.Dispose();
                    Monitor.PulseAll(_gate);
                }
            }
        }
        private void RemoveEntry(Key key)
        {
            if (_cache.Remove(key, out var entry)) { _bytes -= entry.Bytes; _lru.Remove(entry.Node); }
        }
        internal Statistics Stats { get { lock (_gate) return new(_active.Count, _pending.Count, _cache.Count, _bytes); } }
        internal void Invalidate()
        {
            lock (_gate)
            {
                ++_epoch; _cache.Clear(); _lru.Clear(); _bytes = 0;
                foreach (var job in _pending.ToArray()) CancelJob(job);
                foreach (var job in _active.ToArray()) CancelJob(job);
            }
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; Invalidate(); _disposed = true; Monitor.PulseAll(_gate); }
        }
        internal Task Completion => Task.WhenAll(_workerTasks);
    }
}
