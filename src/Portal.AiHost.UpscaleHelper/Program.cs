using System.Text.Json.Nodes;
using Portal.AiHost.UpscaleHelper;

// portal-upscale: image upscaling helper. Speaks the line protocol of UpscaleSession on stdin and stdout.
if (args.Length > 0 && args[0] is "--version" or "-v")
{
    Console.WriteLine("portal-upscale 1");
    return 0;
}

// Parent death: the host puts this process in a Job Object, so it ends with the host. If stdin closes it ends too.
var input = new BufferedStream(Console.OpenStandardInput(), 1 << 16);
var output = Console.OpenStandardOutput();
var session = new UpscaleSession(input, output, OpenModel);
return await session.RunAsync();

static IModelRunner OpenModel(JsonObject req)
{
    var model = (string?)req["model"] ?? throw new ArgumentException("model is required.");
    if (!File.Exists(model))
    {
        throw new FileNotFoundException("The model file was not found.");
    }

    var device = DeviceChoice.Parse((string?)req["device"]);
    return OnnxRunner.Open(model, device, (int?)req["threads"] ?? 0);
}
