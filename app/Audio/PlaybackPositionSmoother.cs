namespace SoundboardLab.Audio;

// Presentation only: audio samples and the engine's position stay authoritative.
public sealed class PlaybackPositionSmoother
{
    private bool initialized, wasPlaying;
    private int version;
    private double lastTime, displayed;
    public double Update(double position, double duration, bool playing, int timelineVersion, double time)
    {
        position = Math.Clamp(position, 0, Math.Max(0, duration));
        if (!initialized || version != timelineVersion || !playing || !wasPlaying)
            displayed = position;
        else
        {
            double elapsed = Math.Clamp(time-lastTime, 0, .25);
            double predicted = displayed + elapsed;
            // Correct packet/device-clock quantization gradually, without moving
            // backwards during ordinary playback or running ahead during a stall.
            double corrected = predicted + (position-predicted) * (1-Math.Exp(-elapsed/.12));
            displayed = Math.Max(displayed, Math.Min(corrected, position+.075));
        }
        displayed = Math.Clamp(displayed, 0, Math.Max(0, duration));
        lastTime=time;version=timelineVersion;wasPlaying=playing;initialized=true;
        return displayed;
    }
}
