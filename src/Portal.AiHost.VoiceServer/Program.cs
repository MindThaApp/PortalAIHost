using Portal.AiHost.VoiceServer;

VoiceArgs options;
try
{
    options = VoiceArgs.Parse(args);
}
catch (ArgumentException e)
{
    await Console.Error.WriteLineAsync("portal-voice-server: " + e.Message);
    return 2;
}

using var synth = SherpaSynthesizer.FromModelRoot(options.ModelDir, options.Threads);
using var server = new VoiceHttpServer(options.Port, options.ApiKey, synth, Console.Out);
using var done = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    done.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
try
{
    server.Start();
}
catch (System.Net.HttpListenerException e)
{
    await Console.Error.WriteLineAsync("portal-voice-server: could not listen on the port: " + e.Message);
    return 3;
}

Console.WriteLine($"portal-voice-server listening on 127.0.0.1:{options.Port} threads={options.Threads}");
done.Wait();
await server.StopAsync();
return 0;
