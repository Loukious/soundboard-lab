using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SoundboardLab.Audio;

internal static class ReadOnlyInjectionStatus
{
    internal static InjectionStatus Read(string[] endpoints)
    {
        if(endpoints.Length==0){
            using var enumerator=new MMDeviceEnumerator();
            using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications);
            endpoints=[device.ID];
        }
        var states=endpoints.Select(id=>{Query(id,out var state);return state;}).ToArray();
        bool connected=states.All(x=>x.Connected!=0&&x.ActiveConsumers>0);
        return new(connected,states[0].SampleRate,states[0].Channels,states.Max(x=>x.QueuedFrames),states.Sum(x=>x.ActiveConsumers),states.Select(x=>x.LastError).FirstOrDefault(x=>x!=0),
            connected?"Microphone mixing is ready.":"Microphone mixing is waiting for a selected device. Reopen your recording app or run the microphone test.");
    }
    [DllImport("SoundboardLab.Audio.dll",EntryPoint="SB_QueryEndpointStatus",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern int Query(string endpointId,out NativeInjection.Status status);
}
