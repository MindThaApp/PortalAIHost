using Portal.AiHost.Core.Voice;
using Portal.AiHost.VoiceServer;

namespace Portal.AiHost.VoiceServer.Tests;

public sealed class WavWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pvs-" + Guid.NewGuid().ToString("N"));

    public WavWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true); // The folder this test created.

    [Fact]
    public void EncodesReadableMonoAudio()
    {
        var wav = WavFileWriter.Encode([0f, 0.5f, -0.5f, 2f], 24000);
        var pcm = WavAudio.ReadMono(wav);
        Assert.Equal(24000, pcm.SampleRate);
        Assert.Equal(4, pcm.Samples.Length);
        Assert.Equal(0, pcm.Samples[0]);
        Assert.InRange(pcm.Samples[1], 16383, 16385);
        Assert.InRange(pcm.Samples[2], -16385, -16383);
        Assert.InRange(pcm.Samples[3], 32766, 32767);
    }

    [Fact]
    public void WritesANewFileAndLeavesNoTemp()
    {
        var path = Path.Combine(_dir, "a.wav");
        WavFileWriter.WriteNew(path, [1, 2, 3]);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void NeverOverwritesAnExistingFile()
    {
        var path = Path.Combine(_dir, "a.wav");
        File.WriteAllBytes(path, [9, 9]);
        Assert.Throws<IOException>(() => WavFileWriter.WriteNew(path, [1, 2, 3]));
        Assert.Equal([9, 9], File.ReadAllBytes(path));
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void AMissingFolderIsAnError() =>
        Assert.Throws<DirectoryNotFoundException>(() => WavFileWriter.WriteNew(Path.Combine(_dir, "no", "a.wav"), [1]));
}
