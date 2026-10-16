namespace SoundboardLab.Services;

public sealed record ForwardingRequest(uint ProcessId,bool WholePc,string Label,float Volume=1,bool MuteVoice=false);

// A latched toggle and independent held controls share one forwarding session.
// Releasing during asynchronous startup cancels that startup before it can stick on.
public sealed class ForwardingController(Func<ForwardingRequest,CancellationToken,Task> start,Func<Task> stop)
{
    private readonly object sync=new();
    private readonly SemaphoreSlim transition=new(1,1);
    private readonly HashSet<string> holds=[];
    private bool latched,running,starting;
    private ForwardingRequest? request;
    private CancellationTokenSource? pending;
    public bool Latched {get{lock(sync)return latched;}}
    public bool Desired {get{lock(sync)return latched||holds.Count>0;}}
    public bool Running {get{lock(sync)return running;}}
    public bool Starting {get{lock(sync)return starting;}}
    public event Action? Changed;
    public Task ToggleAsync(ForwardingRequest? value=null)=>Change(()=>latched=!latched,value);
    public Task HoldAsync(string owner,bool down,ForwardingRequest? value=null)=>Change(()=>{if(down)holds.Add(owner);else holds.Remove(owner);},value);
    public Task StopAsync()=>Change(()=>{latched=false;holds.Clear();},null);
    private Task Change(Action change,ForwardingRequest? value)
    {
        lock(sync){
            bool wasDesired=latched||holds.Count>0;change();bool desired=latched||holds.Count>0;
            if(desired&&!wasDesired){if(value is null){latched=false;holds.Clear();throw new ArgumentException("Choose a forwarding source.");}request=value;}
            if(!desired)pending?.Cancel();
        }
        Changed?.Invoke();return ReconcileAsync();
    }
    private async Task ReconcileAsync()
    {
        await transition.WaitAsync();
        try{
            while(true){
                bool desired;ForwardingRequest? next;lock(sync){desired=latched||holds.Count>0;next=request;if(desired==running)return;}
                if(desired){
                    using var cancellation=new CancellationTokenSource();lock(sync){starting=true;pending=cancellation;}Changed?.Invoke();
                    try{await start(next!,cancellation.Token);lock(sync)running=true;}
                    catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
                    catch{lock(sync){latched=false;holds.Clear();}throw;}
                    finally{lock(sync){starting=false;pending=null;}Changed?.Invoke();}
                }else{try{await stop();}finally{lock(sync)running=false;Changed?.Invoke();}}
            }
        }finally{transition.Release();}
    }
}
