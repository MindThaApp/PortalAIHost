namespace Portal.AiHost.Core.Abstractions;

/// <summary>Transition kinds between two joined clips. Open strings; the host accepts only these two.</summary>
public static class MediaTransitions
{
    public const string Cut = "cut";
    public const string Crossfade = "crossfade";
}

/// <summary>Roles of a soundtrack in a join. A voice track is louder than the music, which is ducked under it.</summary>
public static class MediaAudioRoles
{
    public const string Music = "music";
    public const string Voice = "voice";
}

/// <summary>The change from one clip to the next: a cut, or a crossfade of <see cref="Seconds"/>.</summary>
public sealed record MediaTransition(string Kind, double Seconds);

/// <summary>One soundtrack of a join. The file is a host-owned file (the host never hands over a caller path).</summary>
public sealed record MediaAudioTrack(string Path, string Role, double Volume, double StartSeconds);

/// <summary>
/// What to join: clips in order, the transition after each clip but the last (so <c>Clips.Count - 1</c> entries),
/// and optional soundtracks. Generated clips carry no sound, so only the soundtracks are heard. With
/// <see cref="Duck"/> the music track sinks while a voice track speaks.
/// </summary>
public sealed record MediaJoinPlan(
    IReadOnlyList<string> Clips,
    IReadOnlyList<MediaTransition> Transitions,
    IReadOnlyList<MediaAudioTrack> Audio,
    bool Duck);

/// <summary>
/// Video and audio helpers of the host (ffmpeg). Every call is one child process that ends with the call; a
/// cancellation kills that process and everything it started. A call never overwrites: the output path is new.
/// </summary>
public interface IMediaTools
{
    /// <summary>True when ffmpeg is installed: MP4 conversion, last frames and joins can run.</summary>
    bool Available { get; }

    /// <summary>Re-encodes a clip into an MP4 (H.264 with the Windows encoder, faststart). Throws <see cref="EngineException"/>.</summary>
    Task ConvertToMp4Async(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>The last picture of a video as PNG bytes (read over a pipe, no file is written).</summary>
    Task<byte[]> ExtractLastFramePngAsync(string inputPath, CancellationToken ct);

    /// <summary>Joins clips with the plan's transitions and soundtracks into one new MP4.</summary>
    Task JoinAsync(MediaJoinPlan plan, string outputPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>The length of a media file in seconds, or null when it cannot be read.</summary>
    Task<double?> ProbeDurationAsync(string path, CancellationToken ct);
}
