namespace Portal.AiHost.Contracts;

/// <summary>Consent values a caller must send when it creates a voice from a sample.</summary>
public static class VoiceConsent
{
    /// <summary>The sample is a recording of the person using this PC.</summary>
    public const string OwnVoice = "own_voice";

    /// <summary>The person in the sample gave permission for their voice to be copied.</summary>
    public const string Permission = "permission";

    public static bool IsValid(string? value) => value is OwnVoice or Permission;
}

/// <summary>Values of the speech job status field.</summary>
public static class SpeechJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Sample formats accepted when a voice is created.</summary>
public static class VoiceSampleFormats
{
    public const string Wav = "wav";
    public const string Mp3 = "mp3";
}

/// <summary>The voice entry of GET /host/capabilities. Absent when this host has no voice feature at all.</summary>
public sealed class VoiceCapabilities
{
    /// <summary>A voice can be made from a sample (the cloning model is installed and the engine is present).</summary>
    public bool Cloning { get; init; }

    /// <summary>Text can be read aloud with at least one voice.</summary>
    public bool Speech { get; init; }

    /// <summary>Audio formats a speech job can produce. Only "wav" for now; empty when speech is false.</summary>
    public List<string> Formats { get; init; } = new();

    /// <summary>Set when Speech is false: see <see cref="UnavailableReasons"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Sample formats a voice can be created from (wav, and mp3 when the host can decode it).</summary>
    public List<string>? SampleFormats { get; init; }

    public int? MaxSampleSeconds { get; init; }
    public int? MaxTextChars { get; init; }
}

/// <summary>POST /host/voices.</summary>
public sealed class VoiceCreateRequest
{
    public string Name { get; set; } = "";

    /// <summary>"own_voice" or "permission". Required.</summary>
    public string? Consent { get; set; }

    /// <summary>The MP3 or WAV file, base64 encoded. 3 to 60 seconds of speech; 10 to 30 seconds works best.</summary>
    public string SampleBase64 { get; set; } = "";

    /// <summary>"wav" or "mp3". Optional: the host looks at the file itself.</summary>
    public string? SampleFormat { get; set; }
}

/// <summary>PATCH /host/voices/{id}.</summary>
public sealed class VoiceRenameRequest
{
    public string Name { get; set; } = "";
}

public sealed class VoiceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Consent { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The app that created the voice. Every app can read text with it; only this app (or the user) removes it.</summary>
    public string CreatedBy { get; set; } = "";

    public long DurationMs { get; set; }
    public int SampleRate { get; set; }
}

public sealed class BuiltInVoiceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Language { get; set; }
}

/// <summary>GET /host/voices.</summary>
public sealed class VoiceList
{
    public List<VoiceInfo> Voices { get; set; } = new();
    public List<BuiltInVoiceInfo> Builtin { get; set; } = new();
}

/// <summary>POST /host/speech/jobs. Exactly one of <c>voice_id</c> and <c>builtin_voice</c>.</summary>
public sealed class SpeechJobRequest
{
    public string? VoiceId { get; set; }
    public string? BuiltinVoice { get; set; }
    public string Text { get; set; } = "";

    /// <summary>0.5 to 2.0. Default 1.0.</summary>
    public double? Speed { get; set; }

    /// <summary>"wav" (the default and only value for now).</summary>
    public string? Format { get; set; }
}

public sealed class SpeechJobResult
{
    public string Format { get; set; } = "wav";

    /// <summary>File name in the host's own output folder. Fetch the audio with GET .../audio.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Full path of the result on the PC that runs the host. Only set for apps on that PC.</summary>
    public string? Path { get; set; }

    public long Bytes { get; set; }
    public long DurationMs { get; set; }
    public int SampleRate { get; set; }
}

public sealed class SpeechJobError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>GET and DELETE /host/speech/jobs/{id}; the answer of POST /host/speech/jobs.</summary>
public sealed class SpeechJobInfo
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = SpeechJobStatuses.Queued;

    /// <summary>0 to 1.</summary>
    public double Progress { get; set; }

    public string? VoiceId { get; set; }
    public string? BuiltinVoice { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? QueuePosition { get; set; }
    public SpeechJobResult? Result { get; set; }
    public SpeechJobError? Error { get; set; }
}
