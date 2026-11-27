using System.Text.Json;

// Test double for the Best quality add-on worker. Speaks the same JSON-lines protocol on stdin and stdout.
// First argument picks the behaviour:
//   (none)    normal: "copies" each input frame to the output folder under the same name, never overwriting
//   slow      waits 200 ms per frame so a cancel can arrive; answers cancel with a "cancelled" event
//   hang      accepts an upscale command and then never answers (the host must kill it)
//   nocuda    probe reports ok:false, reason no_cuda
//   oom       upscale answers with an insufficient_memory error
var mode = args.Length > 0 ? args[0] : "";
var cancelled = false;
var work = new Queue<JsonElement>();
var sync = new object();

void Emit(string json)
{
    Console.Out.WriteLine(json);
    Console.Out.Flush();
}

Emit("{\"event\":\"ready\",\"protocol\":1}");

var worker = new Thread(() =>
{
    while (true)
    {
        JsonElement cmd;
        lock (sync)
        {
            while (work.Count == 0)
            {
                Monitor.Wait(sync);
            }

            cmd = work.Dequeue();
        }

        var src = cmd.GetProperty("input_dir").GetString()!;
        var dst = cmd.GetProperty("output_dir").GetString()!;
        var seg = cmd.GetProperty("segment").GetInt32();
        if (mode == "hang")
        {
            Thread.Sleep(Timeout.Infinite);
        }

        if (mode == "oom")
        {
            Emit("{\"event\":\"error\",\"code\":\"insufficient_memory\",\"message\":\"only 1 byte free\"}");
            continue;
        }

        Directory.CreateDirectory(dst);
        var names = Directory.GetFiles(src, "*.png").Select(Path.GetFileName).Order().ToList();
        var done = 0;
        var stopped = false;
        foreach (var n in names)
        {
            if (cancelled)
            {
                Emit("{\"event\":\"cancelled\",\"segment\":" + seg + "}");
                stopped = true;
                break;
            }

            var target = Path.Combine(dst, n!);
            if (!File.Exists(target))
            {
                File.Copy(Path.Combine(src, n!), target, overwrite: false);
            }

            done++;
            Emit("{\"event\":\"progress\",\"segment\":" + seg + ",\"frames_done\":" + done + ",\"frames_total\":" + names.Count + "}");
            if (mode == "slow")
            {
                Thread.Sleep(200);
            }
        }

        if (!stopped)
        {
            Emit("{\"event\":\"segment_done\",\"segment\":" + seg + ",\"frames\":" + names.Count + "}");
        }
    }
})
{
    IsBackground = true,
};
worker.Start();

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    using var doc = JsonDocument.Parse(line);
    var op = doc.RootElement.GetProperty("op").GetString();
    if (op == "probe")
    {
        Emit(mode == "nocuda"
            ? "{\"event\":\"probe\",\"ok\":false,\"reason\":\"no_cuda\"}"
            : "{\"event\":\"probe\",\"ok\":true,\"device\":\"Fake GPU\",\"vram_bytes\":12884901888}");
    }
    else if (op == "upscale")
    {
        cancelled = false;
        lock (sync)
        {
            work.Enqueue(doc.RootElement.Clone());
            Monitor.Pulse(sync);
        }
    }
    else if (op == "cancel")
    {
        cancelled = true;
    }
    else if (op == "quit")
    {
        break;
    }
}
