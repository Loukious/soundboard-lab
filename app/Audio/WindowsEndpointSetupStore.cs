using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace SoundboardLab.Audio;

internal sealed class WindowsEndpointSetupStore:IEndpointSetupStore,IDisposable
{
    private readonly RegistryKey machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
    private static string BackupFile(string id)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"SoundboardLab","EndpointBackups",EndpointInstallPlan.EndpointGuid(id)+".json");
    private RegistryKey OpenEffects(string id,bool write=false)=>machine.OpenSubKey(EndpointInstallPlan.PathFor(id)+@"\FxProperties",write?RegistryKeyPermissionCheck.ReadWriteSubTree:RegistryKeyPermissionCheck.ReadSubTree,write?RegistryRights.ReadKey|RegistryRights.SetValue:RegistryRights.ReadKey)??throw new InvalidOperationException("The selected recording device has no accessible effects property store.");
    public string[] ConfiguredEndpoints()
    {
        using var capture=machine.OpenSubKey(EndpointInstallPlan.CapturePath);var result=new List<string>();
        foreach(string name in capture?.GetSubKeyNames()??[]){using var fx=capture!.OpenSubKey(name+@"\FxProperties");if(fx?.GetValue(EndpointInstallPlan.Custom+",1") is string id){_=EndpointInstallPlan.EndpointGuid(id);if(!name.Equals(EndpointInstallPlan.EndpointGuid(id),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Mismatched recording-device configuration.");result.Add(id);}}
        return result.ToArray();
    }
    public EndpointValue[] ReadValues(string id)
    {
        using var fx=OpenEffects(id);var names=fx.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return EndpointInstallPlan.ValueNames.Select(name=>names.Contains(name)?EndpointValue.From(name,fx.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)!,fx.GetValueKind(name)):EndpointValue.Missing(name)).ToArray();
    }
    public EndpointBackup? ReadBackup(string id)
    {
        string file=BackupFile(id);if(!File.Exists(file))return null;
        var backup=JsonSerializer.Deserialize<EndpointBackup>(File.ReadAllText(file),Json)??throw new InvalidDataException("Invalid recording-device backup.");
        EndpointInstallPlan.ValidateBackup(id,backup);return backup;
    }
    public void WriteValues(string id,IEnumerable<EndpointValue> values)
    {
        using var fx=OpenEffects(id,true);
        foreach(var value in values){if(!EndpointInstallPlan.ValueNames.Contains(value.Name,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Invalid endpoint effect property.");if(value.Exists)fx.SetValue(value.Name,value.Data(),(RegistryValueKind)value.Kind);else fx.DeleteValue(value.Name,false);}
    }
    public void WriteBackup(string id,EndpointBackup? backup)
    {
        string file=BackupFile(id);if(backup is null){File.Delete(file);return;}
        EndpointInstallPlan.ValidateBackup(id,backup);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string pending=file+".next."+Guid.NewGuid().ToString("N");
        try{File.WriteAllText(pending,JsonSerializer.Serialize(backup,Json));if(File.Exists(file))File.Replace(pending,file,file+".previous");else File.Move(pending,file);}
        finally{if(File.Exists(pending))File.Delete(pending);}
    }
    public void ArchiveBackup(string id){string file=BackupFile(id);File.Move(file,file+".restored."+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"."+Guid.NewGuid().ToString("N"));}
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int RegisterServer();
    public string RegisterAudioComponent()
    {
        // Only load the component shipped beside this compiled helper. Requests
        // cannot supply an arbitrary elevated DLL or registry path.
        string source=Path.Combine(AppContext.BaseDirectory,"SoundboardLab.Audio.dll");
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"SoundboardLab","Audio",hash[..12]);Directory.CreateDirectory(folder);
        string installed=Path.Combine(folder,"SoundboardLab.Audio.dll");
        if(!File.Exists(installed)||!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed))).Equals(hash,StringComparison.OrdinalIgnoreCase))File.Copy(source,installed,true);
        nint library=NativeLibrary.Load(installed);
        try{Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<RegisterServer>(NativeLibrary.GetExport(library,"DllRegisterServer"))());}
        finally{NativeLibrary.Free(library);}
        return installed;
    }
    public void Dispose()=>machine.Dispose();
}
