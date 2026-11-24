using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Scheduling;

/// <summary>Per-app pause and priority in the fair queue, on a manual clock.</summary>
public sealed class AppControlSchedulerTests : IDisposable
{
    private sealed class FakeControls : IAppControls
    {
        private readonly Dictionary<string, AppControl> _map = new();
        public event EventHandler? Changed;
        public AppControl Get(string appId) => _map.GetValueOrDefault(appId) ?? AppControl.Default;

        public Task SetAsync(string appId, bool? paused, AppPriority? priority, CancellationToken ct)
        {
            var c = Get(appId);
            _map[appId] = new AppControl(paused ?? c.Paused, priority ?? c.Priority);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private readonly ManualTimeProvider _time = new();
    private readonly FakeControls _controls = new();
    private readonly FairScheduler _scheduler;

    public AppControlSchedulerTests() =>
        _scheduler = new FairScheduler(new FairSchedulerOptions { AppControls = _controls }, _time);

    public void Dispose() => _scheduler.Dispose();

    private Task<ISchedulerLease> Acquire(string app) => _scheduler.AcquireAsync(
        new SchedulingRequest { AppId = app, Deadline = _time.GetUtcNow() + TimeSpan.FromHours(2) }, null, default);

    [Fact]
    public async Task HighGoesBeforeNormalBeforeLow()
    {
        await _controls.SetAsync("app.low", null, AppPriority.Low, default);
        await _controls.SetAsync("app.high", null, AppPriority.High, default);
        var running = await Acquire("app.busy");
        var low = Acquire("app.low");
        var normal = Acquire("app.normal");
        var high = Acquire("app.high");

        await running.DisposeAsync();
        Assert.True(high.IsCompletedSuccessfully);
        Assert.False(normal.IsCompleted);
        await (await high).DisposeAsync();
        Assert.True(normal.IsCompletedSuccessfully);
        Assert.False(low.IsCompleted);
        await (await normal).DisposeAsync();
        Assert.True(low.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SamePriorityKeepsArrivalOrder()
    {
        var running = await Acquire("app.busy");
        var first = Acquire("app.a");
        var second = Acquire("app.b");
        await running.DisposeAsync();
        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public async Task LowRequestWaitingLongIsPromotedAheadOfNewNormalOnes()
    {
        await _controls.SetAsync("app.low", null, AppPriority.Low, default);
        var running = await Acquire("app.busy");
        var low = Acquire("app.low");
        _time.Advance(TimeSpan.FromSeconds(61));
        var normal = Acquire("app.normal");

        // The low request now ranks as normal and arrived first, so it goes first.
        await running.DisposeAsync();
        Assert.True(low.IsCompletedSuccessfully);
        Assert.False(normal.IsCompleted);
    }

    [Fact]
    public async Task LowRequestIsNotPromotedBeforeTheAge()
    {
        await _controls.SetAsync("app.low", null, AppPriority.Low, default);
        var running = await Acquire("app.busy");
        var low = Acquire("app.low");
        _time.Advance(TimeSpan.FromSeconds(30));
        var normal = Acquire("app.normal");
        await running.DisposeAsync();
        Assert.True(normal.IsCompletedSuccessfully);
        Assert.False(low.IsCompleted);
    }

    [Fact]
    public async Task PausedAppNewRequestsAreRefused()
    {
        await _controls.SetAsync("app.a", true, null, default);
        var ex = await Assert.ThrowsAsync<ApiException>(() => Acquire("app.a"));
        Assert.Equal(ErrorCodes.AppPaused, ex.Code);
        Assert.Equal(0, _scheduler.Snapshot().Waiting);
    }

    [Fact]
    public async Task QueuedRequestsOfAPausedAppWaitAndRunningOnesFinish()
    {
        var running = await Acquire("app.a");
        var queued = Acquire("app.a");
        var other = Acquire("app.b");
        await _controls.SetAsync("app.a", true, null, default);

        await running.DisposeAsync();
        Assert.False(queued.IsCompleted);
        Assert.True(other.IsCompletedSuccessfully);
        await (await other).DisposeAsync();
        Assert.False(queued.IsCompleted);

        await _controls.SetAsync("app.a", false, null, default);
        Assert.True(queued.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ResumingStartsAWaitingRequestOnAFreeSlot()
    {
        var running = await Acquire("app.busy");
        var queued = Acquire("app.a");
        await _controls.SetAsync("app.a", true, null, default);
        await running.DisposeAsync();
        Assert.False(queued.IsCompleted);
        Assert.Equal(0, _scheduler.Snapshot().Running);
        await _controls.SetAsync("app.a", false, null, default);
        Assert.True(queued.IsCompletedSuccessfully);
    }
}
