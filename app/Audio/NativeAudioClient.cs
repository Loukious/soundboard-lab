using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SoundboardLab.Audio;

internal sealed class NativeAudioClient:SafeHandleZeroOrMinusOneIsInvalid
{
    private const string Dll="SoundboardLab.Audio.dll";
    public string EndpointId {get;}
    public NativeAudioClient(string id):base(true){EndpointId=id;SetHandle(Create(id));if(IsInvalid)throw new InvalidOperationException(OpenFailureMessage(NativeInjection.SB_GetLastError()));}
    internal static string OpenFailureMessage(int code)=>code==unchecked((int)0x800700AA)
        ?"Another Soundboard Lab instance is using the microphone mixer. Exit the other instance from the tray, then try Play again."
        :$"Microphone mixing is not enabled for this device ({code:X8}). Open Preferences → Devices → Repair.";
    public NativeInjection.Status Status {get{GetStatus(this,out var status);return status;}}
    public int Push(float[] samples,int frames)=>PushSamples(this,samples,frames);
    public void Stop()=>StopClient(this);
    public void Controls(float mic,float clip,bool block,bool pause)=>SetControls(this,mic,clip,block?1:0,pause?1:0);
    protected override bool ReleaseHandle(){Destroy(handle);return true;}
    [DllImport(Dll,EntryPoint="SB_CreateClient",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern IntPtr Create(string id);
    [DllImport(Dll,EntryPoint="SB_DestroyClient",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]private static extern void Destroy(IntPtr client);
    [DllImport(Dll,EntryPoint="SB_ClientGetStatus",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]private static extern int GetStatus(NativeAudioClient client,out NativeInjection.Status status);
    [DllImport(Dll,EntryPoint="SB_ClientPush",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]private static extern int PushSamples(NativeAudioClient client,[In]float[] samples,int frames);
    [DllImport(Dll,EntryPoint="SB_ClientStop",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]private static extern void StopClient(NativeAudioClient client);
    [DllImport(Dll,EntryPoint="SB_ClientSetControls",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true)]private static extern void SetControls(NativeAudioClient client,float mic,float clip,int block,int pause);
}
