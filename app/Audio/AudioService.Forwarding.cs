namespace SoundboardLab.Audio;

public sealed partial class AudioService
{
    private sealed class ForwardingSession(IForwardingSource source,string label)
    {
        public IForwardingSource Source {get;}=source;
        public string Label {get;}=label;
        public ForwardingMixer Mixer {get;}=new();
        public CancellationTokenSource Stop {get;}=new();
        public Task Worker {get;set;}=Task.CompletedTask;
    }
    private ForwardingSession? forwarding;
    private float forwardingVolume=1;
    private bool forwardingMuteVoice;
    public static bool SupportsAudioForwarding=>ProcessLoopbackSource.IsSupported;
    public bool IsForwarding=>Volatile.Read(ref forwarding) is not null;
    public string ForwardingSourceName=>Volatile.Read(ref forwarding)?.Label??"";
    public float ForwardingVolume {get=>forwardingVolume;set=>forwardingVolume=ValidateGain(value);}
    public bool ForwardingMuteVoice {get=>forwardingMuteVoice;set{forwardingMuteVoice=value;lock(gate)UpdateControls();}}
    public event EventHandler? ForwardingChanged;
    public Task StartForwardingAsync(uint processId,bool wholePc,string label,CancellationToken cancellationToken=default)=>StartForwardingCoreAsync(new ProcessLoopbackSource(wholePc?(uint)Environment.ProcessId:processId,wholePc),label,cancellationToken);
    private async Task StartForwardingCoreAsync(IForwardingSource source,string label,CancellationToken cancellationToken)
    {
        ForwardingSession? session=null;
        try{
            await StopForwardingAsync().ConfigureAwait(false);
            await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
            try{
                ObjectDisposedException.ThrowIf(disposed,this);
                await StopCoreAsync().ConfigureAwait(false);
                var status=GetInjectionStatus();if(!status.Connected)throw new InvalidOperationException(status.Message);
                await source.StartAsync(cancellationToken).ConfigureAwait(false);cancellationToken.ThrowIfCancellationRequested();
                lock(gate){
                    session=new(source,label);forwarding=session;StopNativeTransport();UpdateControls();
                    session.Worker=Task.Run(()=>ForwardAsync(session));
                }
            }finally{transition.Release();}
        }catch{if(session is null)source.Dispose();throw;}
        ForwardingChanged?.Invoke(this,EventArgs.Empty);
    }
    public async Task StopForwardingAsync()
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try{
            ForwardingSession? session;lock(gate){session=forwarding;session?.Stop.Cancel();}
            if(session is null)return;
            await StopCoreAsync().ConfigureAwait(false);
            await session.Worker.ConfigureAwait(false);
        }finally{transition.Release();}
    }
    private async Task ForwardAsync(ForwardingSession session)
    {
        var chunk=new float[960];Exception? failure=null;
        try{
            while(!session.Stop.IsCancellationRequested){
                if(session.Source.Failure is {} error)throw new InvalidOperationException("Audio forwarding stopped: "+error.Message,error);
                lock(gate){
                    if(!ReferenceEquals(forwarding,session))break;
                    if(!GetInjectionStatus().Connected)throw new InvalidOperationException("Audio forwarding stopped because the microphone mixer disconnected.");
                    for(int batch=0;batch<4&&NativeTransportQueued()<1440;batch++){
                        session.Source.Read(chunk);session.Mixer.Render(chunk,clipVolume,forwardingVolume,paused);
                        if(PushNativeTransport(chunk,480)!=480)throw new InvalidOperationException("The microphone mixer could not accept forwarded audio.");
                    }
                }
                await Task.Delay(5,session.Stop.Token).ConfigureAwait(false);
            }
        }catch(OperationCanceledException)when(session.Stop.IsCancellationRequested){}
        catch(Exception ex){failure=ex;}
        finally{
            try{session.Source.Dispose();}catch(Exception ex){failure??=ex;}
            finally{
                lock(gate){if(ReferenceEquals(forwarding,session)){forwarding=null;StopNativeTransport();UpdateControls();}}
                session.Stop.Dispose();ForwardingChanged?.Invoke(this,EventArgs.Empty);
            }
        }
        if(failure is not null)ReportError(failure);
    }
}
