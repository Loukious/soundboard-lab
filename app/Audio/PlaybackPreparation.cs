namespace SoundboardLab.Audio;

public sealed record PlaybackRoutingOptions(int MicrophoneDelayMs = 0, string ActivationPath = "",double? TargetLufs=null,double FixedAdjustmentDb=0);
public sealed record PreparedPlayback(float[] Speakers,float[] Microphone);
public static class PlaybackPreparation
{
    public static PreparedPlayback Prepare(float[] clip,float[] activation,PlaybackDestination target,int microphoneDelayMs,float[]? microphoneClip=null)
    {
        if(clip.Length%2!=0||activation.Length%2!=0||microphoneDelayMs is <-10000 or >10000)throw new ArgumentOutOfRangeException(nameof(microphoneDelayMs));
        microphoneClip??=clip;if(microphoneClip.Length!=clip.Length)throw new ArgumentException("Both playback streams must have the same duration.");
        int prefix=target==PlaybackDestination.Speakers?0:activation.Length;
        int delay=target==PlaybackDestination.Both?Math.Abs(microphoneDelayMs)*96:0;
        if(prefix==0&&delay==0)return new(clip,target==PlaybackDestination.Speakers?clip:microphoneClip);
        var speakers=new float[checked(prefix+delay+clip.Length)];var microphone=new float[speakers.Length];
        Array.Copy(clip,0,speakers,prefix+(microphoneDelayMs<0?delay:0),clip.Length);
        if(prefix>0)Array.Copy(activation,microphone,prefix);
        Array.Copy(microphoneClip,0,microphone,prefix+(microphoneDelayMs>=0?delay:0),clip.Length);
        return new(speakers,microphone);
    }
}
