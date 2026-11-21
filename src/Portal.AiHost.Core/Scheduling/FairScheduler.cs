using System.Globalization;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Scheduling;

public sealed class FairSchedulerOptions
{
    public int MaxQueuedPerApp { get; init; } = ApiLimits.MaxQueuedPerApp;
    public int MaxQueuedPerHost { get; init; } = 64;

    /// <summary>A batch request waiting this long is treated as interactive (no starvation).</summary>
    public TimeSpan BatchPromotionAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Suggested back-off for queue_full.</summary>
    public int QueueFullRetryAfterMs { get; init; } = 2000;

    public int DefaultSlots { get; init; } = 1;
}

/// <summary>
/// Fair queue over N engine slots per model kind (architecture section 3):
/// <list type="bullet">
/// <item>Interactive before batch; round-robin across app ids within a class.</item>
/// <item>With 2 or more slots, batch never takes the last free slot.</item>
/// <item>Batch waiting 5 min or more is promoted to interactive, so batch work is never starved.</item>
/// <item>8 queued per app and 64 per host, then 429 queue_full. Deadlines end a queued request with
/// 504 deadline_exceeded (phase queued); cancellation removes it from the queue.</item>
/// </list>
/// No pre-emption. All decisions run under one lock; timers come from the TimeProvider, so tests drive
/// time with a fake clock.
/// </summary>
public sealed class FairScheduler : IScheduler, IDisposable
{
    private readonly FairSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, KindQueue> _kinds = new(StringComparer.Ordinal);
    private long _sequence;
    private int _waitingTotal;
    private bool _disposed;

    public FairScheduler(FairSchedulerOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new FairSchedulerOptions();
        _time = time ?? TimeProvider.System;
    }

    public Task<ISchedulerLease> AcquireAsync(SchedulingRequest request, IProgress<QueueEvent>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<ISchedulerLease>(ct);
        }

        var notes = new Notes();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetUtcNow();
            if (request.Deadline <= now)
            {
                throw DeadlineExceeded();
            }

            var queue = KindLocked(request.Kind);
            var appWaiting = _kinds.Values.Sum(k => k.WaitingFor(request.AppId));
            if (appWaiting >= _options.MaxQueuedPerApp || _waitingTotal >= _options.MaxQueuedPerHost)
            {
                throw new ApiException(ErrorCodes.QueueFull, "The host queue is full. Try again shortly.",
                    retryAfterMs: _options.QueueFullRetryAfterMs);
            }

            entry = new Entry(this, queue, request, progress, now, ++_sequence);
            queue.Enqueue(entry);
            _waitingTotal++;

            DispatchLocked(queue, notes);

            // Still queued (not started by the dispatch above): arm deadline, promotion and cancellation.
            if (queue.Contains(entry))
            {
                entry.ArmTimers(_time, now, _options.BatchPromotionAge);
                if (ct.CanBeCanceled)
                {
                    entry.CancelRegistration = ct.Register(static state =>
                    {
                        var (e, token) = ((Entry, CancellationToken))state!;
                        e.Owner.Cancel(e, token);
                    }, (entry, ct));
                }

                var position = queue.PositionOf(entry);
                if (progress is not null && queue.Contains(entry))
                {
                    notes.Report(() => progress.Report(new QueueEvent(QueueEventKind.Queued, position)));
                }
            }
        }

        Run(notes);
        return entry.Task;
    }

    public QueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            var byApp = new Dictionary<string, int>(StringComparer.Ordinal);
            int running = 0, interactive = 0, batch = 0;
            foreach (var k in _kinds.Values)
            {
                running += k.Running;
                foreach (var e in k.AllWaiting())
                {
                    if (e.Request.Priority == RequestPriority.Batch && !e.Promoted)
                    {
                        batch++;
                    }
                    else
                    {
                        interactive++;
                    }

                    byApp[e.Request.AppId] = byApp.GetValueOrDefault(e.Request.AppId) + 1;
                }
            }

            return new QueueSnapshot(running, interactive, batch, byApp);
        }
    }

    public void SetSlots(string kind, int slots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        var notes = new Notes();
        lock (_gate)
        {
            var queue = KindLocked(kind);
            queue.Slots = slots;
            DispatchLocked(queue, notes);
        }

        Run(notes);
    }

    /// <summary>Fails every queued request with host_stopping. Running leases stay valid until disposed.</summary>
    public void Dispose()
    {
        var notes = new Notes();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var k in _kinds.Values)
            {
                foreach (var e in k.AllWaiting().ToList())
                {
                    RemoveLocked(e);
                    var ex = new ApiException(ErrorCodes.HostStopping, "The host is stopping.");
                    notes.Add(() => e.Completion.TrySetException(ex));
                }
            }
        }

        Run(notes);
    }

    // ---- internals -----------------------------------------------------------------------------

    private KindQueue KindLocked(string kind)
    {
        if (!_kinds.TryGetValue(kind, out var q))
        {
            q = new KindQueue(_options.DefaultSlots);
            _kinds[kind] = q;
        }

        return q;
    }

    /// <summary>Starts as many waiting entries as the slots allow. Called under the lock.</summary>
    private void DispatchLocked(KindQueue queue, Notes notes)
    {
        var now = _time.GetUtcNow();
        while (queue.Running < queue.Slots)
        {
            var free = queue.Slots - queue.Running;

            // Interactive class: interactive entries plus batch entries old enough to be promoted.
            var next = queue.NextRoundRobin(e => e.IsInteractiveClass(now, _options.BatchPromotionAge));

            // Batch class: may not take the last free slot when there are 2 or more slots.
            if (next is null && (queue.Slots < 2 || free >= 2))
            {
                next = queue.NextRoundRobin(static _ => true);
            }

            if (next is null)
            {
                return;
            }

            RemoveLocked(next);
            queue.Running++;
            var lease = new Lease(this, queue, now - next.EnqueuedAt);
            var e = next;
            var granted = false;
            notes.Add(() =>
            {
                granted = e.Completion.TrySetResult(lease);
                if (!granted)
                {
                    // Cannot happen (only one path wins under the lock), but never leak a slot.
                    _ = lease.DisposeAsync();
                }
            });
            if (e.Progress is { } progress)
            {
                notes.Report(() =>
                {
                    if (granted)
                    {
                        progress.Report(new QueueEvent(QueueEventKind.Started, 0));
                    }
                });
            }
        }
    }

    private void RemoveLocked(Entry entry)
    {
        if (entry.Queue.Remove(entry))
        {
            _waitingTotal--;
        }

        entry.DisposeTimers();
    }

    private void Cancel(Entry entry, CancellationToken token)
    {
        lock (_gate)
        {
            if (!entry.Queue.Contains(entry))
            {
                return;
            }

            RemoveLocked(entry);
        }

        entry.Completion.TrySetCanceled(token);
    }

    private void OnDeadline(Entry entry)
    {
        lock (_gate)
        {
            if (!entry.Queue.Contains(entry))
            {
                return;
            }

            RemoveLocked(entry);
        }

        entry.Completion.TrySetException(DeadlineExceeded());
    }

    private void OnPromotion(Entry entry)
    {
        var notes = new Notes();
        lock (_gate)
        {
            if (!entry.Queue.Contains(entry))
            {
                return;
            }

            entry.Promoted = true;
            DispatchLocked(entry.Queue, notes);
        }

        Run(notes);
    }

    private void Release(KindQueue queue)
    {
        var notes = new Notes();
        lock (_gate)
        {
            queue.Running--;
            if (!_disposed)
            {
                DispatchLocked(queue, notes);
            }
        }

        Run(notes);
    }

    /// <summary>
    /// Runs the work collected under the lock: every completion first, then each progress report on its own.
    /// A throwing IProgress can neither stop other requests from starting nor leak a slot.
    /// </summary>
    private static void Run(Notes notes)
    {
        foreach (var complete in notes.Completions)
        {
            complete();
        }

        foreach (var report in notes.Reports)
        {
            try
            {
                report();
            }
#pragma warning disable CA1031 // A caller's progress handler must not break the scheduler.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>Completions and progress reports gathered under the lock, run after it is released.</summary>
    private sealed class Notes
    {
        public List<Action> Completions { get; } = new();
        public List<Action> Reports { get; } = new();

        public void Add(Action completion) => Completions.Add(completion);

        public void Report(Action report) => Reports.Add(report);
    }

    private static ApiException DeadlineExceeded() =>
        new(ErrorCodes.DeadlineExceeded, "The request deadline passed while it was queued.")
        {
            Details = new Dictionary<string, JsonElement>
            {
                ["phase"] = JsonSerializer.SerializeToElement(DeadlinePhases.Queued,
                    AiHostJsonContext.Default.String),
            },
        };

    /// <summary>Waiting entries of one model kind, kept per app for round-robin.</summary>
    private sealed class KindQueue(int slots)
    {
        private readonly Dictionary<string, LinkedList<Entry>> _byApp = new(StringComparer.Ordinal);

        // Apps in turn order; the app served last moves to the end.
        private readonly LinkedList<string> _turns = new();

        public int Slots { get; set; } = slots;
        public int Running { get; set; }

        public int WaitingFor(string appId) => _byApp.TryGetValue(appId, out var l) ? l.Count : 0;

        public IEnumerable<Entry> AllWaiting() => _byApp.Values.SelectMany(l => l);

        public bool Contains(Entry e) => e.Node?.List is not null;

        public void Enqueue(Entry e)
        {
            if (!_byApp.TryGetValue(e.Request.AppId, out var list))
            {
                list = new LinkedList<Entry>();
                _byApp[e.Request.AppId] = list;
                _turns.AddLast(e.Request.AppId);
            }

            e.Node = list.AddLast(e);
        }

        public bool Remove(Entry e)
        {
            if (e.Node?.List is not { } list)
            {
                return false;
            }

            list.Remove(e.Node);
            e.Node = null;
            if (list.Count == 0)
            {
                _byApp.Remove(e.Request.AppId);
                _turns.Remove(e.Request.AppId);
            }

            return true;
        }

        /// <summary>
        /// The first app in turn order that has an eligible entry gives its oldest eligible entry, and that app
        /// moves to the back of the turn order.
        /// </summary>
        public Entry? NextRoundRobin(Func<Entry, bool> eligible)
        {
            for (var node = _turns.First; node is not null; node = node.Next)
            {
                Entry? best = null;
                foreach (var e in _byApp[node.Value])
                {
                    if (eligible(e) && (best is null || e.Sequence < best.Sequence))
                    {
                        best = e;
                    }
                }

                if (best is not null)
                {
                    _turns.Remove(node);
                    _turns.AddLast(node);
                    return best;
                }
            }

            return null;
        }

        /// <summary>1-based place among waiting entries of this kind, by arrival.</summary>
        public int PositionOf(Entry entry) => AllWaiting().Count(e => e.Sequence <= entry.Sequence);
    }

    private sealed class Entry(
        FairScheduler owner, KindQueue queue, SchedulingRequest request, IProgress<QueueEvent>? progress,
        DateTimeOffset enqueuedAt, long sequence)
    {
        private ITimer? _deadlineTimer;
        private ITimer? _promotionTimer;

        public FairScheduler Owner { get; } = owner;
        public KindQueue Queue { get; } = queue;
        public SchedulingRequest Request { get; } = request;
        public IProgress<QueueEvent>? Progress { get; } = progress;
        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;
        public long Sequence { get; } = sequence;
        public bool Promoted { get; set; }
        public LinkedListNode<Entry>? Node { get; set; }
        public CancellationTokenRegistration CancelRegistration { get; set; }

        public TaskCompletionSource<ISchedulerLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ISchedulerLease> Task => Completion.Task;

        public bool IsInteractiveClass(DateTimeOffset now, TimeSpan promotionAge) =>
            Request.Priority == RequestPriority.Interactive || Promoted || now - EnqueuedAt >= promotionAge;

        public void ArmTimers(TimeProvider time, DateTimeOffset now, TimeSpan promotionAge)
        {
            _deadlineTimer = time.CreateTimer(static s => { var e = (Entry)s!; e.Owner.OnDeadline(e); }, this,
                Request.Deadline - now, Timeout.InfiniteTimeSpan);
            if (Request.Priority == RequestPriority.Batch)
            {
                _promotionTimer = time.CreateTimer(static s => { var e = (Entry)s!; e.Owner.OnPromotion(e); }, this,
                    promotionAge, Timeout.InfiniteTimeSpan);
            }
        }

        public void DisposeTimers()
        {
            _deadlineTimer?.Dispose();
            _promotionTimer?.Dispose();
            _deadlineTimer = null;
            _promotionTimer = null;

            // Unregister without waiting: this can run inside the token's own callback.
            CancelRegistration.Unregister();
        }

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Request.AppId}#{Sequence}");
    }

    private sealed class Lease(FairScheduler owner, KindQueue queue, TimeSpan queueWait) : ISchedulerLease
    {
        private int _released;

        public TimeSpan QueueWait { get; } = queueWait;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(queue);
            }

            return ValueTask.CompletedTask;
        }
    }
}
