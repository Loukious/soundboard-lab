using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundboardLab.Audio;

internal sealed class VoiceMeasurementSession : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly CancellationTokenSource stop=new();
    private readonly object gate=new();
    private readonly float[] ring=new float[96000];
    private int write,count;
    private Task? worker;
    public VoiceMeasurementSession(string? endpoint,Action<double,double> update,Action<Exception> error)
    {
        using var enumerator=new MMDeviceEnumerator();
        device=string.IsNullOrWhiteSpace(endpoint)?enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications):enumerator.GetDevice(endpoint);
        capture=new WasapiCapture(device) {WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(48000,2)};
        capture.DataAvailable+=(_,e)=>{lock(gate){for(int i=0;i+4<=e.BytesRecorded;i+=4){float sample=BitConverter.ToSingle(e.Buffer,i);ring[write]=float.IsFinite(sample)?sample:0;write=(write+1)%ring.Length;count=Math.Min(count+1,ring.Length);}}};
        capture.RecordingStopped+=(_,e)=>{if(e.Exception is not null)error(e.Exception);};
        try{capture.StartRecording();worker=Task.Run(async()=>{
            double measured=double.NegativeInfinity;
            try{while(!stop.IsCancellationRequested){await Task.Delay(150,stop.Token).ConfigureAwait(false);float[] samples;lock(gate){samples=new float[count-count%2];int start=(write-samples.Length+ring.Length)%ring.Length;for(int i=0;i<samples.Length;i++)samples[i]=ring[(start+i)%ring.Length];}if(samples.Length<19200)continue;double level=LoudnessMeter.Measure(samples);if(double.IsFinite(level))measured=Math.Max(measured,level);update(level,measured);}}
            catch(OperationCanceledException){}catch(Exception ex){error(ex);}
        });}catch{capture.Dispose();device.Dispose();stop.Dispose();throw;}
    }
    public void Dispose(){stop.Cancel();capture.StopRecording();capture.Dispose();device.Dispose();try{worker?.GetAwaiter().GetResult();}catch(OperationCanceledException){}stop.Dispose();}
}
