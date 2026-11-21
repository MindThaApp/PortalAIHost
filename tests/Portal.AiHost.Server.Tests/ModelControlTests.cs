using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Store;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>Model listing, downloads, remove, settings and idle policy over the real control pipe.</summary>
public sealed class ModelControlTests
{
    private const long GiB = 1L << 30;

    private sealed class Probe(bool battery) : IHardwareProbe
    {
        public Task<HardwareProfile> ProbeAsync(CancellationToken ct) => Task.FromResult(new HardwareProfile("windows", "x64", 12,
            64 * GiB, [new GpuProfile("Test GPU", "amd", "0x1", "31.0", 8 * GiB, 0)], [], "fp"));

        public MemorySnapshot GetMemory() => new(64 * GiB, 50 * GiB, [(long)(7.5 * GiB)]);

        public bool IsOnBattery => battery;
    }

    /// <summary>Serves one fixed body for every GET, with range support for resumes.</summary>
    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var part = body.AsSpan((int)from).ToArray();
            var r = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(part) };
            r.Content.Headers.ContentLength = part.Length;
            if (from > 0)
            {
                r.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            }

            return Task.FromResult(r);
        }
    }

    private static CatalogueEntry Entry(byte[] body) => new()
    {
        Id = "tiny-chat", DisplayName = "Tiny chat", Kind = ModelKinds.Chat, Quality = 3, HfRepo = "test/tiny",
        HfCommit = new string('a', 40), ContextMax = 8192, ContextDefault = 4096,
        Memory = new ModelMemory { WeightsBytes = 2 * GiB, NonExpertBytes = 2 * GiB, NLayers = 24, KvBytesPer1kCtx = 64L << 20, ComputeOverheadBytes = 256L << 20 },
        Files = [new CatalogueFile { Name = "tiny.gguf", Size = body.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(body)), Urls = ["https://example.invalid/tiny.gguf"] }],
    };

    /// <summary>A fake engine whose stop waits for the test, like a real engine that takes seconds to exit.</summary>
    private sealed class SlowStopSupervisor(FakeEngineSupervisor inner) : IEngineSupervisor
    {
        public TaskCompletionSource StopGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Kind => inner.Kind;
        public EngineState State => inner.State;
        public EngineLaunchSpec? Current => inner.Current;
        public event EventHandler<EngineStateChange>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public Task StartAsync(EngineLaunchSpec spec, CancellationToken ct) => inner.StartAsync(spec, ct);

        public async Task StopAsync(CancellationToken ct)
        {
            await StopGate.Task.WaitAsync(ct).ConfigureAwait(false);
            await inner.StopAsync(ct).ConfigureAwait(false);
        }

        public Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest r, CancellationToken ct) => inner.ChatAsync(r, ct);
        public IAsyncEnumerable<ChatCompletionChunk> StreamChatAsync(ChatCompletionRequest r, CancellationToken ct) => inner.StreamChatAsync(r, ct);
        public Task<EmbeddingsResponse> EmbedAsync(EmbeddingsRequest r, CancellationToken ct) => inner.EmbedAsync(r, ct);
        public Task<int> CountTokensAsync(TokenizeRequest r, CancellationToken ct) => inner.CountTokensAsync(r, ct);

        public ValueTask DisposeAsync()
        {
            StopGate.TrySetResult();
            return inner.DisposeAsync();
        }
    }

    private static async Task<(HostShell Shell, ModelStore Store)> StartAsync(TestRoot root, IReadOnlyList<CatalogueEntry>? catalogue,
        byte[]? body = null, bool battery = false, IEngineSupervisor? chat = null, long headroom = 0)
    {
        var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var shell = await HostShell.StartAsync(new HostShellOptions
        {
            Paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? root.Path : null)!,
            Supervisors = new Dictionary<string, IEngineSupervisor>
            {
                [ModelKinds.Chat] = chat ?? new FakeEngineSupervisor(ModelKinds.Chat),
                [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
            },
            KeyProtector = new TestKeyProtector(),
            CallerResolver = new SameUserResolver(),
            QuitGrace = TimeSpan.FromMilliseconds(200),
            Hardware = new Probe(battery),
            Store = store,
            Catalogue = catalogue,
            CreateDownloader = w => new ModelDownloader(new HttpClient(new BytesHandler(body ?? [])), w,
                options: new DownloadOptions { FixedHeadroomBytes = headroom, PreventSleep = false, MaxAttempts = 1 }),
        }) ?? throw new InvalidOperationException("pipe already owned");
        return (shell, store);
    }

    [Fact(Timeout = 30000)]
    public async Task Without_a_trusted_list_the_listing_says_unavailable_and_downloads_are_refused()
    {
        using var root = new TestRoot();
        var (shell, _) = await StartAsync(root, catalogue: null);
        await using var _s = shell;
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        var listing = await client.ListModelsAsync();
        Assert.False(listing.CatalogueAvailable);
        Assert.Empty(listing.Models);
        Assert.StartsWith(root.Path, listing.ModelsFolder!, StringComparison.OrdinalIgnoreCase);
        Assert.True(listing.FreeBytes > 0);
        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => client.StartDownloadAsync("anything"));
        Assert.Equal(ErrorCodes.FeatureDisabled, ex.Code);
    }

    [Fact(Timeout = 30000)]
    public async Task A_download_without_room_on_the_drive_is_refused_up_front_and_writes_nothing()
    {
        using var root = new TestRoot();
        var body = RandomNumberGenerator.GetBytes(4096);
        var (shell, store) = await StartAsync(root, [Entry(body)], body, headroom: 1L << 60);
        await using var _s = shell;
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => client.StartDownloadAsync("tiny-chat"));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.Contains("GB", ex.Message);
        Assert.Null(await store.GetAsync("tiny-chat", CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task A_model_whose_engine_is_still_stopping_after_a_switch_cannot_be_removed()
    {
        using var root = new TestRoot();
        var body = RandomNumberGenerator.GetBytes(4096);
        var chat = new SlowStopSupervisor(new FakeEngineSupervisor(ModelKinds.Chat));
        var (shell, store) = await StartAsync(root, [Entry(body)], body, chat: chat);
        await using var _s = shell;
        var fileA = Path.Combine(root.Path, "a.gguf");
        var fileB = Path.Combine(root.Path, "b.gguf");
        File.WriteAllBytes(fileA, body);
        File.WriteAllBytes(fileB, body);
        var a = await store.AddExternalAsync(fileA, ModelKinds.Chat, EngineKinds.LlamaServer, CancellationToken.None);
        var b = await store.AddExternalAsync(fileB, ModelKinds.Chat, EngineKinds.LlamaServer, CancellationToken.None);
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        await using var second = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        await client.ActivateModelAsync(a.Id);
        await chat.StartAsync(new EngineLaunchSpec { Kind = ModelKinds.Chat, ModelId = a.Id, ModelFiles = [fileA] }, CancellationToken.None);
        Assert.Equal(EngineState.Ready, chat.State);

        // Switch to B: the active slot moves at once, the old engine is still stopping with A open.
        var switching = client.ActivateModelAsync(b.Id);
        while (shell.Runtime.GetActive(ModelKinds.Chat)?.ModelId != b.Id)
        {
            await Task.Delay(10);
        }

        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => second.ExchangeAsync(ControlTypes.RemoveModel,
            JsonSerializer.SerializeToElement(new RemoveModelCommand { CatalogueId = a.Id }, AiHostJsonContext.Default.RemoveModelCommand)));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.NotNull(await store.GetAsync(a.Id, CancellationToken.None));

        // The store itself also holds the in-use mark while the engine runs with A.
        Assert.All((await store.GetAsync(a.Id, CancellationToken.None))!.Files, f => Assert.Equal(StoredFileState.InUse, f.State));

        chat.StopGate.SetResult();
        await switching;
        Assert.Equal(EngineState.Stopped, chat.State);
        for (var i = 0; i < 100 && (await store.GetAsync(a.Id, CancellationToken.None))!.Files.Any(f => f.State == StoredFileState.InUse); i++)
        {
            await Task.Delay(20);
        }

        await second.ExchangeAsync(ControlTypes.RemoveModel,
            JsonSerializer.SerializeToElement(new RemoveModelCommand { CatalogueId = a.Id }, AiHostJsonContext.Default.RemoveModelCommand));
        Assert.Null(await store.GetAsync(a.Id, CancellationToken.None));
        Assert.True(File.Exists(fileA)); // a file the user added is only forgotten
    }

    [Fact(Timeout = 30000)]
    public async Task Installed_catalogue_model_is_listed_then_remove_deletes_only_its_own_files()
    {
        using var root = new TestRoot();
        var body = RandomNumberGenerator.GetBytes(64 * 1024);
        var entry = Entry(body);
        var (shell, store) = await StartAsync(root, [entry], body);
        await using var _s = shell;
        // Put the model in place through the store's own write path (what the downloader uses).
        using (store.AcquireDownload(entry.Id))
        {
            await store.EnsureModelAsync(entry.Id, entry.DisplayName, entry.Kind, entry.Engine, CancellationToken.None);
            var part = await store.BeginFileAsync(entry.Id, "tiny.gguf", CancellationToken.None);
            await File.WriteAllBytesAsync(part, body);
            await store.CommitFileAsync(entry.Id, part, entry.Files[0].Sha256, CancellationToken.None);
        }

        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        var row = Assert.Single((await client.ListModelsAsync()).Models);
        Assert.True(row.Installed);
        Assert.Null(row.DownloadState);
        Assert.True(row.Recommended);

        // A file the user added in place is only forgotten, never deleted.
        var outside = Path.Combine(root.Path, "mine.gguf");
        File.WriteAllBytes(outside, body);
        var external = await store.AddExternalAsync(outside, ModelKinds.Chat, EngineKinds.LlamaServer, CancellationToken.None);
        Assert.Contains((await client.ListModelsAsync()).Models, m => m.Id == external.Id && m.ExistingCopy);

        var downloaded = (await store.GetAsync("tiny-chat", CancellationToken.None))!.Files.Single().Path;
        await client.ExchangeAsync(ControlTypes.RemoveModel, System.Text.Json.JsonSerializer.SerializeToElement(
            new RemoveModelCommand { CatalogueId = "tiny-chat" }, AiHostJsonContext.Default.RemoveModelCommand));
        await client.ExchangeAsync(ControlTypes.RemoveModel, System.Text.Json.JsonSerializer.SerializeToElement(
            new RemoveModelCommand { CatalogueId = external.Id }, AiHostJsonContext.Default.RemoveModelCommand));
        Assert.False(File.Exists(downloaded));
        Assert.True(File.Exists(outside));
        Assert.False(Assert.Single((await client.ListModelsAsync()).Models).Installed);

        var missing = await Assert.ThrowsAsync<ControlPipeException>(() => client.ExchangeAsync(ControlTypes.RemoveModel,
            System.Text.Json.JsonSerializer.SerializeToElement(new RemoveModelCommand { CatalogueId = "nope" }, AiHostJsonContext.Default.RemoveModelCommand)));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
    }

    [Fact(Timeout = 30000)]
    public async Task The_active_model_cannot_be_removed()
    {
        using var root = new TestRoot();
        var body = RandomNumberGenerator.GetBytes(4096);
        var (shell, store) = await StartAsync(root, [Entry(body)], body);
        await using var _s = shell;
        var outside = Path.Combine(root.Path, "mine.gguf");
        File.WriteAllBytes(outside, body);
        var external = await store.AddExternalAsync(outside, ModelKinds.Chat, EngineKinds.LlamaServer, CancellationToken.None);
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);
        await client.ActivateModelAsync(external.Id);
        var ex = await Assert.ThrowsAsync<ControlPipeException>(() => client.ExchangeAsync(ControlTypes.RemoveModel,
            System.Text.Json.JsonSerializer.SerializeToElement(new RemoveModelCommand { CatalogueId = external.Id }, AiHostJsonContext.Default.RemoveModelCommand)));
        Assert.Equal(ErrorCodes.InvalidRequest, ex.Code);
        Assert.True(File.Exists(outside));
    }

    [Fact(Timeout = 30000)]
    public async Task Settings_answer_says_a_new_folder_moves_nothing_and_idle_policy_follows_settings_and_battery()
    {
        using var root = new TestRoot();
        var (shell, _) = await StartAsync(root, catalogue: null, battery: true);
        await using var _s = shell;
        await using var client = await ControlPipeClient.ConnectAsync(shell.Paths().PipeName);

        var status = await client.GetStatusAsync();
        Assert.True(status.OnBattery);
        Assert.Equal(300, status.Status.Models[ModelKinds.Chat].IdleUnloadSeconds);
        Assert.Equal(TimeSpan.FromSeconds(300), shell.IdleUnloadFor(ModelKinds.Chat));

        var answer = await client.ExchangeAsync(ControlTypes.SetSettings, System.Text.Json.JsonSerializer.SerializeToElement(new HostSettings
        {
            ModelsFolder = Path.Combine(root.Path, "elsewhere"),
            Kinds = new() { [ModelKinds.Chat] = new KindSettings { KeepLoaded = true } },
        }, AiHostJsonContext.Default.HostSettings));
        var saved = answer.Payload!.Value.Deserialize(AiHostJsonContext.Default.SettingsSaved)!;
        Assert.True(saved.ModelsFolderChanged);
        Assert.True(saved.AppliesAtRestart);
        Assert.Contains("not moved or deleted", saved.Note);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "models")));

        status = await client.GetStatusAsync();
        Assert.True(status.Status.Models[ModelKinds.Chat].KeepLoaded);
        Assert.Null(shell.IdleUnloadFor(ModelKinds.Chat));
    }

    [Fact]
    public void Store_folder_stays_inside_the_test_root_and_uses_a_marked_subfolder()
    {
        using var root = new TestRoot();
        var paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? root.Path : null)!;
        Assert.Equal(Path.Combine(root.Path, "models"), Program.StoreFolderFor(paths, new HostSettings()));
        Assert.Equal(Path.Combine(root.Path, "models"), Program.StoreFolderFor(paths, new HostSettings { ModelsFolder = @"C:\Somewhere" }));
        Assert.Equal(Path.Combine(root.Path, "pick", Program.StoreFolderName),
            Program.StoreFolderFor(paths, new HostSettings { ModelsFolder = Path.Combine(root.Path, "pick") }));
    }

    [Fact]
    public void Catalogue_fails_closed_without_a_trusted_key()
    {
        using var root = new TestRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "catalogue"));
        File.WriteAllText(Path.Combine(root.Path, "catalogue", "catalogue.json"), "{}");
        File.WriteAllText(Path.Combine(root.Path, "catalogue", "catalogue.json.sig"), "AAAA");
        Assert.Null(CatalogueSource.Load(root.Path, _ => null, []));
        Assert.Null(CatalogueSource.Load(root.Path, _ => null, [Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo())]));
    }

    [Fact]
    public void Unverified_callers_have_no_publisher()
    {
        Assert.Null(ControlHandler.PublisherOf(new CallerIdentity(1, null, Environment.ProcessPath, Verified: false, SameUser: true)));
    }
}

internal static class ShellTestExtensions
{
    public static HostPaths Paths(this HostShell shell) => shell.PathsForTests;
}

public sealed class LaunchArgsTests
{
    [Theory]
    [InlineData("portal-ai-host:start", LaunchMode.Start)]
    [InlineData("PORTAL-AI-HOST:START", LaunchMode.Start)]
    [InlineData("portal-ai-host:open", LaunchMode.Open)]
    [InlineData("portal-ai-host://open/", LaunchMode.Open)]
    [InlineData("\"portal-ai-host:open\"", LaunchMode.Open)]
    [InlineData("portal-ai-host:run?cmd=calc.exe", LaunchMode.Start)]
    [InlineData("portal-ai-host:open&x=1", LaunchMode.Start)]
    [InlineData("portal-ai-host:", LaunchMode.Start)]
    [InlineData("--something", LaunchMode.Start)]
    public void Only_start_and_open_are_understood(string arg, LaunchMode expected)
    {
        Assert.Equal(expected, LaunchArgs.Parse([arg]));
    }

    [Fact]
    public void No_arguments_is_a_plain_start_that_shows_a_running_host()
    {
        Assert.Equal(LaunchMode.Open, LaunchArgs.Parse([]));
        Assert.False(LaunchArgs.IsProtocol([]));
        Assert.True(LaunchArgs.IsProtocol(["portal-ai-host:start"]));
    }
}
