using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;
using Concentus;
using Concentus.Oggfile;

namespace SoundboardLab.Audio;

public static class AudioFiles
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public static Task<float[]> DecodeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Decode(path, cancellationToken), cancellationToken);

    public static float[] Decode(string path, CancellationToken cancellationToken = default)
    {
        using (var probe = File.OpenRead(path))
        {
            var prefix = new byte[Math.Min(128, (int)Math.Min(probe.Length, 128))];
            _ = probe.Read(prefix);
            if (System.Text.Encoding.ASCII.GetString(prefix).Contains("OpusHead", StringComparison.Ordinal))
            {
                probe.Position = 0;
                var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
                var opus = new OpusOggReadStream(decoder, probe);
                var decoded = new List<float>();
                while (opus.HasNextPacket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var packet = opus.DecodeNextPacket();
                    if (packet is not null) foreach (var sample in packet) decoded.Add(sample / 32768f);
                    if (decoded.Count > 48000 * 2 * 3600) throw new InvalidOperationException("Clips longer than one hour require splitting before importing.");
                }
                return decoded.ToArray();
            }
        }
        using WaveStream reader = string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path) : new AudioFileReader(path);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels != 2) source = new StereoDownmixProvider(source);
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);
        var data = new List<float>();
        var buffer = new float[16384];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Count > 48000 * 2 * 3600) throw new InvalidOperationException("Clips longer than one hour require splitting before importing.");
            for (var i = 0; i < count; i++) data.Add(float.IsFinite(buffer[i]) ? buffer[i] : 0);
        }
        if (data.Count % 2 != 0) data.Add(0);
        return data.ToArray();
    }

    public static async Task<AudioAnalysis> AnalyzeAsync(string path, int waveformPoints = 600, CancellationToken cancellationToken = default)
    {
        var samples = await DecodeAsync(path, cancellationToken).ConfigureAwait(false);
        return Analyze(samples, waveformPoints);
    }

    public static AudioAnalysis Analyze(float[] stereo48k, int waveformPoints = 600)
    {
        if (stereo48k.Length % 2 != 0) throw new ArgumentException("Expected interleaved stereo frames.");
        waveformPoints = Math.Clamp(waveformPoints, 1, 10000);
        var waveform = new float[waveformPoints];
        float peak = 0;
        for (var i = 0; i < stereo48k.Length; i++)
        {
            var value = Math.Abs(stereo48k[i]);
            peak = Math.Max(peak, value);
            var index = (int)((long)i * waveformPoints / Math.Max(1, stereo48k.Length));
            waveform[index] = Math.Max(waveform[index], value);
        }
        return new AudioAnalysis(TimeSpan.FromSeconds(stereo48k.Length / 96000d), peak,
            LoudnessMeter.Measure(stereo48k), waveform);
    }

    public static async Task<AudioAnalysis> ExportAsync(string input, string output, AudioEditOptions options, CancellationToken cancellationToken = default)
    {
        var samples = await DecodeAsync(input, cancellationToken).ConfigureAwait(false);
        var edited = Edit(samples, options);
        await Task.Run(() => WriteWav(output, edited), cancellationToken).ConfigureAwait(false);
        return Analyze(edited);
    }

    public static float[] Edit(float[] samples, AudioEditOptions options)
    {
        if (!float.IsFinite(options.Gain) || options.Gain < 0 || options.PeakCeiling is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Gain and peak ceiling must be finite and valid.");
        if (options.TargetLufs is double target && (!double.IsFinite(target) || target is < -70 or > 0))
            throw new ArgumentOutOfRangeException(nameof(options.TargetLufs));
        var totalFrames = samples.Length / 2;
        int start = (int)Math.Clamp((options.Start ?? TimeSpan.Zero).TotalSeconds * SampleRate, 0, totalFrames);
        int end = (int)Math.Clamp((options.End ?? TimeSpan.FromSeconds(totalFrames / (double)SampleRate)).TotalSeconds * SampleRate, 0, totalFrames);
        if (end <= start) throw new ArgumentException("Trim end must be after trim start.");
        var edited = samples.AsSpan(start * 2, (end - start) * 2).ToArray();
        var fadeIn = Math.Max(0, (options.FadeIn ?? TimeSpan.Zero).TotalSeconds * SampleRate);
        var fadeOut = Math.Max(0, (options.FadeOut ?? TimeSpan.Zero).TotalSeconds * SampleRate);
        for (var frame = 0; frame < edited.Length / 2; frame++)
        {
            var fade = Math.Min(fadeIn > 0 ? Math.Min(1, frame / fadeIn) : 1,
                fadeOut > 0 ? Math.Min(1, (edited.Length / 2 - 1 - frame) / fadeOut) : 1);
            edited[frame * 2] *= (float)fade;
            edited[frame * 2 + 1] *= (float)fade;
        }
        double gain = options.Gain;
        if (options.TargetLufs is double targetLufs)
        {
            var measured = LoudnessMeter.Measure(edited);
            if (double.IsFinite(measured)) gain *= Math.Pow(10, (targetLufs - measured) / 20);
        }
        var peak = edited.Length == 0 ? 0 : edited.Max(x => Math.Abs(x));
        if (peak > 0) gain = Math.Min(gain, options.PeakCeiling / peak);
        for (var i = 0; i < edited.Length; i++) edited[i] *= (float)gain;
        return edited;
    }

    public static void WriteWav(string path, float[] stereo48k)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, Channels));
        writer.WriteSamples(stereo48k, 0, stereo48k.Length);
    }

    private sealed class StereoDownmixProvider(ISampleProvider input) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(input.WaveFormat.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var channels = input.WaveFormat.Channels;
            var temp = new float[count / 2 * channels];
            var read = input.Read(temp, 0, temp.Length);
            for (var frame = 0; frame < read / channels; frame++)
            {
                // Preserve front L/R; distribute other channels at reduced gain. LFE omitted for 5.1/7.1.
                float l = temp[frame * channels], r = temp[frame * channels + 1];
                for (var ch = 2; ch < channels; ch++)
                {
                    if (ch == 3 && channels >= 6) continue;
                    var value = temp[frame * channels + ch] * 0.5f;
                    l += value; r += value;
                }
                buffer[offset + frame * 2] = l; buffer[offset + frame * 2 + 1] = r;
            }
            return read / channels * 2;
        }
    }
}
