namespace SoundboardLab.Audio;

// Compute the K-weighted block history once, then use a six-second initial
// estimate and cumulative gated measurements as the playback timeline advances.
public static class DynamicNormalizer
{
    public static float[] Normalize(float[] stereo,double targetLufs,CancellationToken token=default)
    {
        if(stereo.Length%2!=0||!double.IsFinite(targetLufs)||targetLufs is <-70 or >0)throw new ArgumentOutOfRangeException(nameof(targetLufs));
        int frames=stereo.Length/2;var channels=new[]{History(stereo,0),History(stereo,1)};var result=new float[stereo.Length];
        double[] previous=[1,1],current=[1,1];
        for(int frame=0;frame<frames;frame++){
            if(frame%48000==0){token.ThrowIfCancellationRequested();int prefix=Math.Min(frames,Math.Max(6*48000,frame));for(int ch=0;ch<2;ch++){previous[ch]=frame==0?Gain(channels[ch],prefix,targetLufs):current[ch];current[ch]=Gain(channels[ch],prefix,targetLufs);}}
            double blend=Math.Min(1,(frame%48000)/4800d);
            for(int ch=0;ch<2;ch++)result[frame*2+ch]=(float)Math.Clamp(stereo[frame*2+ch]*(previous[ch]+(current[ch]-previous[ch])*blend),-.98,.98);
        }
        return result;
    }
    private static List<(int End,double Energy)> History(float[] samples,int channel)
    {
        var shelf=new Filter(1.53512485958697,-2.69169618940638,1.19839281085285,-1.69065929318241,.73248077421585);var high=new Filter(1,-2,1,-1.99004745483398,.99007225036621);
        const int size=19200;var ring=new double[size];double sum=0;var history=new List<(int,double)>();
        int frames=samples.Length/2;for(int frame=0;frame<frames;frame++){double value=high.Process(shelf.Process(samples[frame*2+channel]));double energy=value*value;sum+=energy-ring[frame%size];ring[frame%size]=energy;if(frame+1>=size&&(frame+1-size)%4800==0)history.Add((frame+1,sum/size));}
        if(history.Count==0&&frames>0)history.Add((frames,sum/frames));return history;
    }
    private static double Gain(List<(int End,double Energy)> history,int prefix,double target)
    {
        var values=history.Where(x=>x.End<=prefix&&ToLufs(x.Energy)>=-70).Select(x=>x.Energy).ToArray();if(values.Length==0)return 1;
        double gate=ToLufs(values.Average())-10;var gated=values.Where(x=>ToLufs(x)>=gate).ToArray();double measured=ToLufs(gated.Average());return Math.Pow(10,(target-measured)/20);
    }
    private static double ToLufs(double energy)=>energy>0?-.691+10*Math.Log10(energy):double.NegativeInfinity;
    private sealed class Filter(double b0,double b1,double b2,double a1,double a2){private double z1,z2;public double Process(double value){double y=b0*value+z1;z1=b1*value-a1*y+z2;z2=b2*value-a2*y;return y;}}
}
