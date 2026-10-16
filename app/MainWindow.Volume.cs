using SoundboardLab.Audio;

namespace SoundboardLab;

public partial class MainWindow
{
    private ApplicationSessionVolume? applicationSessionVolume;
    private void InitializeApplicationVolume()
    {
        // Hidden layout checks never create a stream or write Windows volumes.
        if(!IsVisible||arguments.Contains("--smoke-test"))return;
        var session=new ApplicationSessionVolume();
        try{session.Start(NullIfEmpty(Settings.RenderDeviceId),(float)Settings.SpeakerVolume);applicationSessionVolume=session;AudioService.ApplicationVolume=session;}
        catch(Exception ex){session.Dispose();Log("Application volume: "+ex.Message);}
    }
    private void SynchronizeApplicationVolume()
    {
        if(applicationSessionVolume?.TryGetVolume(out float level)!=true||Math.Abs(Settings.SpeakerVolume-level)<.001)return;
        Settings.SpeakerVolume=level;audio.SpeakerVolume=level;
        SpeakerSlider.Value=Settings.LinearVolumeSlider?level:Math.Sqrt(level);
    }
    private void DisposeApplicationVolume()
    {
        if(ReferenceEquals(AudioService.ApplicationVolume,applicationSessionVolume))AudioService.ApplicationVolume=null;
        applicationSessionVolume?.Dispose();applicationSessionVolume=null;
    }
}
