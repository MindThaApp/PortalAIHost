using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Portal.AiHost.VoiceServer;

/// <summary>
/// The loopback HTTP side of the helper. GET /health and POST /v1/speech, both behind a bearer key. One request is
/// synthesised at a time. The model is loaded on the first speech request, so /health answers as soon as the port is open.
/// </summary>
public sealed class VoiceHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _apiKey;
    private readonly ISpeechSynthesizer _synth;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TextWriter _log;
    private Task? _loop;

    public VoiceHttpServer(int port, string apiKey, ISpeechSynthesizer synth, TextWriter log)
    {
        _apiKey = apiKey;
        _synth = synth;
        _log = log;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!Authorised(ctx.Request))
            {
                await JsonAsync(ctx, 401, "unauthorized").ConfigureAwait(false);
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (ctx.Request.HttpMethod == "GET" && path == "/health")
            {
                await JsonAsync(ctx, 200, "ok").ConfigureAwait(false);
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/v1/speech")
            {
                await SpeechAsync(ctx).ConfigureAwait(false);
            }
            else
            {
                await JsonAsync(ctx, 404, "not found").ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The caller went away.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.WriteLine("error: " + e.GetType().Name);
            try
            {
                await JsonAsync(ctx, 500, "engine error").ConfigureAwait(false);
            }
            catch (Exception e2) when (e2 is HttpListenerException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task SpeechAsync(HttpListenerContext ctx)
    {
        SpeechRequest request;
        try
        {
            request = SpeechRequest.Parse(await ReadBodyAsync(ctx.Request).ConfigureAwait(false));
        }
        catch (FormatException e)
        {
            await JsonAsync(ctx, 400, e.Message).ConfigureAwait(false);
            return;
        }

        try
        {
            await _one.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await JsonAsync(ctx, 503, "shutting down").ConfigureAwait(false);
            return;
        }

        SynthResult result;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = await Task.Run(() => _synth.Synthesize(request, (d, t) => _log.WriteLine($"progress {d}/{t}"), _stop.Token)).ConfigureAwait(false);
        }
        catch (SpeechException e)
        {
            await JsonAsync(ctx, e.Status, e.Message).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            await JsonAsync(ctx, 503, "cancelled").ConfigureAwait(false);
            return;
        }
        finally
        {
            _one.Release();
        }

        var wav = WavFileWriter.Encode(result.Samples, result.SampleRate);
        _log.WriteLine($"speech {result.Seconds:F1}s audio in {sw.Elapsed.TotalSeconds:F1}s");
        if (request.OutPath is { } outPath)
        {
            try
            {
                WavFileWriter.WriteNew(outPath, wav);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await JsonAsync(ctx, 409, "the output file could not be created").ConfigureAwait(false);
                return;
            }

            await JsonAsync(ctx, 200, "written").ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "audio/wav";
        ctx.Response.ContentLength64 = wav.Length;
        await ctx.Response.OutputStream.WriteAsync(wav).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private bool Authorised(HttpListenerRequest r)
    {
        var h = r.Headers["Authorization"];
        if (h is null || !h.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(h[7..]), Encoding.UTF8.GetBytes(_apiKey));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest r)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await r.InputStream.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > SpeechRequest.MaxBodyBytes)
            {
                throw new FormatException("The request body is too large.");
            }
        }

        return ms.ToArray();
    }

    private static async Task JsonAsync(HttpListenerContext ctx, int status, string message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [status < 300 ? "status" : "error"] = message });
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
        _one.Dispose();
    }
}
