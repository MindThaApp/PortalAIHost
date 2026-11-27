using System.Text;

// Test double for the stable-diffusion.cpp command-line program. Reads the flags the host sends (-o, --steps, -p)
// and prints what the real program prints: log lines and a progress bar redrawn with carriage returns. The prompt
// picks a behaviour:
//   "hang"      writes a partial output, then waits forever (the tests cancel it)
//   "fail"      prints an error line and exits with code 1
//   "oom"       prints an out-of-memory error line and exits with code 1
//   "nooutput"  exits with code 0 without writing anything
// Anything else: a normal run. The output file holds a PNG signature followed by the argument list, one per line.
// With "pid" in the prompt the process id is written to fake.pid in the working directory.

string Arg(string name, string def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

var output = Arg("-o", "");
var steps = int.Parse(Arg("--steps", "20"));
var prompt = Arg("-p", "");
var stdout = Console.Out;

void Log(string text)
{
    stdout.Write("[INFO ] stable-diffusion.cpp:100 - " + text + "\n");
    stdout.Flush();
}

if (prompt.Contains("pid", StringComparison.Ordinal))
{
    File.WriteAllText("fake.pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

Log("loading model from 'fake'");
Log("loading tensors completed, taking 0.01s");

if (prompt.Contains("fail", StringComparison.Ordinal))
{
    Console.Error.Write("[ERROR] stable-diffusion.cpp:42 - load model from 'fake' failed\n");
    return 1;
}

if (prompt.Contains("oom", StringComparison.Ordinal))
{
    Console.Error.Write("[ERROR] ggml_vulkan.cpp:7 - ErrorOutOfDeviceMemory: failed to allocate buffer\n");
    return 1;
}

if (prompt.Contains("nooutput", StringComparison.Ordinal))
{
    return 0;
}

if (prompt.Contains("hang", StringComparison.Ordinal))
{
    // Written before the sampling line so a cancel on the first sampling update always finds the partial.
    File.WriteAllBytes(output, "partial"u8.ToArray());
}

Log("get_learned_condition completed, taking 5 ms");
Log("sampling using Euler A method");
for (var step = 1; step <= steps; step++)
{
    var bar = new string('=', step * 20 / steps).PadRight(20);
    stdout.Write($"\r  |{bar}| {step}/{steps} - 0.01s/it\u001b[K");
    stdout.Flush();
    if (step == 1 && prompt.Contains("hang", StringComparison.Ordinal))
    {
        Thread.Sleep(Timeout.Infinite);
    }

    Thread.Sleep(5);
}

stdout.Write("\n");
Log("sampling completed, taking 0.10s");
Log("decode_first_stage completed, taking 0.02s");
var content = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
content.AddRange(Encoding.UTF8.GetBytes(string.Join("\n", args)));
using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
{
    file.Write(content.ToArray());
}

Log($"save result PNG image to '{output}'");
return 0;
