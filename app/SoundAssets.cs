using System.IO;
using SoundboardLab.Audio;
using SoundboardLab.Models;
namespace SoundboardLab;
internal static class SoundAssets
{
    public static void Initialize(AppPreferences settings,string stateDirectory)
    {
        if(string.IsNullOrWhiteSpace(settings.RecordingFolder))settings.RecordingFolder=Path.Combine(stateDirectory,"recordings");
        var notifications=new Dictionary<string,string>{["record-speakers"]="recording-speakers",["record-microphone"]="recording-microphone",["record-stop"]="recording-finished",["mode-both"]="default",["mode-speakers"]="speakers",["mode-microphone"]="microphone",["hotkeys-on"]="hotkeys-enabled",["hotkeys-off"]="hotkeys-disabled",["auto-on"]="auto-keys-enabled",["auto-off"]="auto-keys-disabled"};
        for(int i=1;i<=9;i++)notifications["page-"+i]="hotbar-page-"+i;
        int index=0;foreach(var pair in notifications){string path=Path.Combine(AppContext.BaseDirectory,"sounds","notify",pair.Value+".m4a");if(!File.Exists(path)){path=Path.Combine(stateDirectory,"sounds",pair.Value+".wav");if(!File.Exists(path)){var samples=new float[11520];double frequency=450+index*35;for(int frame=0;frame<samples.Length/2;frame++){float value=.04f*(float)(Math.Sin(2*Math.PI*frequency*frame/48000)*Math.Sin(Math.PI*frame/(samples.Length/2)));samples[frame*2]=samples[frame*2+1]=value;}AudioFiles.WriteWav(path,samples);}}settings.Notifications.TryAdd(pair.Key,new(){Enabled=true,Path=path});index++;}
        if(string.IsNullOrWhiteSpace(settings.VoiceActivationPath)){string path=Path.Combine(AppContext.BaseDirectory,"sounds","white_noise.mp3");if(!File.Exists(path)){path=Path.Combine(stateDirectory,"sounds","activation.wav");if(!File.Exists(path)){var random=new Random(42);var samples=new float[9600];for(int frame=0;frame<samples.Length/2;frame++)samples[frame*2]=samples[frame*2+1]=(float)(random.NextDouble()-.5)*.2f;AudioFiles.WriteWav(path,samples);}}settings.VoiceActivationPath=path;}
    }
}
