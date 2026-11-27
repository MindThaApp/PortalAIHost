using PortalAIHost.App.Services;

namespace PortalAIHost.App.Tests;

public class UpdateNudgeTests
{
    private sealed class FakeSource(Func<UpdateQueryResult> answer) : IUpdateSource
    {
        public int Calls { get; private set; }

        public Task<UpdateQueryResult> QueryAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private sealed class FakeState : IUpdateNudgeState
    {
        public string? DismissedVersion { get; set; }
        public void SaveDismissed(string version) => DismissedVersion = version;
    }

    [Fact]
    public async Task Shows_the_version_when_an_update_exists()
    {
        var source = new FakeSource(() => UpdateQueryResult.Found("1.1.0.0"));
        var nudge = new UpdateNudgeService(source, new FakeState(), enabled: true);

        Assert.Equal("1.1.0.0", await nudge.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Asks_the_store_once_per_session()
    {
        var source = new FakeSource(() => UpdateQueryResult.Found("1.1.0.0"));
        var nudge = new UpdateNudgeService(source, new FakeState(), enabled: true);

        Assert.NotNull(await nudge.CheckAsync(CancellationToken.None));
        Assert.Null(await nudge.CheckAsync(CancellationToken.None));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_dismissed_version_stays_quiet_but_a_newer_one_shows()
    {
        var state = new FakeState();
        var first = new UpdateNudgeService(new FakeSource(() => UpdateQueryResult.Found("1.1.0.0")), state, enabled: true);
        var version = await first.CheckAsync(CancellationToken.None);
        first.Dismiss(version!);

        var sameVersion = new UpdateNudgeService(new FakeSource(() => UpdateQueryResult.Found("1.1.0.0")), state, enabled: true);
        Assert.Null(await sameVersion.CheckAsync(CancellationToken.None));

        var newer = new UpdateNudgeService(new FakeSource(() => UpdateQueryResult.Found("1.2.0.0")), state, enabled: true);
        Assert.Equal("1.2.0.0", await newer.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task No_check_when_disabled()
    {
        var source = new FakeSource(() => UpdateQueryResult.Found("1.1.0.0"));
        var nudge = new UpdateNudgeService(source, new FakeState(), enabled: false);

        Assert.Null(await nudge.CheckAsync(CancellationToken.None));
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false)] // unpackaged or dev run
    [InlineData(true, true, false, false, false, false)] // fake host run
    [InlineData(true, false, true, false, false, false)] // check run that skips first run
    [InlineData(true, false, false, true, false, false)] // check run with its own state folder
    [InlineData(true, false, false, false, true, false)] // screenshot or off-screen run
    public void The_gate_allows_only_a_plain_packaged_run(bool packaged, bool fake, bool skip, bool custom, bool shot, bool expected) =>
        Assert.Equal(expected, UpdateNudgeGate.IsEnabled(packaged, fake, skip, custom, shot));

    [Fact]
    public async Task No_bar_when_there_is_no_update_or_the_store_fails()
    {
        var none = new UpdateNudgeService(new FakeSource(() => UpdateQueryResult.NoUpdate), new FakeState(), enabled: true);
        Assert.Null(await none.CheckAsync(CancellationToken.None));

        var failed = new UpdateNudgeService(new FakeSource(() => UpdateQueryResult.Failed), new FakeState(), enabled: true);
        Assert.Null(await failed.CheckAsync(CancellationToken.None));

        var throws = new UpdateNudgeService(new FakeSource(() => throw new InvalidOperationException("no store")), new FakeState(), enabled: true);
        Assert.Null(await throws.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public void The_store_remembers_the_dismissed_version_in_one_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PortalAIHost.nudge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateNudgeStore(folder);
            Assert.Null(store.DismissedVersion);
            store.SaveDismissed("1.1.0.0");

            Assert.Equal("1.1.0.0", new UpdateNudgeStore(folder).DismissedVersion);
            Assert.Equal([UpdateNudgeStore.FileName], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_state_file_means_nothing_is_dismissed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PortalAIHost.nudge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, UpdateNudgeStore.FileName), "{ not json");

            Assert.Null(new UpdateNudgeStore(folder).DismissedVersion);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
