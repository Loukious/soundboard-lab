using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
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
                sessions[endpointId]=own;foreach(var session in own){session.Volume=level;if(level>0)session.Mute=false;}
                return true;
            } catch(Exception ex) when(ex is COMException or InvalidOperationException){foreach(var session in own)session.Dispose();sessions.Remove(endpointId);return false;}
        }
    }
    public bool TrySetVolume(float value)
    {
        if(!float.IsFinite(value)||value is <0 or >1)return false;
        lock(gate){
            requestedVolume=value;bool changed=false;
            foreach(var session in sessions.Values.SelectMany(x=>x))try{session.Volume=value;if(value>0)session.Mute=false;changed=true;}catch(COMException){}
            return changed;
        }
    }
    public bool TryGetVolume(out float value)
    {
        lock(gate){foreach(var session in sessions.Values.SelectMany(x=>x))try{value=session.Volume;return true;}catch(COMException){}value=requestedVolume;return false;}
    }
    private static IEnumerable<IAppAudioSession> EnumerateSessions(string endpointId)
    {
        using var devices=new MMDeviceEnumerator();using var device=devices.GetDevice(endpointId);
        var manager=device.AudioSessionManager;manager.RefreshSessions();var entries=manager.Sessions;
        for(int i=0;i<entries.Count;i++)yield return new Session(entries[i]);
    }
    private sealed class Session(AudioSessionControl control) : IAppAudioSession
    {
        public uint ProcessId=>control.GetProcessID;
        public float Volume {get=>control.SimpleAudioVolume.Volume;set=>control.SimpleAudioVolume.Volume=value;}
        public bool Mute {get=>control.SimpleAudioVolume.Mute;set=>control.SimpleAudioVolume.Mute=value;}
        public void Dispose()=>control.Dispose();
    }
    private sealed class SilentProvider : IWaveProvider
    {
        public WaveFormat WaveFormat {get;}=WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        public int Read(byte[] buffer,int offset,int count){Array.Clear(buffer,offset,count);return count;}
    }
    public void Dispose()
    {
        anchor?.Stop();anchor?.Dispose();anchor=null;
        lock(gate){foreach(var session in sessions.Values.SelectMany(x=>x))session.Dispose();sessions.Clear();}
    }
}
