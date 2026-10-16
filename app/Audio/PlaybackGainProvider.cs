using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundboardLab.Audio;

// Volume is applied to this player's PCM buffer. It never changes the endpoint,
// another audio session, or Windows' communications/ducking preferences.
internal sealed class PlaybackGainProvider(BufferedWaveProvider input,Func<float> volume):IWaveProvider
{
    private readonly object gate=new();
    private byte[] restartBuffer=[];
    private long consumed;
    private long firstReadTimestamp;
    public long ConsumedFrames=>Interlocked.Read(ref consumed);
    internal long FirstReadTimestamp=>Interlocked.Read(ref firstReadTimestamp);
    public WaveFormat WaveFormat=>input.WaveFormat;
    public void Reset(){lock(gate){input.ClearBuffer();Interlocked.Exchange(ref consumed,0);Interlocked.Exchange(ref firstReadTimestamp,0);}}
    public void Restart(float[] stereo,int firstFrame,int frames)
    {
        if(firstFrame<0||frames<0||firstFrame>stereo.Length/2-frames)throw new ArgumentOutOfRangeException(nameof(frames));
        lock(gate){
            int bytes=frames*8;if(restartBuffer.Length<bytes)restartBuffer=new byte[bytes];
            Buffer.BlockCopy(stereo,firstFrame*8,restartBuffer,0,bytes);
            input.ClearBuffer();Interlocked.Exchange(ref consumed,0);
            Interlocked.Exchange(ref firstReadTimestamp,0);
            input.AddSamples(restartBuffer,0,bytes);
        }
    }
    public int Read(byte[] buffer,int offset,int count)
    {
        lock(gate){
            int available=Math.Min(count,input.BufferedBytes),read=input.Read(buffer,offset,count);
            if(available>0&&firstReadTimestamp==0)Interlocked.Exchange(ref firstReadTimestamp,System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Add(ref consumed,available/input.WaveFormat.BlockAlign);
            var samples=MemoryMarshal.Cast<byte,float>(buffer.AsSpan(offset,read));float gain=volume();
            for(int i=0;i<samples.Length;i++)samples[i]=Math.Clamp(samples[i]*gain,-1,1);
            return read;
        }
    }
}
