using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoundboardLab.Services;

internal sealed class ForegroundAudioTarget
{
    private uint lastProcess;
    public void Observe()
    {
        nint window=GetForegroundWindow();if(window==0||window==GetShellWindow())return;
        GetWindowThreadProcessId(window,out uint process);
        if(process!=0&&process!=Environment.ProcessId)lastProcess=process;
    }
    public (uint Id,string Name) Resolve()
    {
        Observe();if(lastProcess==0)throw new InvalidOperationException("Switch to the app you want to forward, then use its forwarding hotkey.");
        try{using var process=Process.GetProcessById((int)lastProcess);if(process.HasExited)throw new InvalidOperationException();return(lastProcess,process.ProcessName);}
        catch(ArgumentException){throw new InvalidOperationException("The previous foreground app has closed. Select another app and use the forwarding hotkey.");}
    }
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern nint GetShellWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint processId);
}
