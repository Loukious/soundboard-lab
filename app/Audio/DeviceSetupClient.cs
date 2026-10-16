using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SoundboardLab.Audio;

internal sealed record DeviceSetupRequest(string Operation,string[] Endpoints,bool Repair=false,string? Slot=null)
{
    internal void Validate()
    {
        if(Operation is not ("configure" or "install" or "restore" or "restart" or "probe"))throw new ArgumentException("Invalid device setup operation.");
        if(Endpoints.Length>128)throw new ArgumentException("Too many recording devices.");
        foreach(string id in Endpoints)_=EndpointInstallPlan.EndpointGuid(id);
        if(Slot is not null)_=EndpointInstallPlan.EffectName(Slot);
        if((Operation is "install" or "restore")&&Endpoints.Length!=1)throw new ArgumentException("Select one recording device.");
        if(Operation=="configure"&&Endpoints.Length==0)throw new ArgumentException("Check at least one recording device.");
        if((Operation is "restart" or "probe")&&(Endpoints.Length!=0||Slot is not null))throw new ArgumentException("Invalid Windows Audio helper request.");
    }
}
internal sealed record DeviceSetupResult(bool Success,string Message);

internal static class DeviceSetupClient
{
    private const string PipePrefix="SoundboardLab.DeviceSetup.";
    private static readonly SemaphoreSlim SetupGate=new(1,1);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
    internal static async Task RunAsync(DeviceSetupRequest request)
    {
        request.Validate();await SetupGate.WaitAsync();
        try{await RunCoreAsync(request);}finally{SetupGate.Release();}
    }
    private static async Task RunCoreAsync(DeviceSetupRequest request)
    {
        request.Validate();string executable=Path.Combine(AppContext.BaseDirectory,"SoundboardLab.exe");
        if(!File.Exists(executable))throw new FileNotFoundException("The compiled device setup helper is missing. Reinstall the complete application package.",executable);
        string name=PipePrefix+Guid.NewGuid().ToString("N");
        // The default Windows pipe DACL permits the owner and administrators.
        // CurrentUserOnly also compares elevation and rejects our elevated child.
        // Authenticate the actual child process before sending any request.
        using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance);
        bool elevated=request.Operation!="probe";
        var start=new ProcessStartInfo(executable){UseShellExecute=elevated,Verb=elevated?"runas":"",CreateNoWindow=!elevated,WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--device-setup");start.ArgumentList.Add(name);start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var process=await Task.Run(()=>Process.Start(start)??throw new InvalidOperationException("Device setup could not start."));
        using var handshake=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connected=pipe.WaitForConnectionAsync(handshake.Token);var exited=process.WaitForExitAsync();
        if(await Task.WhenAny(connected,exited)==exited&&!pipe.IsConnected)throw new InvalidOperationException("The device setup helper exited before connecting.");
        await connected;
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint peer)||peer!=(uint)process.Id)throw new InvalidOperationException("Device setup helper identity did not match.");
        using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};using var reader=new StreamReader(pipe,leaveOpen:true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        using var completion=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string? response=await reader.ReadLineAsync(completion.Token);
        var result=response is not null?JsonSerializer.Deserialize<DeviceSetupResult>(response):null;
        await exited.WaitAsync(completion.Token);
        if(result?.Success!=true||process.ExitCode!=0)throw new InvalidOperationException(result?.Message??"Device setup did not complete.");
    }
    internal static bool IsHelperInvocation(string[] args)=>args.Length>0&&args[0]=="--device-setup";
    internal static async Task<int> RunHelperAsync(string[] args)
    {
        if(args.Length!=3||!args[1].StartsWith(PipePrefix,StringComparison.Ordinal)||!Guid.TryParseExact(args[1][PipePrefix.Length..],"N",out _)||!int.TryParse(args[2],out int parentId))return 2;
        using var parent=Process.GetProcessById(parentId);
        if(!string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))return 2;
        using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous);
        using var connect=new CancellationTokenSource(TimeSpan.FromSeconds(20));await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint peer)||peer!=(uint)parentId)return 2;
        using var reader=new StreamReader(pipe,leaveOpen:true);using var writer=new StreamWriter(pipe,leaveOpen:true){AutoFlush=true};
        DeviceSetupResult result;
        try{
            string? line=await reader.ReadLineAsync(connect.Token).ConfigureAwait(false);
            if(line is null||line.Length>65536)throw new InvalidDataException("Invalid device setup request.");
            var request=JsonSerializer.Deserialize<DeviceSetupRequest>(line)??throw new InvalidDataException("Missing device setup request.");request.Validate();
            using var identity=WindowsIdentity.GetCurrent();
            if(request.Operation!="probe"&&!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Device setup requires administrator access.");
            if(request.Operation=="restart")WindowsAudioServices.Restart();
            else{
                using var store=new WindowsEndpointSetupStore();
                if(request.Operation=="probe"){foreach(string id in store.ConfiguredEndpoints()){_=store.ReadValues(id);_=store.ReadBackup(id);}}
                else if(request.Operation=="restore")EndpointConfiguration.Restore(store,request.Endpoints[0]);
                else EndpointConfiguration.Configure(store,request.Endpoints,request.Repair,request.Slot,request.Operation=="configure");
            }
            result=new(true,"Device setup completed. Reopen recording applications to reload their audio graphs.");
        }catch(Exception error){result=new(false,error.Message);}
        await writer.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);return result.Success?0:1;
    }
}
