using SoundboardLab.Audio;

namespace SoundboardLab;

public partial class MainWindow
{
    private Func<string[],InjectionStatus> injectionStatusReader=ReadOnlyInjectionStatus.Read;
    private Task? statusRefreshTask;
    private InjectionStatus lastInjectionStatus=new(false,0,0,0,0,0,"Checking microphone status.");
    private string[] StatusEndpoints()=>Settings.CaptureDeviceIds.Count>0?Settings.CaptureDeviceIds.ToArray():string.IsNullOrWhiteSpace(Settings.CaptureDeviceId)?[]:[Settings.CaptureDeviceId];
    private void RefreshStatus()
    {
        if(closing)return;
        if(audio.IsForwarding){MicStatus.Text="Forwarding · "+audio.ForwardingSourceName;MicStatus.ToolTip="Audio forwarding is active. Stop playback or open Preferences → Audio to stop forwarding.";return;}
        if(statusRefreshTask is {IsCompleted:false})return;
        statusRefreshTask=ReadStatusAsync(StatusEndpoints(),injectionStatusReader);
    }
    private async Task ReadStatusAsync(string[] endpoints,Func<string[],InjectionStatus> read)
    {
        InjectionStatus result;
        try{result=await Task.Run(()=>read(endpoints));}
        catch(Exception ex){result=new(false,0,0,0,0,ex.HResult,ex.Message);}
        if(closing||audio.IsForwarding||!endpoints.SequenceEqual(StatusEndpoints()))return;
        lastInjectionStatus=result;
        MicStatus.Text=result.Connected?$"Mic ready · {microphonePeak:P0}":"Mic mixing needs setup";
        MicStatus.ToolTip=result.Message;
    }
}
