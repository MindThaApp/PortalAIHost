using System.Diagnostics;
using System.Net;
using System.Text;
using Portal.AiHost.Engine;

// Test double for llama-server. Understands --host/--port/--api-key like the real thing, plus
//   --load-ms N     /health answers 503 for N ms after start
//   --crash-ms N    exit with code 3 N ms after start
//   --crash-file P  exit with code 3 as soon as the file P exists (the test decides when)
//   --exit-ms N     exit with code 4 N ms after start (used while loading)
//   --pid-file P    write own pid to P
// "host <exe>" mode: put <exe> (this same exe, stub mode) in a job object, print its pid, wait.
// The tests kill this host process and check that the child dies with it.

if (args.Length >= 2 && args[0] == "host")
{
    using var job = new JobObject();
    var psi = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in args.Skip(2))
    {
        psi.ArgumentList.Add(a);
    }

    var child = Process.Start(psi)!;
    job.Assign(child);
    Console.WriteLine("pid=" + child.Id);
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

string Arg(string name, string def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

var port = int.Parse(Arg("--port", "0"));
var key = Arg("--api-key", "");
var loadMs = int.Parse(Arg("--load-ms", "0"));
var crashMs = int.Parse(Arg("--crash-ms", "0"));
var exitMs = int.Parse(Arg("--exit-ms", "0"));
var crashFile = Arg("--crash-file", "");
var pidFile = Arg("--pid-file", "");
var started = Stopwatch.StartNew();
if (pidFile.Length > 0)
{
    File.WriteAllText(pidFile, Environment.ProcessId.ToString());
}

if (crashMs > 0)
{
    _ = Task.Run(async () => { await Task.Delay(crashMs); Environment.Exit(3); });
}

if (crashFile.Length > 0)
{
    _ = Task.Run(async () =>
    {
        while (!File.Exists(crashFile))
        {
            await Task.Delay(10);
        }

        Environment.Exit(3);
    });
}

if (exitMs > 0)
{
    _ = Task.Run(async () => { await Task.Delay(exitMs); Environment.Exit(4); });
}

var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine("stub listening");
while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = Task.Run(() => Handle(ctx));
}

async Task Handle(HttpListenerContext ctx)
{
    try
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path == "/health")
        {
            ctx.Response.StatusCode = started.ElapsedMilliseconds >= loadMs ? 200 : 503;
            await Write(ctx, "{\"status\":\"ok\"}");
            return;
        }

        if (ctx.Request.Headers["Authorization"] != "Bearer " + key)
        {
            ctx.Response.StatusCode = 401;
            await Write(ctx, "{}");
            return;
        }

        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = await sr.ReadToEndAsync();
        }

        var stream = body.Contains("\"stream\":true", StringComparison.Ordinal);
        if (path == "/v1/chat/completions" && stream)
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            var o = ctx.Response.OutputStream;
            for (var i = 0; i < 50; i++)
            {
                var chunk = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"stub\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"tok" + i + " \"},\"finish_reason\":null}]}\n\n";
                var bytes = Encoding.UTF8.GetBytes(chunk);
                await o.WriteAsync(bytes);
                await o.FlushAsync();
                await Task.Delay(100);
            }

            var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await o.WriteAsync(done);
            ctx.Response.Close();
        }
        else if (path == "/v1/chat/completions")
        {
            await Write(ctx, "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"stub\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}");
        }
        else if (path == "/v1/embeddings")
        {
            await Write(ctx, "{\"object\":\"list\",\"model\":\"stub\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.5,0.25]}],\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}");
        }
        else if (path == "/apply-template")
        {
            await Write(ctx, "{\"prompt\":\"a b c\"}");
        }
        else if (path == "/v1/speech")
        {
            // Voice helper stand-in: a 0.1 s mono 24 kHz tone, or a 500 for text that says FAIL.
            if (body.Contains("FAIL", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 500;
                await Write(ctx, "{}");
                return;
            }

            const int rate = 24000;
            var n = rate / 10;
            var wav = new byte[44 + n * 2];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(36 + n * 2).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(rate).CopyTo(wav, 24);
            BitConverter.GetBytes(rate * 2).CopyTo(wav, 28);
            BitConverter.GetBytes((short)2).CopyTo(wav, 32);
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(n * 2).CopyTo(wav, 40);
            for (var i = 0; i < n; i++)
            {
                BitConverter.GetBytes((short)(Math.Sin(i * 0.06) * 7000)).CopyTo(wav, 44 + i * 2);
            }

            ctx.Response.ContentType = "audio/wav";
            await ctx.Response.OutputStream.WriteAsync(wav);
            ctx.Response.Close();
        }
        else if (path == "/tokenize")
        {
            await Write(ctx, "{\"tokens\":[1,2,3]}");
        }
        else
        {
            ctx.Response.StatusCode = 404;
            await Write(ctx, "{}");
        }
    }
    catch (Exception)
    {
        // client went away mid-stream
    }
}

static async Task Write(HttpListenerContext ctx, string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}
