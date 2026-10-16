using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace SoundboardLab.Audio;

internal interface IApplicationSessionVolume
{
    bool TryAttach(string endpointId);
    bool TrySetVolume(float value);
    bool TryGetVolume(out float value);
}

internal interface IAppAudioSession : IDisposable
{
    event EventHandler? VolumeChanged;
    uint ProcessId {get;}
    float Volume {get;set;}
    bool Mute {get;set;}
}

internal sealed class ApplicationSessionVolume : IApplicationSessionVolume,IDisposable
{
    private readonly object gate=new();
    private readonly Func<string,IEnumerable<IAppAudioSession>> enumerate;
    private readonly Dictionary<string,List<IAppAudioSession>> sessions=[];
    private WasapiOut? anchor;
    private float requestedVolume=1;
    private IAppAudioSession? latestSession;
    private int disposed;
    internal event EventHandler? VolumeChanged;
    internal ApplicationSessionVolume(Func<string,IEnumerable<IAppAudioSession>>? enumerate=null)=>this.enumerate=enumerate??EnumerateSessions;
    internal void Start(string? endpointId,float volume)
    {
        requestedVolume=Math.Clamp(volume,0,1);
        using var devices=new MMDeviceEnumerator();using var device=endpointId is null?devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia):devices.GetDevice(endpointId);
        // A silent shared stream keeps this application's Mixer entry available
        // between clips. Its volume is controlled through ISimpleAudioVolume.
        var output=new WasapiOut(device,AudioClientShareMode.Shared,true,40);
        try{output.Init(new SilentProvider());output.Play();anchor=output;TryAttach(device.ID);}catch{output.Dispose();throw;}
    }
    public bool TryAttach(string endpointId)
    {
        lock(gate){
            if(disposed!=0)return false;
            if(sessions.TryGetValue(endpointId,out var existing)&&existing.Count>0)return true;
            var own=new List<IAppAudioSession>();
            try {
                foreach(var session in enumerate(endpointId)){
                    // Check the process before reading or writing any volume.
                    try{if(session.ProcessId==(uint)Environment.ProcessId){own.Add(session);continue;}}catch(COMException){}
                    session.Dispose();
                }
                if(own.Count==0)return false;
                float level=TryGetVolume(out var current)?current:requestedVolume;
                sessions[endpointId]=own;foreach(var session in own){session.VolumeChanged+=SessionVolumeChanged;session.Volume=level;if(level>0)session.Mute=false;}
                return true;
            } catch(Exception ex) when(ex is COMException or InvalidOperationException){foreach(var session in own){session.VolumeChanged-=SessionVolumeChanged;session.Dispose();}sessions.Remove(endpointId);return false;}
        }
    }
    public bool TrySetVolume(float value)
    {
        if(!float.IsFinite(value)||value is <0 or >1)return false;
        lock(gate){
            if(disposed!=0)return false;
            requestedVolume=value;bool changed=false;
            foreach(var session in sessions.Values.SelectMany(x=>x))try{session.Volume=value;if(value>0)session.Mute=false;changed=true;}catch(COMException){}
            return changed;
        }
    }
    public bool TryGetVolume(out float value)
    {
        lock(gate){
            if(disposed==0){
                if(Volatile.Read(ref latestSession) is {} latest)try{value=latest.Volume;return true;}catch(COMException){}
                foreach(var session in sessions.Values.SelectMany(x=>x))try{value=session.Volume;return true;}catch(COMException){}
            }
            value=requestedVolume;return false;
        }
    }
    private void SessionVolumeChanged(object? sender,EventArgs e)
    {
        // Core Audio calls this on its notification thread. Never take the
        // volume lock or call COM here; UI consumers queue a read of the latest
        // level so delayed notifications cannot rewind a volume drag.
        if(Volatile.Read(ref disposed)!=0||sender is not IAppAudioSession session)return;
        Volatile.Write(ref latestSession,session);VolumeChanged?.Invoke(this,EventArgs.Empty);
    }
    private static IEnumerable<IAppAudioSession> EnumerateSessions(string endpointId)
    {
        using var devices=new MMDeviceEnumerator();using var device=devices.GetDevice(endpointId);
        var manager=device.AudioSessionManager;manager.RefreshSessions();var entries=manager.Sessions;
        for(int i=0;i<entries.Count;i++)yield return new Session(entries[i]);
    }
    private sealed class Session(AudioSessionControl control) : IAppAudioSession,IAudioSessionEventsHandler
    {
        private EventHandler? volumeChanged;
        private bool registered;
        public event EventHandler? VolumeChanged {
            add {volumeChanged+=value;if(!registered){control.RegisterEventClient(this);registered=true;}}
            remove {volumeChanged-=value;if(registered&&volumeChanged is null){control.UnRegisterEventClient(this);registered=false;}}
        }
        public uint ProcessId=>control.GetProcessID;
        public float Volume {get=>control.SimpleAudioVolume.Volume;set=>control.SimpleAudioVolume.Volume=value;}
        public bool Mute {get=>control.SimpleAudioVolume.Mute;set=>control.SimpleAudioVolume.Mute=value;}
        public void OnVolumeChanged(float volume,bool isMuted)=>volumeChanged?.Invoke(this,EventArgs.Empty);
        public void OnDisplayNameChanged(string displayName){}
        public void OnIconPathChanged(string iconPath){}
        public void OnChannelVolumeChanged(uint channelCount,IntPtr newVolumes,uint channelIndex){}
        public void OnGroupingParamChanged(ref Guid groupingId){}
        public void OnStateChanged(AudioSessionState state){}
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason){}
        public void Dispose(){if(registered){control.UnRegisterEventClient(this);registered=false;}volumeChanged=null;control.Dispose();}
    }
    private sealed class SilentProvider : IWaveProvider
    {
        public WaveFormat WaveFormat {get;}=WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        public int Read(byte[] buffer,int offset,int count){Array.Clear(buffer,offset,count);return count;}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        anchor?.Stop();anchor?.Dispose();anchor=null;
        lock(gate){foreach(var session in sessions.Values.SelectMany(x=>x)){session.VolumeChanged-=SessionVolumeChanged;session.Dispose();}sessions.Clear();latestSession=null;}
    }
}
