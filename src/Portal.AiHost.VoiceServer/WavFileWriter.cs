using Portal.AiHost.Core.Voice;

namespace Portal.AiHost.VoiceServer;

/// <summary>Writes a WAV file as a new file only: a temp file in the same folder, then a rename that refuses to replace.</summary>
public static class WavFileWriter
{
    public static byte[] Encode(float[] samples, int sampleRate)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[i] = (short)Math.Clamp((int)MathF.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
        }

        return WavAudio.WritePcm16(new PcmAudio(pcm, sampleRate));
    }

    /// <summary>Throws <see cref="IOException"/> when <paramref name="path"/> already exists; an existing file is never touched.</summary>
    public static void WriteNew(string path, byte[] wav)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new IOException("The output path has no folder.");
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException("The output folder does not exist.");
        }

        if (File.Exists(full) || Directory.Exists(full))
        {
            throw new IOException("The output file already exists.");
        }

        var temp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(wav);
                fs.Flush(true);
            }

            File.Move(temp, full, overwrite: false);
        }
        catch
        {
            try
            {
                File.Delete(temp); // Only the temp file this method created.
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
