namespace SoundboardLab.Audio;

public enum PlaybackDestination { Speakers, Microphone, Both }
public enum RecordingSource { Microphone, Desktop, Both }
public sealed record AudioDevice(string Id, string Name, bool IsInput);
public sealed record AudioAnalysis(TimeSpan Duration, float Peak, double LoudnessLufs, float[] Waveform);
public sealed record AudioEditOptions(TimeSpan? Start = null, TimeSpan? End = null,
    TimeSpan? FadeIn = null, TimeSpan? FadeOut = null, double? TargetLufs = null,
    float Gain = 1, float PeakCeiling = 0.98f);
public sealed class PlaybackProgressEventArgs(TimeSpan position, TimeSpan duration, bool playing, bool completed = false, int timelineVersion = -1) : EventArgs
{
    public TimeSpan Position { get; } = position;
    public TimeSpan Duration { get; } = duration;
    public bool IsPlaying { get; } = playing;
    public bool Completed { get; } = completed;
    public int TimelineVersion { get; } = timelineVersion;
}
public sealed class AudioErrorEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}
public sealed record InjectionStatus(bool Connected, int SampleRate, int Channels,
    int QueuedFrames, int ActiveConsumers, int LastError, string Message);
