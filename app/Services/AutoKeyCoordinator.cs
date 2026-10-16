using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SoundboardLab.Models;
namespace SoundboardLab.Services;

public sealed record AutoKeyContext(string ForegroundProcess,bool Fullscreen,IReadOnlySet<string> RunningProcesses);
public static class AutoKeyConditions
{
    public static bool Matches(AutoKeyEntry rule,bool fromHotkey,bool microphone,AutoKeyContext context)
    {
        if(!microphone||!rule.Enabled||(rule.OnlyHotkey&&!fromHotkey))return false;
        var names=rule.Applications.Split([',',';','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(x=>Path.GetFileNameWithoutExtension(x)??"").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rule.Condition switch {"Always"=>true,"Foreground application is fullscreen"=>context.Fullscreen,"Certain application is running"=>names.Any(context.RunningProcesses.Contains),"Certain application is in foreground"=>names.Contains(context.ForegroundProcess),_=>false};
    }
    public static AutoKeyContext Capture()
    {
        string foreground="";var window=GetForegroundWindow();GetWindowThreadProcessId(window,out uint id);
        try{using var process=Process.GetProcessById((int)id);foreground=process.ProcessName;}catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        var running=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var process in Process.GetProcesses())using(process){try{running.Add(process.ProcessName);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
        bool fullscreen=false;if(window!=GetShellWindow()&&window!=GetDesktopWindow()&&GetWindowRect(window,out var rect)){var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};if(GetMonitorInfo(MonitorFromWindow(window,2),ref info))fullscreen=rect.Left<=info.Monitor.Left&&rect.Top<=info.Monitor.Top&&rect.Right>=info.Monitor.Right&&rect.Bottom>=info.Monitor.Bottom;}
        return new(foreground,fullscreen,running);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{public int Size;public Rect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}

public sealed class AutoKeyCoordinator:IDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly List<(AutoKeyService Service,bool Pulse)> active=[];
    private readonly AutoKeyService legacy=new();
    public AutoKeyOptions Options {get;set;}=new();
    public IReadOnlyList<AutoKeyEntry> Rules {get;set;}=[];
    public async Task<bool> BeginPlaybackAsync(CancellationToken token=default,bool fromHotkey=false,bool microphone=true)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try{
            if(!Options.Enabled||!microphone)return true;
            if(Rules.Count==0){if(string.IsNullOrWhiteSpace(Options.Gesture))return true;legacy.Options=Options;return await legacy.BeginPlaybackAsync(token).ConfigureAwait(false);}
            if(active.Count>0)return true;
            var context=AutoKeyConditions.Capture();
            foreach(var rule in Rules.Where(x=>AutoKeyConditions.Matches(x,fromHotkey,microphone,context))) {
                bool pulse=rule.Type=="Press before and after";
                var service=new AutoKeyService(new(){Enabled=true,Gesture=rule.Key,LeadDelayMs=0,TailDelayMs=pulse?40:Options.TailDelayMs});
                try{await service.BeginPlaybackAsync(token).ConfigureAwait(false);if(pulse&&!service.OwnsKeys){service.Dispose();continue;}if(pulse)await service.EndPlaybackAsync().ConfigureAwait(false);active.Add((service,pulse));}catch{service.Dispose();throw;}
            }
            if(active.Count>0&&Options.LeadDelayMs>0)await Task.Delay(Options.LeadDelayMs,token).ConfigureAwait(false);
            return true;
        }catch{await ReleaseActive().ConfigureAwait(false);throw;}finally{gate.Release();}
    }
    public void EndPlayback()=>_=EndPlaybackAsync();
    public async Task EndPlaybackAsync(){await gate.WaitAsync().ConfigureAwait(false);try{await legacy.EndPlaybackAsync().ConfigureAwait(false);await ReleaseActive().ConfigureAwait(false);}finally{gate.Release();}}
    private async Task ReleaseActive(){var release=active.ToArray();active.Clear();Exception? error=null;foreach(var value in release){try{if(value.Pulse)await value.Service.BeginPlaybackAsync().ConfigureAwait(false);await value.Service.EndPlaybackAsync().ConfigureAwait(false);}catch(Exception ex){error??=ex;}finally{value.Service.Dispose();}}if(error is not null)throw error;}
    public void Dispose(){EndPlaybackAsync().GetAwaiter().GetResult();legacy.Dispose();gate.Dispose();}
}
