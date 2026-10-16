using Microsoft.Win32;
namespace SoundboardLab.Audio;
public static class CaptureDeviceConfiguration
{
    public static string GetEffectSlot(string endpoint)
    {
        string guid=System.Text.RegularExpressions.Regex.Match(endpoint,@"\{[0-9a-fA-F-]{36}\}$").Value;if(guid.Length==0)return "PostMix";
        using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\"+guid+@"\FxProperties");
        foreach(var (pid,slot) in new[]{(2,"PostMix"),(5,"Stream"),(6,"Mode"),(7,"Endpoint")})if(Convert.ToString(key?.GetValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},"+pid))?.Contains("AE6B213A-2854-47D6-93D6-CA9F22880D3E",StringComparison.OrdinalIgnoreCase)==true)return slot;
        return "PostMix";
    }
    public static bool IsInstalled(string endpoint)
    {
        string guid=System.Text.RegularExpressions.Regex.Match(endpoint,@"\{[0-9a-fA-F-]{36}\}$").Value;if(guid.Length==0)return false;
        using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\"+guid+@"\FxProperties");
        return key?.GetValue("{5f294d10-e843-43ba-8860-326e9f173109},1") as string==endpoint && key.GetValueNames().Any(name=>Convert.ToString(key.GetValue(name))?.Contains("AE6B213A-2854-47D6-93D6-CA9F22880D3E",StringComparison.OrdinalIgnoreCase)==true);
    }
}
