using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using SoundboardLab.Audio;

namespace SoundboardLab;

internal sealed record PreferenceDeviceSnapshot(AudioDevice[] Devices,string DefaultCaptureId,IReadOnlySet<string> InstalledCaptures)
{
    internal static PreferenceDeviceSnapshot Read(CancellationToken cancellationToken)
    {
        // This enumerator belongs solely to this background operation. Never
        // borrow the active player's COM objects or return COM objects to WPF.
        using var enumerator=new MMDeviceEnumerator();
        var devices=new List<AudioDevice>();var installed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var flow in new[]{DataFlow.Capture,DataFlow.Render}){
            cancellationToken.ThrowIfCancellationRequested();
            foreach(var endpoint in enumerator.EnumerateAudioEndPoints(flow,DeviceState.Active)){
                using(endpoint){
                    cancellationToken.ThrowIfCancellationRequested();
                    string id=endpoint.ID;devices.Add(new(id,endpoint.FriendlyName,flow==DataFlow.Capture));
                    if(flow==DataFlow.Capture&&CaptureDeviceConfiguration.IsInstalled(id))installed.Add(id);
                }
            }
        }
        string defaultId="";
        try{using var endpoint=enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia);defaultId=endpoint.ID;}catch(COMException){}
        return new(devices.ToArray(),defaultId,installed);
    }
}
