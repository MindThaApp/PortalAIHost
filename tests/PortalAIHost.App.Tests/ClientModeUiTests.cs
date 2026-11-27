using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

/// <summary>Client mode in the window: link states, the one-time prompt, the Home PC card, pairing and device alerts.</summary>
public class ClientModeUiTests
{
    private static async Task WaitAsync(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();
        while (!done() && deadline.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(10);
        Assert.True(done(), "timed out");
    }

    private static async Task<(Rig Rig, ClientLinkViewModel Link)> LinkAsync(Action<FakeHostControl>? setup = null)
    {
        var rig = new Rig(FakeScenario.Loaded);
        rig.Host.ClientMode = true;
        rig.Host.SavedPairing = true;
        setup?.Invoke(rig.Host);
        await rig.Shell.ClientLink.RefreshAsync();
        return (rig, rig.Shell.ClientLink);
    }

    // ---- states ----

    [Fact]
    public async Task Server_mode_has_no_link_and_shows_the_local_model_blocks()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        await rig.Shell.ClientLink.RefreshAsync();
        Assert.False(rig.Shell.ClientLink.IsClientMode);
        Assert.Equal(ClientLinkKind.None, rig.Shell.ClientLink.Kind);
        Assert.False(rig.Shell.ClientLink.HideLocalModel);
    }

    [Fact]
    public async Task Connected_over_the_home_network_names_the_pc_and_the_round_trip()
    {
        var (rig, link) = await LinkAsync();
        using var _ = rig;
        Assert.Equal(ClientLinkKind.Connected, link.Kind);
        Assert.Equal("Vm_LinkConnectedLan", link.ChipText);
        Assert.Equal("Vm_LinkRtt:Desk PC|12", link.DetailText);
        Assert.False(link.NeedsAttention);
        Assert.True(link.HideLocalModel);
    }

    [Fact]
    public async Task Connected_through_the_relay_says_so()
    {
        var (rig, link) = await LinkAsync(h => h.ClientRoute = "relay");
        using var _ = rig;
        Assert.Equal("Vm_LinkConnectedRelay", link.ChipText);
    }

    [Theory]
    [InlineData("connecting", ClientLinkKind.Connecting, "Vm_LinkConnecting")]
    [InlineData("unreachable", ClientLinkKind.Unreachable, "Vm_LinkUnreachable")]
    [InlineData("stopped", ClientLinkKind.Unreachable, "Vm_LinkUnreachable")]
    [InlineData("revoked", ClientLinkKind.Revoked, "Vm_LinkRevoked")]
    [InlineData("fingerprint_changed", ClientLinkKind.NeedsRepair, "Vm_LinkRepair")]
    public async Task Each_link_state_has_its_own_chip(string state, ClientLinkKind kind, string chip)
    {
        var (rig, link) = await LinkAsync(h => h.ClientLinkState = state);
        using var _ = rig;
        Assert.Equal(kind, link.Kind);
        Assert.Equal(chip, link.ChipText);
        Assert.Equal(kind is ClientLinkKind.Unreachable or ClientLinkKind.Revoked or ClientLinkKind.NeedsRepair, link.NeedsAttention);
    }

    [Fact]
    public async Task Fingerprint_change_asks_to_pair_again_with_plain_words()
    {
        var (rig, link) = await LinkAsync(h => h.ClientLinkState = "fingerprint_changed");
        using var _ = rig;
        Assert.Equal("Vm_LinkRepairHelp:Desk PC", link.AttentionText);
    }

    [Fact]
    public async Task After_a_drop_the_last_seen_time_shows()
    {
        var (rig, link) = await LinkAsync();
        using var _ = rig;
        rig.Host.ClientLinkState = "unreachable";
        await link.RefreshAsync();
        Assert.StartsWith("Vm_LinkLastSeen", link.LastSeenText);
    }

    [Fact]
    public async Task While_the_laptop_model_serves_the_local_blocks_come_back_and_the_note_shows()
    {
        var (rig, link) = await LinkAsync(h => { h.ClientLinkState = "unreachable"; h.UsingLaptopModel = true; });
        using var _ = rig;
        Assert.True(link.UsingLaptopModel);
        Assert.Equal("Vm_LinkUsingLaptop", link.UsingLaptopText);
        Assert.False(link.HideLocalModel);
    }

    [Fact]
    public async Task Home_hides_the_download_panel_in_client_mode_and_shows_it_again_in_server_mode()
    {
        var rig = new Rig(FakeScenario.Fresh);
        using var _ = rig;
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        await rig.Shell.RefreshAsync();
        Assert.True(home.ShowOnboarding);
        rig.Host.ClientMode = true;
        rig.Host.SavedPairing = true;
        await rig.Shell.ClientLink.RefreshAsync();
        Assert.False(home.ShowOnboarding);
        Assert.False(home.ShowInstalled);
        await rig.Shell.ClientLink.RunHereCommand.ExecuteAsync(null);
        Assert.True(home.ShowOnboarding);
        home.Dispose();
    }

    [Fact]
    public async Task Tray_line_shows_the_pc_and_state_only_in_client_mode()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        var tray = new TrayPopupViewModel(rig.Host, rig.Ui, rig.Strings, link: rig.Shell.ClientLink);
        Assert.False(tray.HasLinkLine);
        rig.Host.ClientMode = true;
        rig.Host.SavedPairing = true;
        await rig.Shell.ClientLink.RefreshAsync();
        Assert.Equal("Vm_TrayLink:Desk PC|Vm_LinkConnectedLan", tray.LinkLine);
        rig.Host.ClientLinkState = "unreachable";
        await rig.Shell.ClientLink.RefreshAsync();
        Assert.Equal("Vm_TrayLink:Desk PC|Vm_LinkUnreachable", tray.LinkLine);
        tray.Dispose();
    }

    [Fact]
    public async Task A_burst_of_pushes_becomes_one_read()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        rig.Host.ClientMode = true;
        rig.Host.SavedPairing = true;
        for (var i = 0; i < 20; i++) rig.Host.CompletePairingNoop();
        await WaitAsync(() => rig.Shell.ClientLink.Status is not null);
        await Task.Delay(500);
        Assert.True(rig.Host.StatusReads <= 2, $"reads: {rig.Host.StatusReads}");
    }

    // ---- the one-time prompt ----

    [Fact]
    public async Task The_prompt_is_raised_once_when_unreachable_and_pending()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        rig.Host.ClientMode = true;
        rig.Host.SavedPairing = true;
        rig.Host.ClientLinkState = "unreachable";
        rig.Host.FallbackPromptPending = true;
        var raised = 0;
        rig.Shell.ClientLink.FallbackPromptRequested += () => raised++;
        await rig.Shell.ClientLink.RefreshAsync();
        await rig.Shell.ClientLink.RefreshAsync();
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task The_prompt_does_not_appear_while_connected_or_when_not_pending()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        rig.Host.ClientMode = true;
        rig.Host.SavedPairing = true;
        var raised = 0;
        rig.Shell.ClientLink.FallbackPromptRequested += () => raised++;
        rig.Host.FallbackPromptPending = true; // pending but the link is fine
        await rig.Shell.ClientLink.RefreshAsync();
        rig.Host.ClientLinkState = "unreachable";
        rig.Host.FallbackPromptPending = false;
        await rig.Shell.ClientLink.RefreshAsync();
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(true, "local")]
    [InlineData(false, "none")]
    public async Task The_answer_is_saved_and_then_the_host_is_told_the_prompt_was_shown(bool useLaptop, string saved)
    {
        var (rig, link) = await LinkAsync(h => { h.ClientLinkState = "unreachable"; h.FallbackPromptPending = true; });
        using var _ = rig;
        await link.AnswerFallbackPromptAsync(useLaptop);
        Assert.Equal(["setfallback:" + saved, "promptshown"], rig.Host.ClientCalls);
        Assert.False(rig.Host.FallbackPromptPending);
        Assert.Equal(saved, rig.Host.ClientFallbackValue);
    }

    // ---- Settings card ----

    private static async Task<(Rig Rig, SettingsViewModel Vm)> SettingsAsync(Action<FakeHostControl>? setup = null)
    {
        var rig = new Rig(FakeScenario.Loaded);
        setup?.Invoke(rig.Host);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        return (rig, vm);
    }

    [Fact]
    public async Task The_card_shows_the_paired_pc_the_route_and_the_saved_choices()
    {
        var (rig, vm) = await SettingsAsync(h =>
        {
            h.ClientMode = true;
            h.SavedPairing = true;
            h.ClientFallbackValue = "local";
            h.FallbackUnset = false;
            h.RelayModeValue = "manual";
            h.UseRelayValue = true;
        });
        using var _ = rig;
        Assert.True(vm.HomeInClientMode);
        Assert.Equal(SettingsViewModel.ModeHomePc, vm.ModeIndex);
        Assert.Equal("Desk PC", vm.HomePcName);
        Assert.Equal("Vm_LinkConnectedLan", vm.HomeRouteText);
        Assert.Equal(1, vm.FallbackIndex);
        Assert.Equal(1, vm.RelayModeIndex);
        Assert.True(vm.UseRelay);
        Assert.True(vm.ShowUseRelay);
        Assert.Empty(rig.Host.ClientCalls); // Showing the saved values sends nothing back.
    }

    [Fact]
    public async Task Fallback_relay_mode_and_session_relay_round_trip_through_the_host()
    {
        var (rig, vm) = await SettingsAsync(h => { h.ClientMode = true; h.SavedPairing = true; });
        using var _ = rig;
        vm.FallbackIndex = 1;
        await WaitAsync(() => rig.Host.ClientCalls.Contains("setfallback:local"));
        vm.RelayModeIndex = 1;
        await WaitAsync(() => rig.Host.ClientCalls.Contains("setrelaymode:manual"));
        vm.UseRelay = true;
        await WaitAsync(() => rig.Host.ClientCalls.Contains("userelay:True"));
        await WaitAsync(() => vm.FallbackIndex == 1 && vm.RelayModeIndex == 1 && vm.UseRelay);
        Assert.Equal("local", rig.Host.ClientFallbackValue);
        Assert.Equal("manual", rig.Host.RelayModeValue);
        Assert.True(rig.Host.UseRelayValue);
    }

    [Fact]
    public async Task Switching_the_mode_goes_through_setmode_both_ways()
    {
        var (rig, vm) = await SettingsAsync(h => h.SavedPairing = true);
        using var _ = rig;
        vm.ModeIndex = SettingsViewModel.ModeHomePc;
        await WaitAsync(() => rig.Host.ClientCalls.Contains("setmode:client"));
        await WaitAsync(() => vm.HomeInClientMode);
        vm.ModeIndex = SettingsViewModel.ModeThisPc;
        await WaitAsync(() => rig.Host.ClientCalls.Contains("setmode:server"));
        await WaitAsync(() => !vm.HomeInClientMode);
    }

    [Fact]
    public async Task Choosing_the_home_pc_without_a_pairing_opens_the_pairing_dialog_and_changes_nothing()
    {
        var (rig, vm) = await SettingsAsync();
        using var _ = rig;
        var asked = 0;
        vm.PairRequested += () => asked++;
        vm.ModeIndex = SettingsViewModel.ModeHomePc;
        Assert.Equal(1, asked);
        Assert.Equal(SettingsViewModel.ModeThisPc, vm.ModeIndex);
        Assert.Empty(rig.Host.ClientCalls);
    }

    [Fact]
    public async Task Unpair_asks_first_and_only_then_forgets_the_pc()
    {
        var (rig, vm) = await SettingsAsync(h => { h.ClientMode = true; h.SavedPairing = true; });
        using var _ = rig;
        rig.Dialogs.Answer = false;
        await vm.UnpairCommand.ExecuteAsync(null);
        Assert.DoesNotContain("unpair", rig.Host.ClientCalls);
        rig.Dialogs.Answer = true;
        await vm.UnpairCommand.ExecuteAsync(null);
        await WaitAsync(() => rig.Host.ClientCalls.Contains("unpair"));
        await WaitAsync(() => !vm.HomePaired);
        Assert.Equal("", vm.HomePcName);
    }

    // ---- pairing dialog ----

    private const string Link = "portalai://pair?h=192.168.1.20&p=48123&f=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq&c=ABCDEFGHJKLM&n=Desk%20PC";

    private static PairingViewModel Pairing(Rig rig) => new(rig.Host, rig.Ui, rig.Strings, "My laptop");

    [Fact]
    public async Task A_valid_link_shows_the_pc_name_and_a_short_fingerprint_before_anything_is_sent()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        vm.UseLink(Link);
        Assert.True(vm.OnConfirm);
        Assert.Equal("Desk PC", vm.PcName);
        Assert.Equal("ABCD EFGH IJKL", vm.FingerprintText);
        Assert.Empty(rig.Host.PairCalls);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_bad_link_gives_a_plain_error_and_stays_on_the_first_step()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        vm.UseLink("hello");
        Assert.True(vm.OnLink);
        Assert.Equal("Vm_PairErr_invalid_input", vm.Error);
    }

    [Fact]
    public void A_link_on_the_clipboard_only_highlights_the_button()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        vm.NoteClipboard(Link);
        Assert.True(vm.ClipboardHasLink);
        Assert.Equal("", vm.LinkText);
        vm.NoteClipboard("something else");
        Assert.False(vm.ClipboardHasLink);
    }

    [Fact]
    public async Task Confirm_pairs_sends_the_device_name_and_reports_the_pc()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        string? pc = null;
        vm.Paired += name => pc = name;
        vm.UseLink(Link);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("Desk PC", pc);
        var call = Assert.Single(rig.Host.PairCalls);
        Assert.Equal(Link, call.Link);
        Assert.Equal("My laptop", call.DeviceName);
        Assert.True(rig.Host.ClientMode);
    }

    [Theory]
    [InlineData("unreachable", null, "Vm_PairErr_unreachable")]
    [InlineData("certificate_mismatch", null, "Vm_PairErr_certificate_mismatch")]
    [InlineData("address_not_allowed", null, "Vm_PairErr_address_not_allowed")]
    [InlineData("already_paired", null, "Vm_PairErr_already_paired")]
    [InlineData("refused", "expired", "Vm_PairReason_expired")]
    [InlineData("refused", "wrong_code", "Vm_PairReason_wrong_code")]
    [InlineData("refused", "code_voided", "Vm_PairReason_code_voided")]
    [InlineData("refused", "no_offer", "Vm_PairReason_no_offer")]
    [InlineData("refused", "too_many_devices", "Vm_PairReason_too_many_devices")]
    [InlineData("refused", "something_new", "Vm_PairErr_refused")]
    [InlineData("brand_new_error", null, "Vm_PairErr_protocol_error")]
    public async Task Pairing_errors_come_back_as_plain_sentences_and_the_dialog_can_try_again(string error, string? reason, string text)
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        rig.Host.NextPairResult = new PairResult { Ok = false, Error = error, Reason = reason, Message = "english" };
        vm.UseLink(Link);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(text, vm.Error);
        Assert.True(vm.OnConfirm);
        Assert.False(rig.Host.ClientMode);
        await vm.ConfirmCommand.ExecuteAsync(null); // A second try works.
        Assert.True(rig.Host.ClientMode);
    }

    [Fact]
    public async Task While_pairing_the_dialog_shows_progress_and_cancel_goes_back_without_a_result()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        var started = new TaskCompletionSource();
        rig.Host.PairHook = async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var paired = false;
        vm.Paired += _ => paired = true;
        vm.UseLink(Link);
        var run = vm.ConfirmCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.OnWorking);
        vm.CancelCommand.Execute(null);
        await run;
        Assert.True(vm.OnConfirm);
        Assert.False(paired);
        Assert.False(rig.Host.ClientMode);
    }

    [Fact]
    public void A_device_name_outside_one_to_sixty_four_characters_cannot_be_confirmed()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Pairing(rig);
        vm.UseLink(Link);
        vm.DeviceName = "";
        Assert.False(vm.CanConfirm);
        vm.DeviceName = new string('x', 65);
        Assert.False(vm.CanConfirm);
        vm.DeviceName = "Laptop";
        Assert.True(vm.CanConfirm);
    }

    // ---- first run ----

    [Fact]
    public void First_run_use_my_home_pc_closes_the_welcome_and_asks_the_main_window_to_pair()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new FirstRunViewModel(rig.Shell, rig.Ui, rig.Strings);
        var closed = 0;
        vm.CloseRequested += () => closed++;
        vm.UseHomePcCommand.Execute(null);
        Assert.Equal(1, closed);
        Assert.True(rig.Shell.PairOnStart);
        vm.Dispose();
    }

    // ---- device alerts and the mute switch ----

    [Fact]
    public async Task A_device_alert_shows_a_notice_naming_the_device()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.Host.RaiseDeviceAlert("paired", "Kitchen laptop");
        await WaitAsync(() => rig.Shell.HasDeviceNotice);
        Assert.Equal("Vm_DeviceAlertPaired:Kitchen laptop", rig.Shell.DeviceNotice);
        rig.Host.RaiseDeviceAlert("relay_connect", "Phone");
        await WaitAsync(() => rig.Shell.DeviceNotice.StartsWith("Vm_DeviceAlertRelay"));
    }

    [Fact]
    public async Task A_muted_devices_kind_shows_no_notice()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var settings = await rig.Host.GetSettingsAsync();
        settings.MutedNotifications = [NotificationKinds.Devices];
        await rig.Host.SetSettingsAsync(settings);
        rig.Host.RaiseDeviceAlert("paired", "Kitchen laptop");
        await Task.Delay(200);
        Assert.False(rig.Shell.HasDeviceNotice);
    }

    [Fact]
    public async Task The_devices_switch_saves_only_its_own_kind_and_reset_turns_it_back_on()
    {
        var (rig, vm) = await SettingsAsync();
        using var _ = rig;
        Assert.True(vm.NotifyDevices);
        vm.NotifyDevices = false;
        Assert.Equal([NotificationKinds.Devices], (await rig.Host.GetSettingsAsync()).MutedNotifications);
        vm.ResetNotificationsCommand.Execute(null);
        Assert.True(vm.NotifyDevices);
        Assert.Null((await rig.Host.GetSettingsAsync()).MutedNotifications);
    }
}
