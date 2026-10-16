using System.IO;
using NAudio.Wave;
namespace SoundboardLab.Audio;

public sealed record RecordingExportOptions(string Format="WAV",int Bitrate=320000,bool Normalize=false,double TargetDb=89,bool TrimSilence=false);
public static class RecordingExport
{
    public static float[] Process(float[] samples,RecordingExportOptions options)
    {
        int start=0,end=samples.Length/2;
        if(options.TrimSilence){const float threshold=.001f;while(start<end&&Math.Max(Math.Abs(samples[start*2]),Math.Abs(samples[start*2+1]))<threshold)start++;while(end>start&&Math.Max(Math.Abs(samples[end*2-2]),Math.Abs(samples[end*2-1]))<threshold)end--;}
        // Preserve a short silence for an entirely silent recording so the result remains playable.
        if(end<=start)return new float[9600];
        var clip=samples.AsSpan(start*2,(end-start)*2).ToArray();
        return options.Normalize?AudioFiles.Edit(clip,new(TargetLufs:Math.Clamp(options.TargetDb-112,-70,0))):clip;
    }
    public static async Task<string> SaveAsync(string source,RecordingExportOptions options,CancellationToken cancellationToken=default)
    {
        var samples=Process(await AudioFiles.DecodeAsync(source,cancellationToken),options);
        string target=Path.ChangeExtension(source,options.Format.ToUpperInvariant() switch {"WAV"=>"wav","MP3"=>"mp3","M4A"=>"m4a",_=>throw new ArgumentException("Choose M4A, MP3, or WAV.")});
        string temporary=Path.Combine(Path.GetDirectoryName(target)!,Guid.NewGuid()+".partial"+Path.GetExtension(target));
        try {
            await Task.Run(()=>{
                if(options.Format.Equals("WAV",StringComparison.OrdinalIgnoreCase))AudioFiles.WriteWav(temporary,samples);
                else {
                    using var stream=new MemoryStream();using(var writer=new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(stream),new WaveFormat(48000,16,2)))writer.WriteSamples(samples,0,samples.Length);
                    stream.Position=0;using var reader=new WaveFileReader(stream);
                    if(options.Format.Equals("MP3",StringComparison.OrdinalIgnoreCase))MediaFoundationEncoder.EncodeToMp3(reader,temporary,options.Bitrate);
                    else MediaFoundationEncoder.EncodeToAac(reader,temporary,192000);
                }
            },cancellationToken);
            File.Move(temporary,target,true);if(!source.Equals(target,StringComparison.OrdinalIgnoreCase))File.Delete(source);return target;
        }catch{if(File.Exists(temporary))File.Delete(temporary);throw;}
    }
}
