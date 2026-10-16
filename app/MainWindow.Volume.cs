using SoundboardLab.Audio;
using System.Windows.Threading;

namespace SoundboardLab;

public partial class MainWindow
{
    private ApplicationSessionVolume? applicationSessionVolume;
    private int applicationVolumeUpdatePending;
    private bool synchronizingApplicationVolume;
    private void InitializeApplicationVolume()
    {
        // Hidden layout checks never create a stream or write Windows volumes.
        if(!IsVisible||arguments.Contains("--smoke-test"))return;
        var session=new ApplicationSessionVolume();
        try{
            session.Start(NullIfEmpty(Settings.RenderDeviceId),(float)Settings.SpeakerVolume);applicationSessionVolume=session;AudioService.ApplicationVolume=session;
            session.VolumeChanged+=(_,_)=>{
                if(Dispatcher.HasShutdownStarted||Interlocked.Exchange(ref applicationVolumeUpdatePending,1)!=0)return;
                Dispatcher.BeginInvoke(DispatcherPriority.DataBind,()=>{
                    Interlocked.Exchange(ref applicationVolumeUpdatePending,0);
                    if(ReferenceEquals(applicationSessionVolume,session))SynchronizeApplicationVolume();
                });
            };
        }
        catch(Exception ex){session.Dispose();Log("Application volume: "+ex.Message);}
    }
    private void SynchronizeApplicationVolume()
    {
        if(applicationSessionVolume?.TryGetVolume(out float level)!=true||!float.IsFinite(level))return;
        ApplyApplicationVolume(level);
    }
    private void ApplyApplicationVolume(float level)
    {
        level=Math.Clamp(level,0,1);Settings.SpeakerVolume=level;audio.SynchronizeSpeakerVolume(level);
        synchronizingApplicationVolume=true;
        try{SpeakerSlider.Value=Settings.LinearVolumeSlider?level:Math.Sqrt(level);}
        finally{synchronizingApplicationVolume=false;}
    }
    private void DisposeApplicationVolume()
    {
        if(ReferenceEquals(AudioService.ApplicationVolume,applicationSessionVolume))AudioService.ApplicationVolume=null;
        applicationSessionVolume?.Dispose();applicationSessionVolume=null;
    }
}
