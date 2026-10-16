using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace SoundboardLab.Audio;

internal interface IForwardingSource:IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    void Read(Span<float> samples);
    Exception? Failure {get;}
}

// Capture only: no rendering stream, file writer, endpoint-volume API or mixer
// registration. Whole-PC mode excludes this process tree to avoid self-capture.
internal sealed class ProcessLoopbackSource(uint processId,bool wholePc):IForwardingSource
{
    private readonly StereoFrameBuffer buffer=new(5760);
    private readonly CancellationTokenSource stop=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
    private Exception? failure;
    public Exception? Failure=>Volatile.Read(ref failure);
    public static bool IsSupported=>OperatingSystem.IsWindowsVersionAtLeast(10,0,20348);
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if(!IsSupported)throw new PlatformNotSupportedException("Audio forwarding requires Windows build 20348 or later (Windows 11 or Windows Server 2022+).");
        if(processId==0)throw new ArgumentException("Choose a running application to forward.");
        worker=Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private void Run()
    {
        bool initialized=false;
        try{
            Marshal.ThrowExceptionForHR(CoInitializeEx(0,0));initialized=true;
            using var target=wholePc?null:Process.GetProcessById((int)processId);
            if(target is not null)_=target.SafeHandle; // Keep the original process identity until capture ends.
            using var client=ActivateAsync(processId,wholePc,stop.Token).GetAwaiter().GetResult();
            using var signal=new AutoResetEvent(false);
            client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.Loopback|AudioClientStreamFlags.EventCallback,0,0,WaveFormat.CreateIeeeFloatWaveFormat(48000,2),Guid.Empty);
            client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());var capture=client.AudioCaptureClient;
            float[] packet=new float[Math.Max(960,client.BufferSize*2)];
            client.Start();ready.TrySetResult();
            try{
                while(!stop.IsCancellationRequested){
                    if(target?.HasExited==true)throw new InvalidOperationException("The source app closed.");
                    signal.WaitOne(20);
                    while(!stop.IsCancellationRequested&&capture.GetNextPacketSize()>0){
                        var pointer=capture.GetBuffer(out int frames,out var flags,out _,out _);
                        try{
                            int length=checked(frames*2);if(packet.Length<length)packet=new float[length];
                            if((flags&AudioClientBufferFlags.Silent)!=0)Array.Clear(packet,0,length);else Marshal.Copy(pointer,packet,0,length);
                            buffer.Write(packet.AsSpan(0,length),keepNewest:true);
                        }finally{capture.ReleaseBuffer(frames);}
                    }
                }
            }finally{client.Stop();}
        }catch(OperationCanceledException)when(stop.IsCancellationRequested){ready.TrySetCanceled();}
        catch(Exception ex){Volatile.Write(ref failure,ex);ready.TrySetException(ex);}
        finally{if(initialized)CoUninitialize();}
    }
    public void Read(Span<float> samples)=>buffer.Read(samples);
    public void Dispose(){stop.Cancel();worker?.GetAwaiter().GetResult();stop.Dispose();buffer.Clear();}

    private static async Task<AudioClient> ActivateAsync(uint processId,bool exclude,CancellationToken cancellationToken)
    {
        var parameters=new ActivationParameters {Type=1,ProcessId=processId,Mode=exclude?1:0};
        nint blob=Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
        IActivateAudioInterfaceAsyncOperation? operation=null;var completion=new Completion(cancellationToken);
        try{
            Marshal.StructureToPtr(parameters,blob,false);
            var variant=new BlobVariant {Type=65,Size=Marshal.SizeOf<ActivationParameters>(),Data=blob};
            Guid iid=typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback",ref iid,ref variant,completion,out operation));
        }finally{Marshal.FreeHGlobal(blob);}
        try{return new AudioClient((IAudioClient)await completion.Result.Task.ConfigureAwait(false));}
        finally{if(operation is not null)Marshal.ReleaseComObject(operation);GC.KeepAlive(completion);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct ActivationParameters {public int Type;public uint ProcessId;public int Mode;}
    [StructLayout(LayoutKind.Explicit,Size=24)] private struct BlobVariant {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public int Size;[FieldOffset(16)]public nint Data;}
    [ComImport,Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IAgileObject {}
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion(CancellationToken cancellationToken):IActivateAudioInterfaceCompletionHandler,IAgileObject
    {
        public TaskCompletionSource<object> Result {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try{
                operation.GetActivateResult(out int result,out object activated);
                if(result<0){if(activated is not null)Marshal.ReleaseComObject(activated);Marshal.ThrowExceptionForHR(result);}
                if(activated is null)throw new InvalidOperationException("Windows did not return an audio capture interface.");
                if(cancellationToken.IsCancellationRequested){Marshal.ReleaseComObject(activated);Result.TrySetCanceled(cancellationToken);}else Result.TrySetResult(activated);
            }catch(Exception ex){Result.TrySetException(ex);}
        }
    }
    [DllImport("Mmdevapi.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string path,ref Guid iid,ref BlobVariant parameters,IActivateAudioInterfaceCompletionHandler completion,out IActivateAudioInterfaceAsyncOperation operation);
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
}
