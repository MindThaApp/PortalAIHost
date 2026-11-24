using System.Buffers.Text;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Auth;

public sealed class ClientRegistryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingAudit _audit = new();
    private readonly FakeKeyProtector _protector = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    public void Dispose() => _dir.Dispose();

    private ClientRegistry NewRegistry(ClientRegistryOptions? options = null, IHostKeyProtector? protector = null) =>
        new(options ?? new ClientRegistryOptions { DataDirectory = _dir.Path }, protector ?? _protector, _time, _audit);

    private static AuthRequestCreate Req(string appId = "com.mindthaapp.voiceportal", string name = "Voice Portal") =>
        new() { AppId = appId, AppName = name, AppVersion = "1.3.0", Reason = "Meeting minutes" };

    private async Task<(string Token, string TokenId)> IssueAsync(ClientRegistry registry, string appId = "com.mindthaapp.voiceportal")
    {
        var info = await registry.CreateRequestAsync(Req(appId), Callers.Packaged(), _ct);
        await registry.ApproveAsync(info.RequestId, _ct);
        var result = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        Assert.NotNull(result);
        Assert.Equal(AuthStatuses.Approved, result.Status);
        return (result.Token!, result.TokenId!);
    }

    [Fact]
    public async Task ApprovedRequestIssuesTokenExactlyOnce()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        Assert.StartsWith("ar_", info.RequestId);
        Assert.Equal(_time.GetUtcNow().AddMinutes(10), info.ExpiresAt);

        var pending = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Pending, pending!.Status);
        Assert.Null(pending.Token);

        await registry.ApproveAsync(info.RequestId, _ct);
        var approved = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Approved, approved!.Status);
        Assert.True(TokenFormat.IsWellFormed(approved.Token));
        Assert.Equal(TokenFormat.TotalLength, approved.Token!.Length);

        // Replaying the poll never returns the token again.
        var replay = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Claimed, replay!.Status);
        Assert.Null(replay.Token);
        Assert.Equal(approved.TokenId, replay.TokenId);

        var validation = await registry.ValidateAsync("Bearer " + approved.Token, _ct);
        Assert.True(validation.IsValid);
        Assert.Equal("com.mindthaapp.voiceportal", validation.Principal!.AppId);
        Assert.Equal(ClientPrincipal.LocalDevice, validation.Principal.DeviceId);
        Assert.Equal(approved.TokenId, validation.Principal.TokenId);
    }

    [Fact]
    public async Task ConcurrentPollsAfterApprovalHandOutOneToken()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        await registry.ApproveAsync(info.RequestId, _ct);

        var polls = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct)))
            .ToArray();
        var results = await Task.WhenAll(polls);

        Assert.Single(results, r => r!.Token is not null);
        Assert.Equal(31, results.Count(r => r!.Status == AuthStatuses.Claimed));
        Assert.Single(await registry.ListAsync(_ct));
    }

    [Fact]
    public async Task NoPlaintextTokenIsPersistedOrLogged()
    {
        string token;
        AccessRequestResult? result;
        using (var registry = NewRegistry())
        {
            var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
            await registry.ApproveAsync(info.RequestId, _ct);
            result = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
            token = result!.Token!;
            await registry.ValidateAsync(token, _ct);
            await registry.RevokeAsync(result.TokenId!, _ct);
            await registry.ValidateAsync(token, _ct);
        }

        var secret = token[ApiInfo.TokenPrefix.Length..];
        var rawSecret = Base64Url.DecodeFromChars(secret);
        foreach (var file in Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(rawSecret), text, StringComparison.Ordinal);
            Assert.False(bytes.AsSpan().IndexOf(rawSecret) >= 0, $"raw token bytes found in {Path.GetFileName(file)}");
        }

        Assert.NotEmpty(_audit.Events);
        Assert.All(_audit.Events, e => Assert.DoesNotContain(secret, e.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostKeyIsStoredOnlyInProtectedForm()
    {
        using var registry = NewRegistry();
        await IssueAsync(registry);
        Assert.Equal(1, _protector.ProtectCalls);
        var blob = File.ReadAllBytes(_dir.File(HostKeyFile.FileName));
        Assert.StartsWith("FAKEDPAPI1", Encoding.ASCII.GetString(blob));
    }

    [Fact]
    public async Task RevokedTokenIsRejected()
    {
        using var registry = NewRegistry();
        var (token, tokenId) = await IssueAsync(registry);

        await registry.RevokeAsync(tokenId, _ct);
        var validation = await registry.ValidateAsync(token, _ct);
        Assert.False(validation.IsValid);
        Assert.Equal(ErrorCodes.TokenRevoked, validation.ErrorCode);

        // Still revoked after a restart.
        using var reopened = NewRegistry();
        Assert.Equal(ErrorCodes.TokenRevoked, (await reopened.ValidateAsync(token, _ct)).ErrorCode);
        Assert.True((await reopened.ListAsync(_ct)).Single().Revoked);
    }

    [Fact]
    public async Task RevokePerAppAndPerDevice()
    {
        using var registry = NewRegistry();
        var (voice, _) = await IssueAsync(registry, "com.mindthaapp.voiceportal");
        var (pdf, _) = await IssueAsync(registry, "com.mindthaapp.pdfportal");

        Assert.Equal(1, registry.RevokeApp("com.mindthaapp.voiceportal"));
        Assert.Equal(ErrorCodes.TokenRevoked, (await registry.ValidateAsync(voice, _ct)).ErrorCode);
        Assert.True((await registry.ValidateAsync(pdf, _ct)).IsValid);

        Assert.Equal(1, registry.RevokeDevice(ClientPrincipal.LocalDevice));
        Assert.Equal(ErrorCodes.TokenRevoked, (await registry.ValidateAsync(pdf, _ct)).ErrorCode);
        Assert.Equal(0, registry.RevokeDevice(ClientPrincipal.LocalDevice));
    }

    [Fact]
    public async Task ReplayedApprovalIsRejected()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        await registry.ApproveAsync(info.RequestId, _ct);

        var again = await Assert.ThrowsAsync<ApiException>(() => registry.ApproveAsync(info.RequestId, _ct));
        Assert.Equal(ErrorCodes.InvalidRequest, again.Code);
        await Assert.ThrowsAsync<ApiException>(() => registry.DenyAsync(info.RequestId, _ct));

        await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        await Assert.ThrowsAsync<ApiException>(() => registry.ApproveAsync(info.RequestId, _ct));
        Assert.Single(await registry.ListAsync(_ct));

        var unknown = await Assert.ThrowsAsync<ApiException>(() => registry.ApproveAsync("ar_nope", _ct));
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);
    }

    [Fact]
    public async Task UnknownAppGetsNoTokenWithoutApproval()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req("com.unknown.tool", "Tool"), Callers.Unpackaged(), _ct);

        var poll = registry.WaitForRequestAsync(info.RequestId, TimeSpan.FromSeconds(25), _ct);
        Assert.False(poll.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(25));
        var pending = await poll;
        Assert.Equal(AuthStatuses.Pending, pending!.Status);
        Assert.Null(pending.Token);

        _time.Advance(TimeSpan.FromMinutes(10));
        var expired = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Expired, expired!.Status);
        Assert.Null(expired.Token);
        await Assert.ThrowsAsync<ApiException>(() => registry.ApproveAsync(info.RequestId, _ct));

        Assert.Empty(await registry.ListAsync(_ct));
        var guess = await registry.ValidateAsync(TokenFormat.NewToken(), _ct);
        Assert.Equal(ErrorCodes.TokenInvalid, guess.ErrorCode);
        Assert.Equal(ErrorCodes.TokenInvalid, (await registry.ValidateAsync("pah_short", _ct)).ErrorCode);
        Assert.Equal(ErrorCodes.TokenInvalid, (await registry.ValidateAsync("", _ct)).ErrorCode);
    }

    [Fact]
    public async Task ApprovalNotCollectedBeforeExpiryIssuesNothing()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        await registry.ApproveAsync(info.RequestId, _ct);
        _time.Advance(TimeSpan.FromMinutes(10));

        var result = await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Expired, result!.Status);
        Assert.Null(result.Token);
        Assert.Empty(await registry.ListAsync(_ct));
    }

    [Fact]
    public async Task LongPollWakesOnDecision()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        var poll = registry.WaitForRequestAsync(info.RequestId, TimeSpan.FromSeconds(25), _ct);
        Assert.False(poll.IsCompleted);

        await registry.DenyAsync(info.RequestId, _ct);
        var result = await poll.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AuthStatuses.Denied, result!.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task LongPollCancellationThrows()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        using var cts = new CancellationTokenSource();
        var poll = registry.WaitForRequestAsync(info.RequestId, TimeSpan.FromSeconds(25), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task DenialStartsCooldownUntilReAllowedOrExpired()
    {
        using var registry = NewRegistry();
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        await registry.DenyAsync(info.RequestId, _ct);

        var refused = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct));
        Assert.Equal(ErrorCodes.AccessDenied, refused.Code);

        // The cool-down survives a restart.
        using (var reopened = NewRegistry())
        {
            await Assert.ThrowsAsync<ApiException>(() => reopened.CreateRequestAsync(Req(), Callers.Packaged(), _ct));
        }

        _time.Advance(TimeSpan.FromHours(23));
        await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct));
        _time.Advance(TimeSpan.FromHours(1));
        var allowed = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        await registry.DenyAsync(allowed.RequestId, _ct);

        await registry.ReAllowAsync("com.mindthaapp.voiceportal", _ct);
        await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
    }

    [Fact]
    public async Task OnePendingRequestPerAppAndHourlyLimit()
    {
        using var registry = NewRegistry(new ClientRegistryOptions { DataDirectory = _dir.Path, RequestsPerHourPerApp = 3 });
        var first = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        var dup = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct));
        Assert.Equal(ErrorCodes.RateLimited, dup.Code);
        Assert.True(dup.RetryAfterMs > 0);

        await registry.ApproveAsync(first.RequestId, _ct);
        for (var i = 0; i < 2; i++)
        {
            var r = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
            await registry.ApproveAsync(r.RequestId, _ct);
        }

        var limited = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct));
        Assert.Equal(ErrorCodes.RateLimited, limited.Code);

        // Another app is not affected; the first app may ask again an hour later.
        await registry.CreateRequestAsync(Req("com.mindthaapp.pdfportal", "PDF Portal"), Callers.Packaged(), _ct);
        _time.Advance(TimeSpan.FromHours(1));
        await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
    }

    [Fact]
    public async Task OtherWindowsUserIsRefused()
    {
        using var registry = NewRegistry();
        var ex = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Callers.OtherUser(), _ct));
        Assert.Equal(ErrorCodes.AccessDenied, ex.Code);
        Assert.Empty(registry.PendingRequests);
    }

    [Theory]
    [InlineData("", "Voice Portal", "app_id")]
    [InlineData("voiceportal", "Voice Portal", "app_id")]
    [InlineData("Com.Example.App", "Voice Portal", "app_id")]
    [InlineData("com.example.app", "", "app_name")]
    [InlineData("com.example.app", "Voice\u202EPortal", "app_name")]
    [InlineData("com.example.app", "Voice\nPortal", "app_name")]
    public async Task CreateValidatesDisplayedFields(string appId, string appName, string param)
    {
        using var registry = NewRegistry();
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            registry.CreateRequestAsync(new AuthRequestCreate { AppId = appId, AppName = appName }, Callers.Packaged(), _ct));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.Equal(param, ex.Param);
    }

    [Fact]
    public async Task ReApprovalRotatesTheAppToken()
    {
        using var registry = NewRegistry();
        var (first, _) = await IssueAsync(registry);
        var (second, _) = await IssueAsync(registry);

        Assert.Equal(ErrorCodes.TokenRevoked, (await registry.ValidateAsync(first, _ct)).ErrorCode);
        Assert.True((await registry.ValidateAsync(second, _ct)).IsValid);

        var (third, thirdId) = await registry.RotateAsync(second, _ct);
        Assert.NotEqual(second, third);
        Assert.Equal(ErrorCodes.TokenRevoked, (await registry.ValidateAsync(second, _ct)).ErrorCode);
        var principal = (await registry.ValidateAsync(third, _ct)).Principal;
        Assert.Equal(thirdId, principal!.TokenId);
        await Assert.ThrowsAsync<ApiException>(() => registry.RotateAsync(second, _ct));
    }

    [Fact]
    public async Task TokensSurviveRestartWithSameKey()
    {
        string token;
        using (var registry = NewRegistry())
        {
            (token, _) = await IssueAsync(registry);
        }

        using var reopened = NewRegistry();
        Assert.True((await reopened.ValidateAsync(token, _ct)).IsValid);
    }

    [Fact]
    public async Task UnreadableHostKeyIsBackedUpAndOldTokensStopWorking()
    {
        string token;
        using (var registry = NewRegistry())
        {
            (token, _) = await IssueAsync(registry);
        }

        var keyPath = _dir.File(HostKeyFile.FileName);
        var oldBlob = File.ReadAllBytes(keyPath);
        var failing = new FakeKeyProtector { FailUnprotect = true };
        using var reopened = NewRegistry(protector: failing);

        Assert.Equal(ErrorCodes.TokenInvalid, (await reopened.ValidateAsync(token, _ct)).ErrorCode);
        var backup = _dir.File(HostKeyFile.FileName + ".unreadable.bak");
        Assert.Equal(oldBlob, File.ReadAllBytes(backup));
        Assert.NotEqual(oldBlob, File.ReadAllBytes(keyPath));
        Assert.Single(await reopened.ListAsync(_ct));
    }

    [Fact]
    public async Task CallerBindingRequiresSamePackageOrExe()
    {
        using var registry = NewRegistry();
        var packaged = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        Assert.True(registry.IsSameCaller(packaged.RequestId, Callers.Packaged(pid: 9999)));
        Assert.False(registry.IsSameCaller(packaged.RequestId, Callers.Packaged("Other.App_123")));
        Assert.False(registry.IsSameCaller(packaged.RequestId, Callers.Unpackaged()));

        var exe = await registry.CreateRequestAsync(Req("com.example.tool", "Tool"), Callers.Unpackaged(), _ct);
        Assert.True(registry.IsSameCaller(exe.RequestId, Callers.Unpackaged(pid: 1)));
        Assert.False(registry.IsSameCaller(exe.RequestId, Callers.Unpackaged(@"C:\Other\evil.exe")));
        Assert.False(registry.IsSameCaller(exe.RequestId, Callers.OtherUser()));
    }

    [Fact]
    public async Task ApprovalPromptShowsAppOnDeviceAndCaller()
    {
        using var registry = NewRegistry();
        AccessRequestInfo? raised = null;
        registry.RequestPending += (_, i) => raised = i;
        var info = await registry.CreateRequestAsync(Req(), Callers.Packaged(), _ct);
        Assert.Same(info, raised);

        var prompt = ApprovalPrompts.For(info);
        Assert.Equal("Voice Portal on this PC", prompt.Title);
        Assert.Equal("verified: MindThaApp.VoicePortal_8wekyb3d8bbwe", prompt.CallerLine);
        Assert.True(prompt.CallerVerified);

        var unverified = ApprovalPrompts.CallerLine(Callers.Unpackaged());
        Assert.Equal(@"unverified: C:\Tools\tool.exe", unverified);
        Assert.Equal("Voice Portal on Laptop", ApprovalPrompts.For(info, "Laptop").Title);

        var control = ApprovalPrompts.ToControl(info);
        Assert.Equal(info.RequestId, control.RequestId);
        Assert.True(control.CallerVerified);
    }

    [Fact]
    public async Task AppControlsDefaultToActiveNormalAndSurviveARestart()
    {
        string tokenId;
        using (var registry = NewRegistry())
        {
            (_, tokenId) = await IssueAsync(registry);
            Assert.Equal(AppControl.Default, registry.Get("com.mindthaapp.voiceportal"));
            var raised = 0;
            registry.Changed += (_, _) => raised++;
            await registry.SetAsync("com.mindthaapp.voiceportal", true, AppPriority.Low, _ct);
            await registry.SetAsync("com.mindthaapp.voiceportal", null, AppPriority.High, _ct);
            Assert.Equal(2, raised);
            Assert.Equal(new AppControl(true, AppPriority.High), registry.Get("com.mindthaapp.voiceportal"));
        }

        using var reopened = NewRegistry();
        Assert.Equal(new AppControl(true, AppPriority.High), reopened.Get("com.mindthaapp.voiceportal"));
        Assert.Equal(AppControl.Default, reopened.Get("com.other.app"));

        // Pausing is not revoking: the token is still active.
        var list = await reopened.ListAsync(_ct);
        Assert.False(list.Single(c => c.TokenId == tokenId).Revoked);
    }

    [Fact]
    public async Task SettingControlsForAnUnknownAppFails()
    {
        using var registry = NewRegistry();
        var ex = await Assert.ThrowsAsync<ApiException>(() => registry.SetAsync("com.unknown.app", true, null, _ct));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }
}
