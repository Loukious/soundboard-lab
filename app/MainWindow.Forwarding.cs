using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

public partial class MainWindow
{
    private readonly ForegroundAudioTarget foregroundAudioTarget=new();
    private ForwardingController forwardingController=null!;
    private string forwardingError="";
    internal event Action? ForwardingStateChanged;
    internal bool ForwardingLatched=>forwardingController.Latched;
    internal bool ForwardingActive=>audio.IsForwarding;
    internal bool ForwardingStarting=>forwardingController.Starting;
    internal string ForwardingStatus=>forwardingError.Length>0?forwardingError:ForwardingStarting?"Starting audio forwarding…":ForwardingActive?"Forwarding: "+audio.ForwardingSourceName:"Off";
    private void InitializeForwarding()
    {
        forwardingController=new(async(request,token)=>{
            try{
                await StopAsync();token.ThrowIfCancellationRequested();
                await EnsureMicrophoneActiveAsync();token.ThrowIfCancellationRequested();
                audio.ForwardingVolume=request.Volume;audio.ForwardingMuteVoice=request.MuteVoice;
                await audio.StartForwardingAsync(request.ProcessId,request.WholePc,request.Label,token);
            }catch{if(!Settings.ShowMicrophoneLevel)StopMicMonitor();throw;}
        },async()=>{await audio.StopForwardingAsync();await StopAsync();if(!Settings.ShowMicrophoneLevel)StopMicMonitor();});
        forwardingController.Changed+=()=>ForwardingStateChanged?.Invoke();
        audio.ForwardingChanged+=(_,_)=>Dispatcher.BeginInvoke(async()=>{
            if(!audio.IsForwarding&&forwardingController.Desired){await forwardingController.StopAsync();}
            ForwardingStateChanged?.Invoke();
        });
    }
    private ForwardingRequest? ForwardingRequestFor(AppPreferences settings)
    {
        if(forwardingController.Desired)return null;
        if(!Audio.AudioService.SupportsAudioForwarding)throw new PlatformNotSupportedException("Audio forwarding requires Windows build 20348 or later.");
        bool wholePc=settings.ForwardingSource=="Whole PC";
        var target=wholePc?((uint)Environment.ProcessId,"Whole PC"):foregroundAudioTarget.Resolve();
        return new(target.Item1,wholePc,target.Item2,(float)settings.ForwardingVolume,settings.ForwardingMuteVoice);
    }
    internal Task ToggleForwardingAsync(AppPreferences? settings=null)=>RunForwardingAction(async()=>await forwardingController.ToggleAsync(forwardingController.Latched?null:ForwardingRequestFor(settings??Settings)));
    internal Task HoldForwardingAsync(string owner,bool down,AppPreferences? settings=null)=>RunForwardingAction(async()=>await forwardingController.HoldAsync(owner,down,down?ForwardingRequestFor(settings??Settings):null));
    internal Task StopForwardingAsync()=>RunForwardingAction(forwardingController.StopAsync);
    private async Task RunForwardingAction(Func<Task> action)
    {
        try{forwardingError="";await action();}
        catch(Exception ex){forwardingError=ex.Message;Log("Audio forwarding: "+ex.Message);}
        ForwardingStateChanged?.Invoke();
    }
}
