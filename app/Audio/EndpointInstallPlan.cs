using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SoundboardLab.Audio;

internal sealed record EndpointValue(string Name,bool Exists,int Kind,JsonElement Value)
{
    internal static EndpointValue Missing(string name)=>new(name,false,0,JsonSerializer.SerializeToElement<object?>(null));
    internal static EndpointValue From(string name,object value,RegistryValueKind kind)=>new(name,true,(int)kind,JsonSerializer.SerializeToElement(value));
    internal object Data()=> (RegistryValueKind)Kind switch {
        RegistryValueKind.DWord=>Value.GetInt32(),RegistryValueKind.QWord=>Value.GetInt64(),
        RegistryValueKind.Binary=>Value.ValueKind==JsonValueKind.String?Value.GetBytesFromBase64():Value.EnumerateArray().Select(x=>x.GetByte()).ToArray(),
        RegistryValueKind.MultiString=>Value.EnumerateArray().Select(x=>x.GetString()??"").ToArray(),
        RegistryValueKind.String or RegistryValueKind.ExpandString=>Value.GetString()??"",
        _=>throw new InvalidDataException("Unsupported endpoint registry value type.")
    };
}

internal sealed record EndpointBackup(int Version,string EndpointId,string EndpointPath,string EffectSlot,string EffectName,string DllPath,string CreatedUtc,EndpointValue[] Values);
internal sealed record EndpointPlan(EndpointBackup Backup,string ChainedEffect,EndpointValue? RestoreEffect);

internal static class EndpointInstallPlan
{
    internal const string Clsid="{AE6B213A-2854-47D6-93D6-CA9F22880D3E}";
    internal const string Custom="{5f294d10-e843-43ba-8860-326e9f173109}";
    internal const string Effect="{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    internal const string DisableEffects="{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    internal const string CapturePath=@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    internal static string EffectName(string slot)=>Effect+","+(slot switch {"PostMix"=>2,"Stream"=>5,"Mode"=>6,"Endpoint"=>7,_=>throw new ArgumentException("Invalid setup mode.")});
    internal static string EndpointGuid(string id)
    {
        if(!Regex.IsMatch(id,@"^\{0\.0\.1\.[0-9a-fA-F]{8}\}\.\{[0-9a-fA-F-]{36}\}$")||!Guid.TryParse(id[^38..],out _))throw new ArgumentException("Select a full Windows recording endpoint ID.");
        return id[^38..];
    }
    internal static string PathFor(string id)=>CapturePath+@"\"+EndpointGuid(id);
    internal static string[] ValueNames=>[EffectName("PostMix"),EffectName("Stream"),EffectName("Mode"),EffectName("Endpoint"),Custom+",1",Custom+",2",DisableEffects];
    internal static void ValidateBackup(string id,EndpointBackup backup)
    {
        if(backup.Version!=1||!string.Equals(backup.EndpointId,id,StringComparison.OrdinalIgnoreCase)||!string.Equals(backup.EndpointPath,PathFor(id),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The saved endpoint backup does not match this microphone.");
        if(backup.EffectName!=EffectName(backup.EffectSlot)||backup.Values.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=backup.Values.Length||backup.Values.Any(x=>!ValueNames.Contains(x.Name,StringComparer.OrdinalIgnoreCase)))throw new InvalidDataException("Invalid saved endpoint effects.");
        if(backup.Values.Count(x=>x.Name==backup.EffectName)!=1)throw new InvalidDataException("The saved previous effect is missing.");
        foreach(var value in backup.Values.Where(x=>x.Exists))_=value.Data();
    }
    internal static EndpointPlan Create(string id,string slot,EndpointValue[] current,EndpointBackup? previous,string dll)
    {
        string name=EffectName(slot);if(previous is not null)ValidateBackup(id,previous);
        var original=previous?.Values.SingleOrDefault(x=>x.Name==name)??current.Single(x=>x.Name==name);
        string chain=original.Exists?original.Data() as string??throw new InvalidDataException("The previous audio effect is not a string."):"";
        if(chain.Equals(Clsid,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Cannot chain the mixer to itself without an original effect backup.");
        var values=(previous?.Values??[]).ToList();
        foreach(string key in new[]{name,Custom+",1",Custom+",2",DisableEffects})if(!values.Any(x=>x.Name==key))values.Add(current.Single(x=>x.Name==key));
        var backup=new EndpointBackup(1,id,PathFor(id),slot,name,dll,previous?.CreatedUtc??DateTime.UtcNow.ToString("o"),values.ToArray());
        return new(backup,chain,previous is not null&&previous.EffectName!=name?previous.Values.Single(x=>x.Name==previous.EffectName):null);
    }
}

internal interface IEndpointSetupStore
{
    string[] ConfiguredEndpoints();
    EndpointValue[] ReadValues(string id);
    EndpointBackup? ReadBackup(string id);
    void WriteValues(string id,IEnumerable<EndpointValue> values);
    void WriteBackup(string id,EndpointBackup? backup);
    void ArchiveBackup(string id);
    string RegisterAudioComponent();
}

internal static class EndpointConfiguration
{
    internal static void Configure(IEndpointSetupStore store,IEnumerable<string> selected,bool repair=false,string? slot=null,bool removeOthers=true)
    {
        string[] ids=selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(ids.Length==0)throw new ArgumentException("Check at least one recording device.");
        foreach(string id in ids)_=EndpointInstallPlan.EndpointGuid(id);
        if(slot is not null)_=EndpointInstallPlan.EffectName(slot);
        var changes=new List<(string Id,EndpointValue[] Before,EndpointBackup? Backup,EndpointPlan? Plan)>();
        foreach(string id in ids){
            var values=store.ReadValues(id);var backup=store.ReadBackup(id);
            bool installed=values.Any(x=>x.Name==EndpointInstallPlan.Custom+",1"&&x.Exists&&string.Equals(x.Data() as string,id,StringComparison.OrdinalIgnoreCase))&&values.Any(x=>x.Name.StartsWith(EndpointInstallPlan.Effect+",")&&x.Exists&&string.Equals(x.Data() as string,EndpointInstallPlan.Clsid,StringComparison.OrdinalIgnoreCase));
            if(installed&&!repair&&slot is null)continue;
            changes.Add((id,values,backup,EndpointInstallPlan.Create(id,slot??backup?.EffectSlot??"PostMix",values,backup,"")));
        }
        if(removeOthers)foreach(string id in store.ConfiguredEndpoints().Where(x=>!ids.Contains(x,StringComparer.OrdinalIgnoreCase))){
            var backup=store.ReadBackup(id)??throw new InvalidDataException("The original microphone effects backup is missing.");EndpointInstallPlan.ValidateBackup(id,backup);
            changes.Add((id,store.ReadValues(id),backup,null));
        }
        // Validate all endpoints/backups before changing any endpoint. Install new
        // selections before restoring devices that were unchecked.
        string dll=changes.Any(x=>x.Plan is not null)?store.RegisterAudioComponent():"";
        var applied=new List<(string Id,EndpointValue[] Before,EndpointBackup? Backup)>();
        try{
            foreach(var change in changes){
                applied.Add((change.Id,change.Before,change.Backup));
                if(change.Plan is {} plan){
                    store.WriteBackup(change.Id,plan.Backup with {DllPath=dll});
                    if(plan.RestoreEffect is {} old)store.WriteValues(change.Id,[old]);
                    store.WriteValues(change.Id,[EndpointValue.From(EndpointInstallPlan.Custom+",1",change.Id,RegistryValueKind.String),EndpointValue.From(EndpointInstallPlan.Custom+",2",plan.ChainedEffect,RegistryValueKind.String),EndpointValue.From(plan.Backup.EffectName,EndpointInstallPlan.Clsid,RegistryValueKind.String),EndpointValue.From(EndpointInstallPlan.DisableEffects,0,RegistryValueKind.DWord)]);
                }else{store.WriteValues(change.Id,change.Backup!.Values);store.ArchiveBackup(change.Id);}
            }
        }catch(Exception error){
            var errors=new List<Exception>{error};
            foreach(var change in applied.AsEnumerable().Reverse()){
                try{store.WriteValues(change.Id,change.Before);store.WriteBackup(change.Id,change.Backup);}catch(Exception rollback){errors.Add(rollback);}
            }
            if(errors.Count>1)throw new AggregateException("Device setup failed and some effects could not be restored. The endpoint backups have been retained.",errors);
            throw;
        }
    }
    internal static void Restore(IEndpointSetupStore store,string id)
    {
        var backup=store.ReadBackup(id)??throw new InvalidDataException("The original microphone effects backup is missing.");EndpointInstallPlan.ValidateBackup(id,backup);
        var before=store.ReadValues(id);
        try{store.WriteValues(id,backup.Values);store.ArchiveBackup(id);}
        catch{store.WriteValues(id,before);store.WriteBackup(id,backup);throw;}
    }
}
