using Portal.AiHost.VoiceServer;

namespace Portal.AiHost.VoiceServer.Tests;

public sealed class VoiceArgsTests
{
    private static string[] Good(params string[] extra) =>
        ["--host", "127.0.0.1", "--port", "45123", "--api-key", "k", "--model-dir", @"C:\m", .. extra];

    [Fact]
    public void ParsesTheHostCommandLine()
    {
        var a = VoiceArgs.Parse(Good("--threads", "6", "--provider", "cpu"));
        Assert.Equal(45123, a.Port);
        Assert.Equal("k", a.ApiKey);
        Assert.Equal(@"C:\m", a.ModelDir);
        Assert.Equal(6, a.Threads);
    }

    [Fact]
    public void ThreadsDefaultAndAreCapped()
    {
        Assert.Equal(VoiceArgs.DefaultThreads, VoiceArgs.Parse(Good()).Threads);
        Assert.Equal(VoiceArgs.MaxThreads, VoiceArgs.Parse(Good("--threads", "9999")).Threads);
    }

    [Theory]
    [InlineData("--host", "0.0.0.0")]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--threads", "0")]
    [InlineData("--provider", "cuda")]
    [InlineData("--bogus", "1")]
    public void BadValuesAreRefused(string flag, string value)
    {
        var args = Good().ToList();
        var i = args.IndexOf(flag);
        if (i >= 0)
        {
            args[i + 1] = value;
        }
        else
        {
            args.AddRange([flag, value]);
        }

        Assert.Throws<ArgumentException>(() => VoiceArgs.Parse(args));
    }

    [Fact]
    public void MissingKeyOrModelDirIsRefused()
    {
        Assert.Throws<ArgumentException>(() => VoiceArgs.Parse(["--port", "1000", "--model-dir", "x"]));
        Assert.Throws<ArgumentException>(() => VoiceArgs.Parse(["--port", "1000", "--api-key", "x"]));
    }

    [Fact]
    public void RepeatedFlagIsRefused() =>
        Assert.Throws<ArgumentException>(() => VoiceArgs.Parse(Good("--port", "46000")));
}

public sealed class TextChunkerTests
{
    [Fact]
    public void ShortTextIsOnePiece() =>
        Assert.Equal(["Hello there. How are you?"], TextChunker.Split("Hello there. How are you?"));

    [Fact]
    public void LongTextSplitsAtSentenceEndsWithinTheLimit()
    {
        var text = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"This is sentence number {i}."));
        var parts = TextChunker.Split(text, 100);
        Assert.True(parts.Count > 5);
        Assert.All(parts, p => Assert.True(p.Length <= 100));
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(text, string.Join(" ", parts));
    }

    [Fact]
    public void ASentenceWithoutStopsIsCutAtCommasOrSpaces()
    {
        var text = string.Join(" ", Enumerable.Repeat("word,", 200));
        var parts = TextChunker.Split(text, 60);
        Assert.All(parts, p => Assert.True(p.Length <= 60));
        Assert.Equal(text.Replace(" ", ""), string.Concat(parts).Replace(" ", ""));
    }

    [Fact]
    public void BlankTextGivesNothing() => Assert.Empty(TextChunker.Split("  \n "));

    [Fact]
    public void DecimalPointsDoNotEndASentence() =>
        Assert.Single(TextChunker.Split("It costs 3.50 dollars."));
}
