using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Notifications;

namespace Portal.AiHost.Core.Tests.Notifications;

public sealed class NotificationTests
{
    private sealed class FakeNotifier : IToastNotifier
    {
        public List<ToastRequest> Shown { get; } = [];
        public bool Succeeds { get; set; } = true;

        public bool Show(ToastRequest request)
        {
            if (Succeeds)
            {
                Shown.Add(request);
            }

            return Succeeds;
        }
    }

    private sealed class Rig
    {
        public FakeNotifier Notifier { get; } = new();
        public HostSettings Settings { get; set; } = new();
        public bool Fullscreen { get; set; }
        public bool WindowOpen { get; set; }
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        public NotificationCenter Center { get; }
        public NotificationEvents Events { get; }

        public Rig()
        {
            Center = new NotificationCenter(Notifier, () => Settings, () => Fullscreen, () => Now,
                windowOpen: () => WindowOpen);
            Events = new NotificationEvents(Center);
        }
    }

    private static ToastRequest Req(string kind, string title = "t") => new(kind, title, "body", "home");

    // ---- rate limiter --------------------------------------------------------------------------

    [Fact]
    public void The_same_kind_is_shown_once_per_gap_and_again_after_it()
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Downloads)));
        Assert.Equal(NotifyOutcome.RateLimited, r.Center.Raise(Req(NotificationKinds.Downloads)));
        r.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(NotifyOutcome.RateLimited, r.Center.Raise(Req(NotificationKinds.Downloads)));
        r.Now += TimeSpan.FromMinutes(1.5);
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Downloads)));
        Assert.Equal(2, r.Notifier.Shown.Count);
    }

    [Fact]
    public void Kinds_are_limited_separately()
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Downloads)));
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Problems)));
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Approvals)));
    }

    [Fact]
    public void A_notification_that_could_not_be_shown_does_not_start_the_gap()
    {
        var r = new Rig();
        r.Notifier.Succeeds = false;
        Assert.Equal(NotifyOutcome.Failed, r.Center.Raise(Req(NotificationKinds.Problems)));
        r.Notifier.Succeeds = true;
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Problems)));
    }

    // ---- full screen ---------------------------------------------------------------------------

    [Fact]
    public void Full_screen_drops_everything_except_approvals()
    {
        var r = new Rig { Fullscreen = true };
        Assert.Equal(NotifyOutcome.DroppedFullscreen, r.Center.Raise(Req(NotificationKinds.Downloads)));
        Assert.Equal(NotifyOutcome.DroppedFullscreen, r.Center.Raise(Req(NotificationKinds.Problems)));
        Assert.Equal(NotifyOutcome.Deferred, r.Center.Raise(Req(NotificationKinds.Approvals)));
        Assert.Empty(r.Notifier.Shown);
        Assert.Equal(1, r.Center.WaitingCount);
    }

    [Fact]
    public void Approvals_wait_for_the_end_of_full_screen_and_several_become_one()
    {
        var r = new Rig { Fullscreen = true };
        r.Events.OnApproval("Notes");
        r.Events.OnApproval("Mail");
        Assert.Equal(NotifyOutcome.Deferred, r.Center.Pump());
        Assert.Empty(r.Notifier.Shown);

        r.Fullscreen = false;
        Assert.Equal(NotifyOutcome.Shown, r.Center.Pump());
        var toast = Assert.Single(r.Notifier.Shown);
        Assert.Equal("approvals", toast.Page);
        Assert.Contains("2 apps", toast.Body);
        Assert.Equal(0, r.Center.WaitingCount);
        Assert.Equal(NotifyOutcome.Idle, r.Center.Pump());
    }

    [Fact]
    public void One_waiting_approval_keeps_its_own_text()
    {
        var r = new Rig { Fullscreen = true };
        r.Events.OnApproval("Notes");
        r.Fullscreen = false;
        r.Center.Pump();
        Assert.Contains("Notes", Assert.Single(r.Notifier.Shown).Body);
    }

    [Fact]
    public void A_rate_limited_approval_waits_for_the_gap_instead_of_being_lost()
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Shown, r.Events.OnApproval("Notes"));
        Assert.Equal(NotifyOutcome.Deferred, r.Events.OnApproval("Mail"));
        Assert.Equal(NotifyOutcome.RateLimited, r.Center.Pump());
        r.Now += NotificationCenter.DefaultMinGap + TimeSpan.FromSeconds(1);
        Assert.Equal(NotifyOutcome.Shown, r.Center.Pump());
        Assert.Contains("Mail", r.Notifier.Shown[1].Body);
    }

    [Fact]
    public void A_failed_full_screen_check_does_not_hide_notifications()
    {
        var notifier = new FakeNotifier();
        var c = new NotificationCenter(notifier, () => new HostSettings(), () => throw new InvalidOperationException());
        Assert.Equal(NotifyOutcome.Shown, c.Raise(Req(NotificationKinds.Problems)));
    }

    // ---- window open ---------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_shown_while_the_window_is_open_and_waiting_approvals_are_dropped()
    {
        var r = new Rig { Fullscreen = true };
        r.Events.OnApproval("Notes");
        r.Fullscreen = false;
        r.WindowOpen = true;
        Assert.Equal(NotifyOutcome.WindowOpen, r.Center.Pump());
        Assert.Equal(0, r.Center.WaitingCount);
        Assert.Equal(NotifyOutcome.WindowOpen, r.Events.OnApproval("Mail"));
        Assert.Equal(NotifyOutcome.WindowOpen, r.Center.Raise(Req(NotificationKinds.Downloads)));
        Assert.Empty(r.Notifier.Shown);
    }

    // ---- per-kind settings ---------------------------------------------------------------------

    [Fact]
    public void Every_kind_is_on_by_default_and_a_muted_kind_is_never_shown()
    {
        var r = new Rig();
        foreach (var kind in NotificationKinds.All)
        {
            Assert.True(NotificationCenter.IsEnabled(r.Settings, kind));
        }

        r.Settings = new HostSettings { MutedNotifications = [NotificationKinds.Downloads] };
        Assert.Equal(NotifyOutcome.Muted, r.Center.Raise(Req(NotificationKinds.Downloads)));
        Assert.Equal(NotifyOutcome.Shown, r.Center.Raise(Req(NotificationKinds.Problems)));
    }

    [Fact]
    public void Turning_approvals_off_clears_what_was_waiting()
    {
        var r = new Rig { Fullscreen = true };
        r.Events.OnApproval("Notes");
        r.Fullscreen = false;
        r.Settings = new HostSettings { MutedNotifications = [NotificationKinds.Approvals] };
        Assert.Equal(NotifyOutcome.Muted, r.Center.Pump());
        Assert.Equal(0, r.Center.WaitingCount);
    }

    [Fact]
    public void Unknown_muted_names_are_ignored_and_the_setting_round_trips_as_json()
    {
        var settings = new HostSettings { MutedNotifications = ["something_new", NotificationKinds.Approvals] };
        Assert.True(NotificationCenter.IsEnabled(settings, NotificationKinds.Downloads));
        var json = System.Text.Json.JsonSerializer.Serialize(settings, AiHostJsonContext.Default.HostSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, AiHostJsonContext.Default.HostSettings)!;
        Assert.False(NotificationCenter.IsEnabled(back, NotificationKinds.Approvals));
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize("{}", AiHostJsonContext.Default.HostSettings)!.MutedNotifications);
    }

    // ---- event to notification mapping ---------------------------------------------------------

    [Fact]
    public void A_finished_download_names_the_model_and_opens_the_models_page()
    {
        var r = new Rig();
        r.Events.OnDownload(new DownloadProgressEvent { CatalogueId = "x", State = "done" }, "Qwen 3");
        var t = Assert.Single(r.Notifier.Shown);
        Assert.Equal(NotificationKinds.Downloads, t.Kind);
        Assert.Equal("models", t.Page);
        Assert.Contains("Qwen 3", t.Body);
    }

    [Fact]
    public void A_failed_download_gives_a_plain_reason_never_the_raw_error()
    {
        var r = new Rig();
        r.Events.OnDownload(new DownloadProgressEvent { State = "failed", Message = "IOException: There is not enough space on the disk. 0x70" }, "Qwen 3");
        var t = Assert.Single(r.Notifier.Shown);
        Assert.Contains("not enough free space", t.Body);
        Assert.DoesNotContain("IOException", t.Body);
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("queued")]
    [InlineData("paused")]
    [InlineData("cancelled")]
    [InlineData("removed")]
    [InlineData("remove_failed")]
    public void Routine_download_states_stay_silent(string state)
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnDownload(new DownloadProgressEvent { State = state }, "m"));
        Assert.Empty(r.Notifier.Shown);
    }

    [Theory]
    [InlineData("connection reset by peer", "connection was lost")]
    [InlineData("hash mismatch", "not complete")]
    [InlineData("something odd", "Something went wrong")]
    public void Download_reasons_are_plain_words(string raw, string expected) =>
        Assert.Contains(expected, NotificationTexts.PlainDownloadReason(raw));

    [Fact]
    public void An_approval_request_names_the_app_and_opens_the_approvals_page()
    {
        var r = new Rig();
        r.Events.OnApproval("Notes");
        var t = Assert.Single(r.Notifier.Shown);
        Assert.Equal(NotificationKinds.Approvals, t.Kind);
        Assert.Equal("approvals", t.Page);
        Assert.Contains("Notes", t.Body);
    }

    [Fact]
    public void An_engine_that_ran_and_then_failed_notifies_once()
    {
        var r = new Rig();
        r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Ready));
        r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Loading, 0, "Restarting after a crash."));
        Assert.Equal(NotifyOutcome.Shown,
            r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Failed, 0, "Engine crashed 3 times; giving up.")));
        Assert.Equal(NotificationKinds.Problems, Assert.Single(r.Notifier.Shown).Kind);
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Failed)));
    }

    [Fact]
    public void A_failed_start_a_stop_and_an_idle_unload_stay_silent()
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Failed, 0, "no model")));
        r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Ready));
        r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Stopping));
        r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Stopped));
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnEngineState(new EngineStateChange(ModelKinds.Chat, EngineState.Failed)));
        Assert.Empty(r.Notifier.Shown);
    }

    private static HealthReport Report(string status, string? failTitle = null) => new()
    {
        Status = status,
        Checks = failTitle is null ? [] : [new HealthCheck { Status = HealthStatuses.Fail, Title = failTitle, Detail = "It is missing." }],
    };

    [Fact]
    public void Health_notifies_only_when_it_turns_to_fail()
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnHealth(Report(HealthStatuses.Ok))); // starting point
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnHealth(Report(HealthStatuses.Warn)));
        Assert.Equal(NotifyOutcome.Shown, r.Events.OnHealth(Report(HealthStatuses.Fail, "AI engine")));
        var t = Assert.Single(r.Notifier.Shown);
        Assert.Contains("AI engine", t.Body);
        Assert.Equal("home", t.Page);
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnHealth(Report(HealthStatuses.Fail, "AI engine"))); // still failing
        r.Events.OnHealth(Report(HealthStatuses.Ok));
        r.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(NotifyOutcome.Shown, r.Events.OnHealth(Report(HealthStatuses.Fail, "AI engine")));
    }

    [Fact]
    public void A_problem_already_present_at_the_first_check_is_left_to_the_window()
    {
        var r = new Rig();
        Assert.Equal(NotifyOutcome.Idle, r.Events.OnHealth(Report(HealthStatuses.Fail, "AI engine")));
        Assert.Empty(r.Notifier.Shown);
    }
}
