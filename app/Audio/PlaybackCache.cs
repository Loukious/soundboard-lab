using System.IO;

namespace SoundboardLab.Audio;

// Keep only the most recently prepared sound. Repeated triggers share its PCM;
// changing a file, routing option, or activation sound invalidates it.
internal sealed class PlaybackCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CacheKey? key;
    private PreparedPlayback? value;
    private sealed record FileStamp(string Path, long Length, DateTime Written);
    private sealed record CacheKey(FileStamp Clip, FileStamp? Activation, PlaybackDestination Target, PlaybackRoutingOptions Options);
    private static FileStamp Stamp(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        return new(file.FullName, file.Length, file.LastWriteTimeUtc);
    }
    public async Task<PreparedPlayback> GetAsync(string path, PlaybackDestination target, PlaybackRoutingOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var next = new CacheKey(Stamp(path), target != PlaybackDestination.Speakers && !string.IsNullOrWhiteSpace(options.ActivationPath) ? Stamp(options.ActivationPath) : null, target, options);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (next == key && value is not null) return value;
            var decoded = await AudioFiles.DecodeAsync(next.Clip.Path, token).ConfigureAwait(false);
            if (decoded.Length == 0) throw new InvalidOperationException("This file contains no audio.");
            var activation = next.Activation is {} sound ? await AudioFiles.DecodeAsync(sound.Path, token).ConfigureAwait(false) : [];
            float[] mic = decoded;
            if (target != PlaybackDestination.Speakers)
            {
                if (options.TargetLufs is double loudness) mic = await Task.Run(() => DynamicNormalizer.Normalize(decoded, loudness, token), token).ConfigureAwait(false);
                else if (options.FixedAdjustmentDb != 0) mic = AudioFiles.Edit(decoded, new(Gain: (float)Math.Pow(10, options.FixedAdjustmentDb / 20)));
            }
            var prepared = PlaybackPreparation.Prepare(decoded, activation, target, options.MicrophoneDelayMs, mic);
            token.ThrowIfCancellationRequested();
            key = next; value = prepared;
            return prepared;
        }
        finally { gate.Release(); }
    }
}
