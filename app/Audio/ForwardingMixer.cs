namespace SoundboardLab.Audio;

// Fixed-size, stereo-aligned queues. A delayed capture callback cannot build up
// seconds of stale audio; the oldest live frames are dropped at the boundary.
public sealed class StereoFrameBuffer(int capacityFrames)
{
    private readonly object gate=new();
    private readonly float[] samples=new float[checked((capacityFrames>0?capacityFrames:throw new ArgumentOutOfRangeException(nameof(capacityFrames)))*2)];
    private int start,count;
    public int Count {get{lock(gate)return count/2;}}
    public int Write(ReadOnlySpan<float> input,bool keepNewest=false)
    {
        if(input.Length%2!=0)throw new ArgumentException("Audio must contain complete stereo frames.");
        lock(gate){
            if(keepNewest&&input.Length>samples.Length)input=input[^samples.Length..];
            int length=Math.Min(input.Length,samples.Length-count);
            if(keepNewest){int discard=Math.Max(0,count+input.Length-samples.Length);start=(start+discard)%samples.Length;count-=discard;length=input.Length;}
            for(int i=0;i<length;i++){float value=input[i];samples[(start+count+i)%samples.Length]=float.IsFinite(value)?value:0;}
            count+=length;return length/2;
        }
    }
    public int Read(Span<float> output)
    {
        if(output.Length%2!=0)throw new ArgumentException("Audio must contain complete stereo frames.");
        lock(gate){int length=Math.Min(output.Length,count);for(int i=0;i<length;i++)output[i]=samples[(start+i)%samples.Length];output[length..].Clear();start=(start+length)%samples.Length;count-=length;return length/2;}
    }
    public void Clear(){lock(gate){start=count=0;}}
}

public sealed class ForwardingMixer
{
    private readonly StereoFrameBuffer clips=new(24000);
    private readonly float[] clipChunk=new float[960];
    private long submitted,lastClipFrame,renderedClipFrames;
    public int QueueClip(float[] data,int frames)=>clips.Write(data.AsSpan(0,checked(frames*2)));
    public int PendingClipFrames(int nativeQueued)=>clips.Count+(int)Math.Clamp(lastClipFrame-(submitted-Math.Max(0,nativeQueued)),0,renderedClipFrames);
    public void ResetClip(){clips.Clear();lastClipFrame=submitted;renderedClipFrames=0;}
    public void Render(Span<float> live,float clipGain,float forwardingGain,bool clipPaused)
    {
        if(live.Length>clipChunk.Length)throw new ArgumentException("Mixer chunks must not exceed 480 stereo frames.");
        int frames=clipPaused?0:clips.Read(clipChunk.AsSpan(0,live.Length));
        if(clipPaused)Array.Clear(clipChunk);
        for(int i=0;i<live.Length;i++)live[i]=Math.Clamp(live[i]*forwardingGain+clipChunk[i]*clipGain,-1,1);
        submitted+=live.Length/2;
        if(frames>0){lastClipFrame=submitted-live.Length/2+frames;renderedClipFrames+=frames;}
    }
}
