using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Scheduling;

/// <summary>All tests run on a manual clock: no sleeps, no wall-clock waits.</summary>
public sealed class FairSchedulerTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FairScheduler _scheduler;

    public FairSchedulerTests() => _scheduler = new FairScheduler(time: _time);

    public void Dispose() => _scheduler.Dispose();

    private SchedulingRequest Req(string app, RequestPriority priority = RequestPriority.Interactive,
        TimeSpan? deadline = null, string kind = ModelKinds.Chat) => new()
        {
            AppId = app,
            Priority = priority,
            Kind = kind,
            Deadline = _time.GetUtcNow() + (deadline ?? TimeSpan.FromHours(1)),
        };

    private Task<ISchedulerLease> Acquire(string app, RequestPriority priority = RequestPriority.Interactive,
        CancellationToken ct = default, IProgress<QueueEvent>? progress = null, TimeSpan? deadline = null) =>
        _scheduler.AcquireAsync(Req(app, priority, deadline), progress, ct);

    /// <summary>Releases the running lease and returns the one that started next (exactly one must start).</summary>
    private static async Task<ISchedulerLease> HandOver(ISchedulerLease running, params Task<ISchedulerLease>[] waiting)
    {
        await running.DisposeAsync();
        var started = waiting.Where(t => t.IsCompletedSuccessfully).ToList();
        Assert.True(started.Count == 1, "exactly one waiter must start");
        return await started[0];
    }

    [Fact]
    public async Task SingleSlotRunsOneAtATime()
    {
        var a = await Acquire("app.a");
        var b = Acquire("app.b");
        Assert.False(b.IsCompleted);
        Assert.Equal(1, _scheduler.Snapshot().Running);
        Assert.Equal(1, _scheduler.Snapshot().Waiting);

        _time.Advance(TimeSpan.FromSeconds(3));
        await a.DisposeAsync();
        Assert.True(b.IsCompletedSuccessfully);
        var lease = await b;
        Assert.Equal(TimeSpan.FromSeconds(3), lease.QueueWait);
        await lease.DisposeAsync();
        Assert.Equal(0, _scheduler.Snapshot().Running);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task InteractiveGoesBeforeBatch()
    {
        var running = await Acquire("app.a");
        var batch = Acquire("app.b", RequestPriority.Batch);
        var interactive = Acquire("app.c");

        var next = await HandOver(running, batch, interactive);
        Assert.Same(await interactive, next);
        Assert.False(batch.IsCompleted);
        await next.DisposeAsync();
        Assert.True(batch.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AppsTakeTurns()
    {
        var running = await Acquire("app.busy");
        var x1 = Acquire("app.x");
        var x2 = Acquire("app.x");
        var x3 = Acquire("app.x");
        var y1 = Acquire("app.y");

        var order = new List<Task<ISchedulerLease>>();
        var pending = new List<Task<ISchedulerLease>> { x1, x2, x3, y1 };
        var current = running;
        while (pending.Count > 0)
        {
            current = await HandOver(current, pending.ToArray());
            var started = pending.Single(t => t.IsCompletedSuccessfully);
            order.Add(started);
            pending.Remove(started);
        }

        Assert.Equal(new[] { x1, y1, x2, x3 }, order);
        await current.DisposeAsync();
    }

    [Fact]
    public async Task BatchNeverTakesTheLastFreeSlot()
    {
        _scheduler.SetSlots(ModelKinds.Chat, 2);
        var batch1 = await Acquire("app.b", RequestPriority.Batch);
        var batch2 = Acquire("app.b", RequestPriority.Batch);
        Assert.False(batch2.IsCompleted);

        var interactive = Acquire("app.i");
        Assert.True(interactive.IsCompletedSuccessfully);
        Assert.Equal(2, _scheduler.Snapshot().Running);

        await (await interactive).DisposeAsync();
        Assert.False(batch2.IsCompleted);
        await batch1.DisposeAsync();
        Assert.True(batch2.IsCompletedSuccessfully);
        await (await batch2).DisposeAsync();
    }

    [Fact]
    public async Task SingleSlotLetsBatchRunWhenIdle()
    {
        var batch = Acquire("app.b", RequestPriority.Batch);
        Assert.True(batch.IsCompletedSuccessfully);
        await (await batch).DisposeAsync();
    }

    [Fact]
    public async Task WaitingBatchIsPromotedAfterFiveMinutes()
    {
        _scheduler.SetSlots(ModelKinds.Chat, 2);
        var batch1 = await Acquire("app.b", RequestPriority.Batch);
        var batch2 = Acquire("app.b", RequestPriority.Batch);

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(batch2.IsCompleted);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(batch2.IsCompletedSuccessfully);
        Assert.Equal(TimeSpan.FromMinutes(5), (await batch2).QueueWait);

        await batch1.DisposeAsync();
        await (await batch2).DisposeAsync();
    }

    [Fact]
    public async Task BatchIsNotStarvedByEndlessInteractiveWork()
    {
        var current = await Acquire("app.i");
        var batch = Acquire("app.b", RequestPriority.Batch);

        // Interactive work keeps arriving faster than it finishes, for 10 minutes.
        for (var minute = 0; minute < 10 && !batch.IsCompleted; minute++)
        {
            var newer = Acquire("app.i");
            _time.Advance(TimeSpan.FromMinutes(1));
            await current.DisposeAsync();
            if (batch.IsCompletedSuccessfully)
            {
                Assert.False(newer.IsCompleted);
                current = await batch;
                await current.DisposeAsync();
                current = await newer;
                break;
            }

            current = await newer;
        }

        Assert.True(batch.IsCompletedSuccessfully);
        Assert.InRange((await batch).QueueWait, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6));
        await current.DisposeAsync();
    }

    [Fact]
    public async Task PerAppAndPerHostQueueCaps()
    {
        var running = await Acquire("app.a");
        var queued = Enumerable.Range(0, 8).Select(_ => Acquire("app.a")).ToList();
        var full = await Assert.ThrowsAsync<ApiException>(() => Acquire("app.a"));
        Assert.Equal(ErrorCodes.QueueFull, full.Code);
        Assert.Equal(2000, full.RetryAfterMs);

        // Other apps still get in, up to 64 waiting in total.
        for (var i = 0; i < 56; i++)
        {
            queued.Add(Acquire("app.n" + (i / 8)));
        }

        Assert.Equal(64, _scheduler.Snapshot().Waiting);
        var hostFull = await Assert.ThrowsAsync<ApiException>(() => Acquire("app.fresh"));
        Assert.Equal(ErrorCodes.QueueFull, hostFull.Code);
        await running.DisposeAsync();
    }

    [Fact]
    public async Task QueuedRequestFailsAtItsDeadline()
    {
        var running = await Acquire("app.a");
        var waiting = Acquire("app.b", deadline: TimeSpan.FromSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(waiting.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<ApiException>(() => waiting);
        Assert.Equal(ErrorCodes.DeadlineExceeded, ex.Code);
        Assert.Equal(DeadlinePhases.Queued, ex.Details!["phase"].GetString());
        Assert.Equal(0, _scheduler.Snapshot().Waiting);

        var late = await Assert.ThrowsAsync<ApiException>(() => Acquire("app.c", deadline: TimeSpan.Zero));
        Assert.Equal(ErrorCodes.DeadlineExceeded, late.Code);
        await running.DisposeAsync();
        Assert.Equal(0, _scheduler.Snapshot().Running);
    }

    [Fact]
    public async Task CancelledRequestLeavesTheQueue()
    {
        var running = await Acquire("app.a");
        using var cts = new CancellationTokenSource();
        var cancelled = Acquire("app.b", ct: cts.Token);
        var next = Acquire("app.c");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, _scheduler.Snapshot().Waiting);

        var started = await HandOver(running, next);
        await started.DisposeAsync();
        Assert.Equal(0, _time.ActiveTimers);

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Acquire("app.d", ct: already.Token));
    }

    [Fact]
    public async Task LeaseReleasesOnce()
    {
        _scheduler.SetSlots(ModelKinds.Chat, 2);
        var a = await Acquire("app.a");
        var b = await Acquire("app.b");
        var c = Acquire("app.c");
        var d = Acquire("app.d");

        await a.DisposeAsync();
        await a.DisposeAsync();
        Assert.True(c.IsCompletedSuccessfully);
        Assert.False(d.IsCompleted);
        Assert.Equal(2, _scheduler.Snapshot().Running);

        await b.DisposeAsync();
        await (await c).DisposeAsync();
        await (await d).DisposeAsync();
    }

    [Fact]
    public async Task MoreSlotsStartWaitingWork()
    {
        var a = await Acquire("app.a");
        var b = Acquire("app.b");
        _scheduler.SetSlots(ModelKinds.Chat, 2);
        Assert.True(b.IsCompletedSuccessfully);
        await a.DisposeAsync();
        await (await b).DisposeAsync();
    }

    [Fact]
    public async Task KindsHaveSeparateSlots()
    {
        var chat = await Acquire("app.a");
        var embed = _scheduler.AcquireAsync(Req("app.a", kind: ModelKinds.Embedding), null, default);
        Assert.True(embed.IsCompletedSuccessfully);
        await chat.DisposeAsync();
        await (await embed).DisposeAsync();
    }

    [Fact]
    public async Task ReportsQueuedPositionThenStarted()
    {
        var running = await Acquire("app.a");
        var first = new ListProgress();
        var second = new ListProgress();
        var w1 = Acquire("app.b", progress: first);
        var w2 = Acquire("app.c", progress: second);

        Assert.Equal(new QueueEvent(QueueEventKind.Queued, 1), first.Events.Single());
        Assert.Equal(new QueueEvent(QueueEventKind.Queued, 2), second.Events.Single());

        var started = await HandOver(running, w1, w2);
        Assert.Equal(QueueEventKind.Started, first.Events.Last().Kind);
        await started.DisposeAsync();
        await (await w2).DisposeAsync();
    }

    [Fact]
    public async Task DisposeFailsQueuedWorkWithHostStopping()
    {
        var running = await Acquire("app.a");
        var waiting = Acquire("app.b");
        _scheduler.Dispose();
        var ex = await Assert.ThrowsAsync<ApiException>(() => waiting);
        Assert.Equal(ErrorCodes.HostStopping, ex.Code);
        await running.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotCountsByClassAndApp()
    {
        var running = await Acquire("app.a");
        var w = new[] { Acquire("app.a", RequestPriority.Batch), Acquire("app.b"), Acquire("app.b") };
        var snap = _scheduler.Snapshot();
        Assert.Equal(1, snap.Running);
        Assert.Equal(2, snap.WaitingInteractive);
        Assert.Equal(1, snap.WaitingBatch);
        Assert.Equal(2, snap.WaitingByApp["app.b"]);
        _scheduler.Dispose();
        foreach (var t in w)
        {
            await Assert.ThrowsAsync<ApiException>(() => t);
        }

        await running.DisposeAsync();
    }

    private sealed class ListProgress : IProgress<QueueEvent>
    {
        public List<QueueEvent> Events { get; } = new();

        public void Report(QueueEvent value) => Events.Add(value);
    }
}
