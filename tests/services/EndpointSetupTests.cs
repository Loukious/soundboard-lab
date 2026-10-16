using System.Text.Json;
using Microsoft.Win32;
using SoundboardLab.Audio;

internal static class EndpointSetupTests
{
    private const string First="{0.0.1.00000000}.{11111111-1111-1111-1111-111111111111}";
    private const string Second="{0.0.1.00000000}.{22222222-2222-2222-2222-222222222222}";
    private const string Third="{0.0.1.00000000}.{33333333-3333-3333-3333-333333333333}";
    private const string Vendor="{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
    internal static void Run(Action<bool,string> check)
    {
        void Reject(Action action,string message){bool failed=false;try{action();}catch(ArgumentException){failed=true;}catch(InvalidDataException){failed=true;}check(failed,message);}
        var store=new MemoryStore();store.Add(First);store.Add(Second);store.Add(Third);
        string original=store.State(First);
        EndpointConfiguration.Configure(store,[First]);
        check(store.Registered==1&&store.String(First,EndpointInstallPlan.EffectName("PostMix"))==EndpointInstallPlan.Clsid,"Compiled device setup installs the selected microphone's effect.");
        check(store.String(First,EndpointInstallPlan.Custom+",2")==Vendor&&store.Backups[First].Values.Single(x=>x.Name==EndpointInstallPlan.EffectName("PostMix")).Data() as string==Vendor,"The vendor effect is chained and retained in the original backup.");
        var firstBackup=store.Backups[First];EndpointConfiguration.Configure(store,[First]);
        check(store.Registered==1&&store.Backups[First]==firstBackup,"An unchanged configured selection does not reinstall its component or replace its backup.");
        EndpointConfiguration.Configure(store,[First],true);
        check(store.String(First,EndpointInstallPlan.Custom+",2")==Vendor&&store.Backups[First].CreatedUtc==firstBackup.CreatedUtc,"Repair keeps the original vendor effect and backup creation date.");
        EndpointConfiguration.Configure(store,[First],true,"Stream",false);
        check(store.String(First,EndpointInstallPlan.EffectName("PostMix"))==Vendor&&store.String(First,EndpointInstallPlan.EffectName("Stream"))==EndpointInstallPlan.Clsid,"Changing setup mode restores the old slot before installing the new slot.");
        check(store.String(First,EndpointInstallPlan.Custom+",2")==""&&store.Backups[First].Values.Any(x=>x.Name==EndpointInstallPlan.EffectName("PostMix")),"A new empty effect slot does not chain to the mixer itself and retains all originals.");
        EndpointConfiguration.Restore(store,First);
        check(store.State(First)==original&&!store.Backups.ContainsKey(First)&&store.Archived.Contains(First),"Restore exactly reinstates every original effect and archives its backup.");
        EndpointConfiguration.Configure(store,[First]);store.Log.Clear();EndpointConfiguration.Configure(store,[Second]);
        check(store.State(First)==original&&store.ConfiguredEndpoints().SequenceEqual(new[]{Second}),"Changing the selection installs the new microphone and restores the unchecked microphone.");
        check(store.Log.IndexOf("write:"+Second)<store.Log.IndexOf("archive:"+First),"New recording devices are installed before working devices are removed.");
        EndpointConfiguration.Configure(store,[Second,First]);string beforeFirst=store.State(First),beforeSecond=store.State(Second),beforeThird=store.State(Third);
        var backupsBefore=store.Backups.ToDictionary(x=>x.Key,x=>JsonSerializer.Serialize(x.Value));
        store.FailWrite=Third;
        bool failed=false;try{EndpointConfiguration.Configure(store,[Third]);}catch(IOException){failed=true;}
        check(failed&&store.State(First)==beforeFirst&&store.State(Second)==beforeSecond&&store.State(Third)==beforeThird,"A partially written new microphone rolls back without removing working devices.");
        check(store.Backups.Count==backupsBefore.Count&&store.Backups.All(x=>backupsBefore[x.Key]==JsonSerializer.Serialize(x.Value)),"Failed installation restores prior backup contents and removes the failed new backup.");
        store.FailArchive=First;failed=false;try{EndpointConfiguration.Configure(store,[Third]);}catch(IOException){failed=true;}
        check(failed&&store.State(First)==beforeFirst&&store.State(Second)==beforeSecond&&store.State(Third)==beforeThird,"A removal failure rolls back both newly installed and already restored devices.");
        store.FailBackup=Third;failed=false;try{EndpointConfiguration.Configure(store,[Third]);}catch(IOException){failed=true;}
        check(failed&&store.State(Third)==beforeThird,"A failed backup write cannot leave the new microphone configured.");
        int registrations=store.Registered;
        Reject(()=>EndpointConfiguration.Configure(store,[]),"An empty recording-device selection is rejected.");
        Reject(()=>EndpointConfiguration.Configure(store,["not-an-endpoint"]),"Malformed endpoint IDs are rejected before setup.");
        Reject(()=>EndpointConfiguration.Configure(store,[First],true,"invalid-slot"),"Unknown effect slots are rejected before setup.");
        var valid=store.Backups[First];store.Backups[First]=valid with {EndpointPath="SOFTWARE\\unrelated"};
        Reject(()=>EndpointConfiguration.Configure(store,[First],true),"A backup cannot redirect elevated writes outside the selected endpoint.");store.Backups[First]=valid;
        store.Backups[First]=valid with {Values=[..valid.Values,valid.Values[0]]};
        Reject(()=>EndpointConfiguration.Configure(store,[First],true),"Duplicate saved properties are rejected before setup.");store.Backups[First]=valid;
        check(store.Registered==registrations,"Invalid requests never register a component or alter endpoint state.");
        var legacy=JsonSerializer.Deserialize<EndpointBackup>(JsonSerializer.Serialize(valid))!;EndpointInstallPlan.ValidateBackup(First,legacy);
        check(legacy.Values.Select(x=>x.Name).SequenceEqual(valid.Values.Select(x=>x.Name)),"The compiled setup reads the existing version-1 backup schema used by the scripts.");
        var binary=new EndpointValue("binary",true,(int)RegistryValueKind.Binary,JsonDocument.Parse("[0,128,255]").RootElement.Clone());
        check(((byte[])binary.Data()).SequenceEqual(new byte[]{0,128,255})&&((byte[])EndpointValue.From("binary",new byte[]{0,128,255},RegistryValueKind.Binary).Data()).SequenceEqual(new byte[]{0,128,255}),"Both legacy numeric-array and compiled base64 binary backups restore correctly.");
        Reject(()=>EndpointInstallPlan.Create(Third,"PostMix",store.Values[Third].Values.Select(x=>x.Name==EndpointInstallPlan.EffectName("PostMix")?EndpointValue.From(x.Name,EndpointInstallPlan.Clsid,RegistryValueKind.String):x).ToArray(),null,"test.dll"),"A missing original backup cannot cause the mixer to chain to itself.");
    }
    private sealed class MemoryStore:IEndpointSetupStore
    {
        internal readonly Dictionary<string,Dictionary<string,EndpointValue>> Values=[];
        internal readonly Dictionary<string,EndpointBackup> Backups=[];
        internal readonly List<string> Archived=[],Log=[];
        internal string? FailWrite,FailArchive,FailBackup;
        internal int Registered;
        internal void Add(string id){Values[id]=EndpointInstallPlan.ValueNames.ToDictionary(x=>x,EndpointValue.Missing);Values[id][EndpointInstallPlan.EffectName("PostMix")]=EndpointValue.From(EndpointInstallPlan.EffectName("PostMix"),Vendor,RegistryValueKind.String);}
        internal string State(string id)=>JsonSerializer.Serialize(Values[id].OrderBy(x=>x.Key));
        internal string? String(string id,string name)=>Values[id][name] is {Exists:true} value?value.Data() as string:null;
        public string[] ConfiguredEndpoints()=>Values.Keys.Where(id=>String(id,EndpointInstallPlan.Custom+",1")==id).ToArray();
        public EndpointValue[] ReadValues(string id)=>Values[id].Values.ToArray();
        public EndpointBackup? ReadBackup(string id)=>Backups.GetValueOrDefault(id);
        public void WriteValues(string id,IEnumerable<EndpointValue> values){Log.Add("write:"+id);foreach(var value in values){Values[id][value.Name]=value;if(FailWrite==id){FailWrite=null;throw new IOException("partial write fixture");}}}
        public void WriteBackup(string id,EndpointBackup? backup){if(FailBackup==id){FailBackup=null;throw new IOException("backup fixture");}if(backup is null)Backups.Remove(id);else Backups[id]=backup;}
        public void ArchiveBackup(string id){Log.Add("archive:"+id);Backups.Remove(id);if(FailArchive==id){FailArchive=null;throw new IOException("archive fixture");}Archived.Add(id);}
        public string RegisterAudioComponent(){Registered++;return "compiled-test.dll";}
    }
}
