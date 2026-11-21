using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Auth;

/// <summary>App id squatting, prompt flooding, revoke races and failed saves.</summary>
public sealed class ClientRegistryHardeningTests : IDisposable
{
    private const string AppId = "com.mindthaapp.voiceportal";
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeKeyProtector _protector = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    public void Dispose() => _dir.Dispose();

    private static readonly CallerIdentity Real = Callers.Packaged();
    private static readonly CallerIdentity Squatter = Callers.Unpackaged(@"C:\Temp\squat.exe");

    private ClientRegistry NewRegistry(ClientRegistryOptions? options = null) =>
        new(options ?? new ClientRegistryOptions { DataDirectory = _dir.Path }, _protector, _time);

    private static AuthRequestCreate Req(string appId = AppId) => new() { AppId = appId, AppName = "Voice Portal" };

    private async Task<string> IssueAsync(ClientRegistry registry, CallerIdentity caller, string appId = AppId)
    {
        var info = await registry.CreateRequestAsync(Req(appId), caller, _ct);
        await registry.ApproveAsync(info.RequestId, _ct);
        return (await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct))!.Token!;
    }

    // ---- 2: app id bound to the first approved program ----

    [Fact]
    public async Task OtherProgramCannotUseABoundAppId()
    {
        using var registry = NewRegistry();
        var token = await IssueAsync(registry, Real);

        var ex = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Squatter, _ct));
        Assert.Equal(ErrorCodes.AccessDenied, ex.Code);
        Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);

        // The binding survives a restart.
        using var reopened = NewRegistry();
        await Assert.ThrowsAsync<ApiException>(() => reopened.CreateRequestAsync(Req(), Squatter, _ct));
    }

    [Fact]
    public async Task SquatterDenialPendingAndQuotaDoNotAffectTheRealApp()
    {
        using var registry = NewRegistry(new ClientRegistryOptions { DataDirectory = _dir.Path, RequestsPerHourPerApp = 2 });
        var squat = await registry.CreateRequestAsync(Req(), Squatter, _ct);

        // The squatter's pending request does not block the real app.
        var real = await registry.CreateRequestAsync(Req(), Real, _ct);

        // Denying the squatter starts a cool-down for the squatter only.
        await registry.DenyAsync(squat.RequestId, _ct);
        var again = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Squatter, _ct));
        Assert.Equal(ErrorCodes.AccessDenied, again.Code);

        await registry.ApproveAsync(real.RequestId, _ct);
        var token = (await registry.WaitForRequestAsync(real.RequestId, TimeSpan.Zero, _ct))!.Token!;
        Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);
    }

    [Fact]
    public async Task SecondProgramsPendingRequestCannotBeApprovedOnceBound()
    {
        using var registry = NewRegistry();
        var real = await registry.CreateRequestAsync(Req(), Real, _ct);
        var squat = await registry.CreateRequestAsync(Req(), Squatter, _ct);

        await registry.ApproveAsync(real.RequestId, _ct);
        var token = (await registry.WaitForRequestAsync(real.RequestId, TimeSpan.Zero, _ct))!.Token!;

        var ex = await Assert.ThrowsAsync<ApiException>(() => registry.ApproveAsync(squat.RequestId, _ct));
        Assert.Equal(ErrorCodes.AccessDenied, ex.Code);
        var poll = await registry.WaitForRequestAsync(squat.RequestId, TimeSpan.Zero, _ct);
        Assert.Null(poll!.Token);
        Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);
        Assert.Single(await registry.ListAsync(_ct));
    }

    [Fact]
    public async Task SquatterQuotaIsSeparate()
    {
        using var registry = NewRegistry(new ClientRegistryOptions { DataDirectory = _dir.Path, RequestsPerHourPerApp = 1 });
        var squat = await registry.CreateRequestAsync(Req(), Squatter, _ct);
        await registry.DenyAsync(squat.RequestId, _ct);
        await registry.ReAllowAsync(AppId, _ct);
        await registry.CreateRequestAsync(Req(), Real, _ct);
    }

    [Fact]
    public async Task RemovingTheAppFreesItsAppId()
    {
        using var registry = NewRegistry();
        await IssueAsync(registry, Real);
        registry.RevokeApp(AppId);
        await registry.CreateRequestAsync(Req(), Squatter, _ct);
    }

    // ---- 5: revoke before the app collects its token ----

    [Fact]
    public async Task RevokeAppBeforeCollectionIssuesNoToken()
    {
        using var registry = NewRegistry();
        var pending = await registry.CreateRequestAsync(Req(), Squatter, _ct);
        var approved = await registry.CreateRequestAsync(Req(), Real, _ct);
        await registry.ApproveAsync(approved.RequestId, _ct);

        registry.RevokeApp(AppId);

        var poll = await registry.WaitForRequestAsync(approved.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Expired, poll!.Status);
        Assert.Null(poll.Token);
        Assert.Equal(AuthStatuses.Expired, (await registry.WaitForRequestAsync(pending.RequestId, TimeSpan.Zero, _ct))!.Status);
        await Assert.ThrowsAsync<ApiException>(() => registry.ApproveAsync(pending.RequestId, _ct));
        Assert.Empty(await registry.ListAsync(_ct));
    }

    [Fact]
    public async Task RevokeDeviceBeforeCollectionIssuesNoToken()
    {
        using var registry = NewRegistry();
        var approved = await registry.CreateRequestAsync(Req(), Real, _ct);
        await registry.ApproveAsync(approved.RequestId, _ct);

        registry.RevokeDevice(ClientPrincipal.LocalDevice);
        var poll = await registry.WaitForRequestAsync(approved.RequestId, TimeSpan.Zero, _ct);
        Assert.Equal(AuthStatuses.Expired, poll!.Status);
        Assert.Null(poll.Token);
        Assert.Empty(await registry.ListAsync(_ct));
    }

    // ---- 8: failed saves ----

    /// <summary>Holds clients.json open so the atomic replace fails until disposed.</summary>
    private FileStream LockRegistryFile() =>
        new(_dir.File(ClientRegistry.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task ValidTokenStaysValidWhenLastUsedCannotBeSaved()
    {
        using var registry = NewRegistry();
        var token = await IssueAsync(registry, Real);
        using (LockRegistryFile())
        {
            Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);
            Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);
        }

        Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);
    }

    [Fact]
    public async Task FailedDenySaveLeavesTheRequestPending()
    {
        using var registry = NewRegistry();
        await IssueAsync(registry, Real, "com.example.other");
        var info = await registry.CreateRequestAsync(Req(), Real, _ct);
        using (LockRegistryFile())
        {
            await Assert.ThrowsAnyAsync<IOException>(() => registry.DenyAsync(info.RequestId, _ct));
        }

        Assert.Equal(AuthStatuses.Pending, (await registry.WaitForRequestAsync(info.RequestId, TimeSpan.Zero, _ct))!.Status);
        var poll = registry.WaitForRequestAsync(info.RequestId, TimeSpan.FromSeconds(25), _ct);
        await registry.DenyAsync(info.RequestId, _ct);
        Assert.Equal(AuthStatuses.Denied, (await poll.WaitAsync(TimeSpan.FromSeconds(10)))!.Status);
    }

    [Fact]
    public async Task FailedRevokeSaveKeepsMemoryAndDiskInStep()
    {
        using var registry = NewRegistry();
        var token = await IssueAsync(registry, Real);
        var tokenId = (await registry.ListAsync(_ct)).Single().TokenId;
        using (LockRegistryFile())
        {
            await Assert.ThrowsAnyAsync<IOException>(() => registry.RevokeAsync(tokenId, _ct));
            Assert.ThrowsAny<IOException>(() => registry.RevokeApp(AppId));
        }

        Assert.True((await registry.ValidateAsync(token, _ct)).IsValid);
        await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req(), Squatter, _ct));

        await registry.RevokeAsync(tokenId, _ct);
        using var reopened = NewRegistry();
        Assert.Equal(ErrorCodes.TokenRevoked, (await reopened.ValidateAsync(token, _ct)).ErrorCode);
    }

    // ---- 4: temporary store failures never log apps out ----

    private ClientRegistryOptions FastRetry() =>
        new() { DataDirectory = _dir.Path, StoreRetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task TemporaryKeyFailureIsUnavailableAndKeepsTheKey()
    {
        string token;
        using (var registry = NewRegistry())
        {
            token = await IssueAsync(registry, Real);
        }

        var keyPath = _dir.File(HostKeyFile.FileName);
        var keyBefore = File.ReadAllBytes(keyPath);
        var flaky = new FakeKeyProtector { FailTransient = true };
        using var reopened = new ClientRegistry(FastRetry(), flaky, _time);

        var ex = await Assert.ThrowsAsync<ApiException>(() => reopened.ValidateAsync(token, _ct));
        Assert.Equal(ErrorCodes.AuthUnavailable, ex.Code);
        Assert.Equal(503, ErrorCatalog.Get(ex.Code).HttpStatus);
        Assert.Equal(keyBefore, File.ReadAllBytes(keyPath));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.bak"));

        flaky.FailTransient = false;
        Assert.True((await reopened.ValidateAsync(token, _ct)).IsValid);
    }

    [Fact]
    public async Task LockedRegistryFileAtStartIsUnavailableNotEmpty()
    {
        string token;
        using (var registry = NewRegistry())
        {
            token = await IssueAsync(registry, Real);
        }

        ClientRegistry reopened;
        using (new FileStream(_dir.File(ClientRegistry.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            reopened = new ClientRegistry(FastRetry(), _protector, _time);
            Assert.False(reopened.IsReady);
            var ex = await Assert.ThrowsAsync<ApiException>(() => reopened.ValidateAsync(token, _ct));
            Assert.Equal(ErrorCodes.AuthUnavailable, ex.Code);
            await Assert.ThrowsAsync<ApiException>(() => reopened.CreateRequestAsync(Req("com.other.app"), Real, _ct));
        }

        using (reopened)
        {
            Assert.True((await reopened.ValidateAsync(token, _ct)).IsValid);
            Assert.True(reopened.IsReady);
        }
    }

    [Fact]
    public async Task WrongLengthKeyIsTreatedAsDamaged()
    {
        File.WriteAllBytes(_dir.File(HostKeyFile.FileName), _protector.Protect([1, 2, 3]));
        using var registry = NewRegistry();
        Assert.True(registry.IsReady);
        Assert.Single(Directory.GetFiles(_dir.Path, "*.bak"));
        await IssueAsync(registry, Real);
    }

    // ---- 3: caps on pending requests, pruned rate-limit buckets ----

    [Fact]
    public async Task OneProgramCannotFloodPrompts()
    {
        using var registry = NewRegistry();
        for (var i = 0; i < 8; i++)
        {
            await registry.CreateRequestAsync(Req($"com.flood.app{i}"), Squatter, _ct);
        }

        var ex = await Assert.ThrowsAsync<ApiException>(() => registry.CreateRequestAsync(Req("com.flood.app8"), Squatter, _ct));
        Assert.Equal(ErrorCodes.RateLimited, ex.Code);
        Assert.Equal(429, ErrorCatalog.Get(ex.Code).HttpStatus);
        Assert.Equal(8, registry.PendingRequests.Count);

        // Another program still gets in.
        await registry.CreateRequestAsync(Req(), Real, _ct);
    }

    [Fact]
    public async Task HostCapsPendingRequestsOverall()
    {
        using var registry = NewRegistry();
        for (var i = 0; i < 16; i++)
        {
            await registry.CreateRequestAsync(Req($"com.many.app{i}"), Callers.Unpackaged($@"C:\T\p{i}.exe"), _ct);
        }

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            registry.CreateRequestAsync(Req("com.many.last"), Callers.Unpackaged(@"C:\T\last.exe"), _ct));
        Assert.Equal(ErrorCodes.RateLimited, ex.Code);
        Assert.True(ex.RetryAfterMs > 0);
    }

    [Fact]
    public async Task RateLimitBucketsArePruned()
    {
        using var registry = NewRegistry();
        for (var i = 0; i < 5; i++)
        {
            await registry.CreateRequestAsync(Req($"com.prune.app{i}"), Real, _ct);
        }

        Assert.Equal(5, registry.TrackedAttemptKeys);
        _time.Advance(TimeSpan.FromHours(1));
        _ = registry.PendingRequests;
        Assert.Equal(0, registry.TrackedAttemptKeys);
    }
}
