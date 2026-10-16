using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
namespace SoundboardLab.Audio;

public sealed class ApplicationVolumeDucker:IDisposable
{
    private readonly CancellationTokenSource stop=new();
    private readonly Task worker;
    private readonly TaskCompletionSource started=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Started=>started.Task;
    public ApplicationVolumeDucker(){worker=Task.Run(RunAsync);}
    public void Update(float multiplier) {if(worker.IsFaulted)worker.GetAwaiter().GetResult();}
    public static int PreferenceValue(string selection)=>selection.StartsWith("Mute",StringComparison.Ordinal)?0:selection.Contains("80%",StringComparison.Ordinal)?1:2;
    public static void ConfigurePreference(string selection){using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Multimedia\Audio");key.SetValue("UserDuckingPreference",PreferenceValue(selection),RegistryValueKind.DWord);}
    public static Task ProbeAsync()=>Task.Run(()=>{using var client=CreateClient();client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.None,1000000,0,client.MixFormat,Guid.Empty);});
    private static AudioClient CreateClient()
    {
        object enumerator=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;NativeDevice? device=null;object? raw=null;
        try{
            Marshal.ThrowExceptionForHR(((NativeEnumerator)enumerator).GetDefaultAudioEndpoint(0,2,out device));
            Guid iid=typeof(NAudio.CoreAudioApi.Interfaces.IAudioClient2).GUID;Marshal.ThrowExceptionForHR(device.Activate(ref iid,23,IntPtr.Zero,out raw));
            var properties=new AudioClientProperties {cbSize=(uint)Marshal.SizeOf<AudioClientProperties>(),bIsOffload=0,eCategory=AudioStreamCategory.Communications};
            IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientProperties>());try{Marshal.StructureToPtr(properties,pointer,false);IntPtr unknown=Marshal.GetIUnknownForObject(raw),client2=IntPtr.Zero;try{Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown,in iid,out client2));var method=Marshal.ReadIntPtr(Marshal.ReadIntPtr(client2),16*IntPtr.Size);Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<SetProperties>(method)(client2,pointer));}finally{if(client2!=IntPtr.Zero)Marshal.Release(client2);Marshal.Release(unknown);}}finally{Marshal.FreeHGlobal(pointer);}
            return new AudioClient((NAudio.CoreAudioApi.Interfaces.IAudioClient)raw);
        }catch{if(raw is not null)Marshal.ReleaseComObject(raw);throw;}
        finally{if(device is not null)Marshal.ReleaseComObject(device);Marshal.ReleaseComObject(enumerator);}
    }
    private async Task RunAsync()
    {
        try{
            using var client=CreateClient();
            client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.None,1000000,0,client.MixFormat,Guid.Empty);
            var render=client.AudioRenderClient;
            client.Start();started.TrySetResult();
            try{while(!stop.IsCancellationRequested){int frames=client.BufferSize-client.CurrentPadding;if(frames>0){render.GetBuffer(frames);render.ReleaseBuffer(frames,AudioClientBufferFlags.Silent);}await Task.Delay(15,stop.Token).ConfigureAwait(false);}}
            catch(OperationCanceledException){}finally{client.Stop();}
        }catch(Exception ex){started.TrySetException(ex);throw;}
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int SetProperties(IntPtr client,IntPtr properties);
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeEnumerator {
        [PreserveSig]int EnumAudioEndpoints(int flow,uint mask,out IntPtr collection);
        [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out NativeDevice device);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface NativeDevice {
        [PreserveSig]int Activate(ref Guid iid,uint context,IntPtr activation,[MarshalAs(UnmanagedType.IUnknown)]out object result);
    }
    public void Dispose(){stop.Cancel();try{worker.GetAwaiter().GetResult();}catch(Exception){}finally{stop.Dispose();}}
}
