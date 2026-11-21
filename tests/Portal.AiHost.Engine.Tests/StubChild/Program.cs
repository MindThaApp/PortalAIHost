using System.Diagnostics;
using System.Net;
using System.Text;
using Portal.AiHost.Engine;

// Test double for llama-server. Understands --host/--port/--api-key like the real thing, plus
//   --load-ms N     /health answers 503 for N ms after start
//   --crash-ms N    exit with code 3 N ms after start
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
