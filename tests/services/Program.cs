using SoundboardLab.Models;
using SoundboardLab.Services;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new Exception(message); }
string temp = Path.Combine(Path.GetTempPath(), "SoundboardLab-service-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    using(var lease=new InstanceLease(temp)){Throws<IOException>(()=>{using var duplicateLease=new InstanceLease(temp);},"Two instances acquired the same library lease.");using var independentLease=new InstanceLease(Path.Combine(temp,"isolated"));Check(true,"Independent data directories could not acquire separate instance leases.");}
    using(var recoveredLease=new InstanceLease(temp))Check(true,"Instance lease was not released on shutdown.");
    string audio = Path.Combine(temp, "clip.wav"); File.WriteAllBytes(audio, [0, 1, 2, 3]);
    string statisticsState=Path.Combine(temp,"playback-statistics");
    using(var stats=new LibraryStore(statisticsState)){
        var entry=stats.AddFiles([audio]).Single();int structuralChanges=0,statisticsChanges=0;
        stats.Changed+=(_,_)=>structuralChanges++;stats.PlaybackStatisticsChanged+=(_,_)=>statisticsChanges++;
        for(int i=0;i<20;i++)stats.MarkPlayed(entry);
        Check(entry.PlayCount==20&&statisticsChanges==20&&structuralChanges==0,"Playback statistics rebuilt the structural library view or lost triggers.");
        stats.FlushPlaybackStatistics();
        using(var recoveredStats=new LibraryStore(statisticsState))Check(recoveredStats.Document.Entries.Single().PlayCount==20,"Flushing playback statistics lost recovery data.");
        stats.MarkPlayed(entry);stats.Save();await Task.Delay(350);
        using(var afterSave=new LibraryStore(statisticsState))Check(afterSave.Document.Entries.Single().PlayCount==21,"An older queued recovery overwrote an explicit save.");
        stats.MarkPlayed(entry);stats.Rename(entry,"Edited after playback");await Task.Delay(350);
        using(var afterEdit=new LibraryStore(statisticsState))Check(afterEdit.Document.Entries.Single() is {PlayCount:22,Title:"Edited after playback"},"An older playback recovery overwrote a structural edit.");
        stats.MarkPlayed(entry);await Task.Delay(500);
        using(var afterTimer=new LibraryStore(statisticsState))Check(afterTimer.Document.Entries.Single().PlayCount==23,"The playback recovery timer did not persist the completed burst.");
        stats.MarkPlayed(entry);
    }
    using(var afterClose=new LibraryStore(statisticsState))Check(afterClose.Document.Entries.Single().PlayCount==24,"Closing the library discarded pending playback statistics.");
    string state = Path.Combine(temp, "state");
    using var store = new LibraryStore(state);
    int changed = 0; store.Changed += (_, _) => changed++;
    var sound = store.AddFiles([audio, audio], "Games/Party").Single();
    Check(store.Document.Entries.Count == 1 && store.Document.Categories.Count == 2, "Deduplication or hierarchical categories failed.");
    store.Rename(sound, "New title"); Check(store.HasUnsavedChanges, "Change tracking failed.");
    Check(store.Undo() && store.Document.Entries[0].Title == "clip", "Rename undo failed.");
    Check(store.Redo() && store.Document.Entries[0].Title == "New title", "Rename redo failed.");
    sound = store.Document.Entries[0];
    string renamedAudio=Path.Combine(temp,"renamed.wav");store.RenameFile(sound,renamedAudio);Check(File.Exists(renamedAudio) && !File.Exists(audio),"Physical audio rename failed.");
    Check(store.Undo() && File.Exists(audio) && !File.Exists(renamedAudio),"Undo did not restore physical filename.");
    Check(store.Redo() && File.Exists(renamedAudio),"Redo did not reapply physical filename.");
    store.Undo();sound=store.Document.Entries[0];
    store.MarkPlayed(sound); Check(sound.PlayCount == 1 && sound.LastPlayed is not null, "Playback statistics failed.");
    store.Pin(sound,0,0);store.Pin(sound,1,3); Check(store.Document.HotbarPins.Count==2 && store.Document.HotbarPins.All(p=>p.SoundId==sound.Id),"Independent hotbar pins for one sound failed.");
    store.Unpin(sound,0);Check(store.Document.HotbarPins.Count==1 && store.Document.HotbarPins[0].Page==1,"Unpin affected a different page.");
    store.RenameCategory("Games", "Board"); Check(sound.Category == "Board/Party" && store.Document.Categories[1].Parent == "Board", "Category rename failed.");
    string categoryIcon=Path.Combine(temp,"category.png");File.WriteAllBytes(categoryIcon,[1,2,3]);
    store.UpdateCategory("*",new(){Name="All clips",IconMode="Custom",IconPath=categoryIcon,IconColor="#007DB8",Hotkey="Ctrl+F23"});
    Check(store.Document.RootCategory.Name=="All clips"&&store.Document.RootCategory.Hotkey=="Ctrl+F23"&&store.Document.Entries.Count==1,"Root category edit lost the sounds or hotkey.");
    Check(store.Undo()&&store.Document.RootCategory.Name=="My Sounds"&&store.Document.RootCategory.StockIcon=="Home","Root category edit undo failed.");
    Check(store.Redo()&&store.Document.RootCategory.IconPath==categoryIcon&&store.Document.RootCategory.Name=="All clips","Root category edit redo failed.");
    store.UpdateCategory("Board",new(){Name="Games",IconMode="Generated",IconColor="#C62828"});
    Check(store.Document.Categories.Single(c=>c.Name=="Games").IconMode=="Generated"&&store.Document.Entries[0].Category=="Games/Party","Category edit did not rename children and apply the icon atomically.");
    Check(store.Undo()&&store.Document.Entries[0].Category=="Board/Party"&&store.Document.Categories.Any(c=>c.Name=="Board/Party"),"Category edit undo did not restore its hierarchy.");
    sound=store.Document.Entries[0];
    string list = Path.Combine(temp, "saved.json"); store.Save(list);
    Check(!store.HasUnsavedChanges && store.CurrentPath == list && File.Exists(list), "Explicit save failed.");
    var originalId = sound.Id;
    store.New(); Check(store.Document.Entries.Count == 0 && store.CurrentPath is null, "New library failed.");
    store.Load(list); Check(store.Document.Entries.Single().Id == originalId, "Persistent identifier failed.");
    Check(store.Document.RootCategory.Name=="All clips"&&store.Document.RootCategory.IconColor=="#007DB8","Root category settings were not persisted.");
    string portable = Path.Combine(temp, "portable"); store.Export(portable); store.Load(Path.Combine(portable, "library.json"));
    Check(File.Exists(store.Document.Entries.Single().FilePath) && store.Document.Entries.Single().FilePath.StartsWith(portable), "Portable export did not resolve bundled paths.");
    Check(File.Exists(store.Document.RootCategory.IconPath)&&store.Document.RootCategory.IconPath.StartsWith(Path.Combine(portable,"icons")),"Portable export did not bundle the root category icon.");
    string before = JsonSerializer.Serialize(store.Document);
    Throws<FileNotFoundException>(() => store.AddFiles([audio, Path.Combine(temp, "missing.wav")]), "Missing files were accepted.");
    Check(JsonSerializer.Serialize(store.Document) == before, "Failed add changed library.");
    string unknownSpl = Path.Combine(temp, "unknown.spl"); File.WriteAllText(unknownSpl, "unknown compressed or binary format");
    Throws<NotSupportedException>(() => store.ImportSoundpad(unknownSpl), "Unsupported SPL silently accepted.");
    Check(JsonSerializer.Serialize(store.Document) == before, "SPL failure changed library.");
    string malformed = Path.Combine(temp, "malformed.json"); File.WriteAllText(malformed, "{broken");
    Throws<JsonException>(() => store.Load(malformed), "Malformed JSON accepted."); Check(JsonSerializer.Serialize(store.Document) == before, "Malformed load changed library.");
    var duplicate = new LibraryDocument(); duplicate.Entries.Add(new() { Id = originalId, FilePath = audio }); duplicate.Entries.Add(new() { Id = originalId, FilePath = audio });
    File.WriteAllText(malformed, JsonSerializer.Serialize(duplicate)); Throws<InvalidDataException>(() => store.Load(malformed), "Duplicate identifiers accepted.");
    Check(Directory.GetFiles(Path.Combine(state, "backups")).Length == 1, "Daily backup missing.");
    using (var recovered = new LibraryStore(state)) Check(recovered.Document.Entries.Count == 1, "Autosave recovery failed.");
    var prefs = new PreferenceStore(state); prefs.Settings.BlockVoice = true; prefs.Settings.ActionHotkeys["record"] = "Ctrl+Alt+F8"; prefs.Save();
    var loadedPrefs = new PreferenceStore(state); loadedPrefs.Load(); Check(loadedPrefs.Settings.BlockVoice && loadedPrefs.Settings.ActionHotkeys["record"] == "Ctrl+Alt+F8", "Preferences round trip failed.");
    Check(GlobalHotkeyService.NormalizeGesture("alt+control+f2") == "Ctrl+Alt+F2", "Hotkey canonicalization failed.");
    Check(GlobalHotkeyService.NormalizeGesture("Ctrl+Mouse4") == "Ctrl+MouseX1", "Mouse hotkey failed.");
    Check(GlobalHotkeyService.NormalizeGesture("Alt+123") == "Alt+123", "Numpad sequence failed.");
    Throws<ArgumentException>(() => GlobalHotkeyService.NormalizeGesture("Ctrl+Imaginary"), "Bad key accepted.");
    var capture=new HotkeyCaptureState("");
    Check(!capture.CanAccept&&capture.Display=="Press a key","An empty capture must ask for a key.");
    capture.Record(new(162,1,true));
    Check(!capture.CanAccept&&capture.Display=="Ctrl","A modifier alone was accepted as a sound hotkey.");
    capture.Record(new(75,1,true));capture.Record(new(75,1,false));capture.Record(new(162,0,false));
    Check(capture.Gesture=="Ctrl+K"&&capture.CanAccept,"Releasing a chord lost its recorded binding.");
    capture.Record(new(76,1,true,true));
    Check(capture.Gesture=="Ctrl+K","Keyboard repeat replaced a captured chord.");
    capture.Record(new(27,0,true));Check(capture.Gesture=="Escape","Escape could not be bound.");
    capture.Record(new(9,0,true));Check(capture.Gesture=="Tab","Tab could not be bound.");
    capture.Record(new(13,0,true));Check(capture.Gesture=="Enter","Enter could not be bound.");
    capture.Record(new(259,4,true));Check(capture.Gesture=="Alt+MouseX1","Mouse capture lost the modifier or button.");
    capture.Record(new(261,2,true));Check(capture.Gesture=="Shift+WheelUp","Wheel capture failed.");
    capture.Record(new(97,1,true));Check(capture.Gesture=="Ctrl+NumPad1","Numpad capture was confused with a top-row digit.");
    capture.Record(new(49,1,true));Check(capture.Gesture=="Ctrl+1","A top-row digit was confused with a numcode sequence.");
    capture.Record(new(186,1,true));Check(GlobalHotkeyService.NormalizeGesture(capture.Gesture)=="Ctrl+VK186","OEM key capture did not round-trip through the matcher.");
    capture.Remove();Check(capture.CanAccept&&capture.Removed&&capture.Gesture=="","Remove hotkey did not create an explicit empty assignment.");
    var rightCapture=new HotkeyCaptureState("",true);rightCapture.Record(new(75,16,true));
    Check(rightCapture.Gesture=="RCtrl+K","Capture lost right-hand modifier identity.");
    capture.Record(new(75,16,true));Check(capture.Gesture=="Ctrl+K","Default capture did not merge right-hand modifiers.");
    string homeList=Path.Combine(temp,"home.spl");
    File.WriteAllText(homeList,"<Soundlist><Sound url=\"clip.wav\"/><Sound url=\"second.wav\"/><Categories><Category name=\"My Sounds\" icon=\"stock_icon_cat_home\"><Sound id=\"0\"/></Category><Category name=\"Voice\"><Sound id=\"1\"/></Category></Categories></Soundlist>");
    using(var homes=new LibraryStore(Path.Combine(temp,"home-import"))){
        homes.ImportSoundpad(homeList);
        Check(homes.Document.RootCategory.Name=="My Sounds"&&homes.Document.Categories.Select(x=>x.Name).SequenceEqual(new[]{"Voice"})&&homes.Document.Entries[0].Category==""&&homes.Document.Entries[1].Category=="Voice","SPL home category was duplicated or ordinary category membership was lost.");
        homes.Document.Categories.Add(new(){Name="My Sounds"});homes.Document.Entries[0].Category="My Sounds";
        string legacyHome=Path.Combine(temp,"legacy-home.json");homes.Save(legacyHome);homes.Load(legacyHome);
        Check(homes.Document.Categories.Count==1&&homes.Document.Entries.Count==2&&homes.Document.Entries[0].Category=="","Previously imported home duplicates were not repaired while retaining sounds.");
    }
    string normalKey="Ctrl+F2",specialKey="Ctrl+F3",ownKey="Ctrl+F4";
    HotkeyAssignment[] assignments=[new("sound",()=>normalKey,value=>normalKey=value),new("special",()=>specialKey,value=>specialKey=value,true),new("own",()=>ownKey,value=>ownKey=value)];
    Check(HotkeyAssignments.Check(assignments,"own","RCtrl+F3",false).Blocked,"An equivalent special binding was not protected.");
    Check(!HotkeyAssignments.Check(assignments,"own","RCtrl+F3",true).Blocked,"Distinct right modifiers were treated as conflicts.");
    Check(HotkeyAssignments.Check(assignments,"own","Ctrl+F2",false).Overwrite&&normalKey=="Ctrl+F2","Checking an overwrite changed the original assignment before acceptance.");
    Check(HotkeyAssignments.Check(assignments,"own","Ctrl+F4",false)==default,"Retaining the current key conflicted with itself.");
    Throws<ArgumentException>(()=>HotkeyAssignments.ClearConflicts(assignments,"own","Ctrl+F3",false),"A protected special hotkey was overwritten.");
    HotkeyAssignments.ClearConflicts(assignments,"own","Ctrl+F2",false);
    Check(normalKey==""&&specialKey=="Ctrl+F3"&&ownKey=="Ctrl+F4","Confirmed reassignment cleared unrelated bindings.");
    using(var suspended=new GlobalHotkeyService()){
        suspended.Configure([new("F24","stop"),new("Ctrl+F24","hotkeysEnable")]);
        using(suspended.BeginCapture(_=>true,installHooks:false)){
            Check(!suspended.IsStarted&&suspended.ResolveAction("F24")==null&&suspended.ResolveAction("Ctrl+F24")==null,"Recording did not suspend ordinary and enable/toggle hotkeys without starting OS input hooks.");
            Throws<InvalidOperationException>(()=>suspended.BeginCapture(_=>true,false),"Nested captures could take ownership of the same input hooks.");
        }
        Check(!suspended.IsStarted&&suspended.ResolveAction("F24")=="stop"&&suspended.ResolveAction("Ctrl+F24")=="hotkeysEnable","Closing capture did not restore the original bindings and disabled hook state.");
    }
    Check(SpecialHotkeyActions.All.Count==46&&SpecialHotkeyActions.All.Take(44).Any(x=>x.Key=="playPreviouslyPlayed")&&SpecialHotkeyActions.All.Single(x=>x.Key=="jumpBack2").DefaultValue==30,"Recovered special hotkey list or default values differ from the reference constructor.");
    Check(SpecialHotkeyActions.All.TakeLast(2).Select(x=>x.Key).SequenceEqual(new[]{"forwardingToggle","forwardingPush"}),"Forwarding extension actions displaced the recovered special hotkeys.");
    using(var forwardKeys=new GlobalHotkeyService()){
        var events=new List<string>();forwardKeys.Triggered+=events.Add;
        HotkeyBinding[] definitions=[new("Ctrl+F24","forwardingPush",ReleaseAction:"forward-release",Repeatable:false),new("F23","forwardingToggle",Repeatable:false)];
        forwardKeys.Configure(definitions,new(){AllowRepeat=true});
        forwardKeys.ProcessKeyboardInput(new(135,1,true));forwardKeys.ProcessKeyboardInput(new(135,1,true,true));forwardKeys.ProcessKeyboardInput(new(135,1,false));
        Check(events.SequenceEqual(new[]{"forwardingPush","forward-release"}),"Push forwarding repeated or failed to release with the key.");
        events.Clear();forwardKeys.ProcessKeyboardInput(new(135,1,true));forwardKeys.ProcessKeyboardInput(new(162,0,false));forwardKeys.ProcessKeyboardInput(new(135,0,false));
        Check(events.SequenceEqual(new[]{"forwardingPush","forward-release"}),"Releasing a required modifier did not stop push forwarding exactly once.");
        events.Clear();forwardKeys.ProcessKeyboardInput(new(134,0,true));forwardKeys.ProcessKeyboardInput(new(134,0,true,true));forwardKeys.ProcessKeyboardInput(new(134,0,false));
        Check(events.SequenceEqual(new[]{"forwardingToggle"}),"Holding a toggle shortcut toggled repeatedly.");
        events.Clear();forwardKeys.ProcessKeyboardInput(new(135,1,true));forwardKeys.Configure(definitions,new(){Enabled=false});
        Check(events.SequenceEqual(new[]{"forwardingPush","forward-release"}),"Disabling or replacing bindings left push forwarding held.");
        forwardKeys.Configure([new("MouseX1","forwardingPush",ReleaseAction:"forward-release",Repeatable:false)]);events.Clear();
        forwardKeys.ProcessKeyboardInput(new(259,0,true));forwardKeys.ProcessKeyboardInput(new(259,0,false));
        Check(events.SequenceEqual(new[]{"forwardingPush","forward-release"}),"A mouse button could not own and release push forwarding.");
        Throws<ArgumentException>(()=>forwardKeys.Configure([new("WheelUp","forwardingPush",ReleaseAction:"forward-release")]),"A wheel impulse was accepted as a held forwarding key.");
    }
    var request=new ForwardingRequest(123,false,"fixture");int forwardingStarts=0,forwardingStops=0;
    var forwarding=new ForwardingController((_,_)=>{forwardingStarts++;return Task.CompletedTask;},()=>{forwardingStops++;return Task.CompletedTask;});
    await forwarding.ToggleAsync(request);await forwarding.HoldAsync("key",true);await forwarding.HoldAsync("key",false);
    Check(forwarding.Running&&forwarding.Latched&&forwardingStarts==1&&forwardingStops==0,"Releasing push forwarding incorrectly disabled a latched toggle.");
    await forwarding.ToggleAsync();Check(!forwarding.Running&&forwardingStops==1,"Toggle off did not end the active forwarding session.");
    await forwarding.HoldAsync("key",true,request);await forwarding.HoldAsync("mouse",true);await forwarding.HoldAsync("key",false);
    Check(forwarding.Running&&forwardingStarts==2,"Releasing one hold stopped another held forwarding control.");
    await forwarding.HoldAsync("mouse",false);Check(!forwarding.Running&&forwardingStops==2,"Releasing the last held control did not stop forwarding.");
    var entering=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayedForwarding=new ForwardingController(async(_,token)=>{entering.TrySetResult();await Task.Delay(Timeout.Infinite,token);},()=>Task.CompletedTask);
    var pendingForwarding=delayedForwarding.HoldAsync("key",true,request);await entering.Task;
    await delayedForwarding.HoldAsync("key",false);await pendingForwarding;
    Check(!delayedForwarding.Running&&!delayedForwarding.Starting&&!delayedForwarding.Desired,"Releasing during asynchronous startup left forwarding enabled.");
    var failedForwarding=new ForwardingController((_,_)=>throw new InvalidOperationException("fixture failure"),()=>Task.CompletedTask);
    try{await failedForwarding.ToggleAsync(request);Check(false,"A forwarding startup failure was hidden.");}catch(InvalidOperationException){}
    Check(!failedForwarding.Desired&&!failedForwarding.Running,"A failed forwarding startup retained toggle or hold state.");
    using(var matcher=new GlobalHotkeyService()){
        matcher.Configure([new("Ctrl+F24","stop"),new("RCtrl+F24","pause")],new(){AllowLeftRightModifiers=true});
        Check(matcher.ResolveAction("Ctrl+F24")=="stop"&&matcher.ResolveAction("RCtrl+F24")=="pause","Left and right hotkey modifiers were not kept distinct.");
        Throws<ArgumentException>(()=>matcher.Configure([new("Ctrl+F24","stop"),new("RCtrl+F24","pause")]),"Disabled right/left distinction did not reject equivalent chords.");
        matcher.Configure([new("Ctrl+Shift+1","play:first",true),new("Ctrl+Shift+123","play:last",true)]);
        Check(matcher.ResolveAction("Ctrl+Shift+1",true)=="play:first"&&matcher.ResolveAction("Ctrl+Shift+123",true)=="play:last"&&matcher.ResolveAction("Ctrl+Shift+1")==null,"Numpad sequences were confused with ordinary key chords.");
        matcher.Configure([new("F24","stop"),new("Ctrl+F24","hotkeysEnable")],new(){Enabled=false});
        Check(matcher.ResolveAction("F24")==null&&matcher.ResolveAction("Ctrl+F24")=="hotkeysEnable","Enable hotkeys must remain available while ordinary hotkeys are disabled.");
    }
    using(var priority=new GlobalHotkeyService()){
        HotkeyBinding soundBinding=new("F2","play:sound"),categoryBinding=new("F2","random-category:music"),
            hotbarBinding=new("F2","hotbar:0",Priority:HotkeyPriority.Hotbar),specialBinding=new("F2","play",Priority:HotkeyPriority.Special);
        foreach(var definitions in new[]{new[]{soundBinding,hotbarBinding},new[]{hotbarBinding,soundBinding},new[]{categoryBinding,hotbarBinding},new[]{hotbarBinding,categoryBinding},new[]{soundBinding,categoryBinding,hotbarBinding}}){
            priority.Configure(definitions);
            Check(priority.ResolveAction("F2")=="hotbar:0","Hotbar priority changed with import/registration order or conflicting category/sound bindings.");
        }
        foreach(var definitions in new[]{new[]{soundBinding,hotbarBinding,specialBinding},new[]{specialBinding,hotbarBinding,soundBinding},new[]{soundBinding,categoryBinding,hotbarBinding,specialBinding}}){
            priority.Configure(definitions);
            Check(priority.ResolveAction("F2")=="play"&&priority.Bindings.Single().Priority==HotkeyPriority.Special,"A special binding did not win over ordinary and hotbar assignments.");
        }
        priority.Configure([new("Ctrl+F2","play:sound"),new("RCtrl+F2","hotbar:0",Priority:HotkeyPriority.Hotbar)]);
        Check(priority.ResolveAction("Ctrl+F2")=="hotbar:0","Priorities were applied before merging equivalent left/right modifiers.");
        priority.Configure([new("Ctrl+F2","play:sound"),new("RCtrl+F2","hotbar:0",Priority:HotkeyPriority.Hotbar)],new(){AllowLeftRightModifiers=true});
        Check(priority.ResolveAction("Ctrl+F2")=="play:sound"&&priority.ResolveAction("RCtrl+F2")=="hotbar:0","Priorities merged intentionally distinct left/right bindings.");
        priority.Configure([new("Alt+123","play:sound",true),new("Alt+123","hotbar:0",true,HotkeyPriority.Hotbar)]);
        Check(priority.ResolveAction("Alt+123",true)=="hotbar:0","Numpad sequences did not use hotbar priority.");
        var actions=new List<string>();priority.Triggered+=actions.Add;
        priority.Configure([specialBinding],new(){SuppressMatchingKeys=true});
        Check(priority.ProcessKeyboardInput(new(113,0,true))&&actions.SequenceEqual(["play"])&&priority.BlocksLocalShortcut("F2"),"F2 did not dispatch globally and block local Rename.");
        Check(priority.ProcessKeyboardInput(new(113,0,true,true))&&actions.Count==1,"Held F2 either repeated playback despite disabled repeat, or leaked through to Rename.");
        Check(!priority.ProcessKeyboardInput(new(113,0,false))&&actions.Count==1,"Releasing F2 triggered playback a second time.");
        priority.Configure([specialBinding],new(){SuppressMatchingKeys=true,AllowRepeat=true});
        Check(priority.ProcessKeyboardInput(new(113,0,true,true))&&actions.Count==2,"Explicit hotkey repeat did not repeat the action.");
        priority.Configure([specialBinding],new(){SuppressMatchingKeys=false});
        Check(!priority.ProcessKeyboardInput(new(113,0,true))&&actions.Count==3&&!priority.BlocksLocalShortcut("F2"),"Pass hotkeys failed to both dispatch playback and allow foreground propagation.");
        priority.Configure([specialBinding,new("F3","hotkeysEnable",Priority:HotkeyPriority.Special),new("F4","hotkeysToggle",Priority:HotkeyPriority.Special)],new(){Enabled=false,SuppressMatchingKeys=true});
        Check(!priority.ProcessKeyboardInput(new(113,0,true))&&actions.Count==3&&!priority.BlocksLocalShortcut("F2"),"Disabled hotkeys dispatched playback or blocked the normal F2 shortcut.");
        Check(priority.ProcessKeyboardInput(new(114,0,true))&&actions.Last()=="hotkeysEnable","Enable hotkeys was lost while playback hotkeys were disabled.");
        Check(priority.ProcessKeyboardInput(new(115,0,true))&&actions.Last()=="hotkeysToggle","Toggle hotkeys was lost while playback hotkeys were disabled.");
        using(priority.BeginCapture(_=>true,false))Check(priority.ProcessKeyboardInput(new(113,0,true))&&actions.Count==5,"Recording a shortcut dispatched its global action.");
        priority.Configure([new("Ctrl+Alt+1","play:first",true)],new(){SuppressMatchingKeys=true});
        Check(priority.ProcessKeyboardInput(new(97,5,true))&&actions.Count==5,"Numpad sequence fired before releasing its modifier.");
        priority.ProcessKeyboardInput(new(164,1,false));
        Check(actions.Last()=="play:first","Numpad sequence stopped committing on modifier release.");
    }
    var context=new AutoKeyContext("game",true,new HashSet<string>(["game","chat"],StringComparer.OrdinalIgnoreCase));
    var heldModifiers=new HashSet<int>();
    using(var nativeInput=new GlobalHotkeyService(heldModifiers.Contains)){
        nativeInput.Configure([new("F2","play"),new("Ctrl+F2","pause")],new(){SuppressMatchingKeys=true});
        var nativeActions=new List<string>();nativeInput.Triggered+=nativeActions.Add;
        Check(nativeInput.ProcessKeyboardMessage(0x100,113,0,0)&&nativeActions.SequenceEqual(["play"]),"The native key-down adapter did not dispatch physical F2.");
        nativeInput.ProcessKeyboardMessage(0x101,113,0,0);
        Check(nativeInput.ProcessKeyboardMessage(0x104,113,16,0)&&nativeActions.Last()=="play"&&nativeActions.Count==2,"An externally remapped/injected F2 was discarded before hotkey matching.");
        nativeInput.ProcessKeyboardMessage(0x105,113,16,0);
        Check(!nativeInput.ProcessKeyboardMessage(0x100,113,16,AutoKeyService.InputTag)&&nativeActions.Count==2,"Auto Keys fed its own F2 back into playback.");
        nativeInput.ProcessKeyboardMessage(0x100,162,0,0); // Its release is swallowed outside this hook.
        Check(nativeInput.ProcessKeyboardMessage(0x100,113,0,0)&&nativeActions.Last()=="play","A stale tracked Ctrl modifier prevented an unmodified F2 after the physical modifier was released.");
        nativeInput.ProcessKeyboardMessage(0x101,113,0,0);
        heldModifiers.Add(162);
        Check(nativeInput.ProcessKeyboardMessage(0x100,113,0,0)&&nativeActions.Last()=="pause","Reconciling stale modifiers lost a genuinely held Ctrl key.");
        nativeInput.ProcessKeyboardMessage(0x101,113,0,0);heldModifiers.Clear();
        var recorded=new List<HotkeyInput>();using(nativeInput.BeginCapture(input=>{recorded.Add(input);return true;},false)){
            nativeInput.ProcessKeyboardMessage(0x100,162,0,0);
            nativeInput.ProcessKeyboardMessage(0x100,113,0,0);
            Check(recorded.Last().Modifiers==1&&recorded.Last().VirtualKey==113,"Capture lost a consumed Ctrl because asynchronous Windows state was not updated.");
            nativeInput.ProcessKeyboardMessage(0x101,162,0,0);
            nativeInput.ProcessKeyboardMessage(0x101,113,0,0);
        }
        // Call the exact native hook entry with its real x64 structure layout;
        // a handled binding returns before CallNextHookEx. No OS event is sent.
        nint keyboardData=System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
        try{
            for(int offset=0;offset<24;offset+=4)System.Runtime.InteropServices.Marshal.WriteInt32(keyboardData,offset,0);
            System.Runtime.InteropServices.Marshal.WriteInt32(keyboardData,0,113);
            System.Runtime.InteropServices.Marshal.WriteInt32(keyboardData,4,0x3c);
            System.Runtime.InteropServices.Marshal.WriteInt32(keyboardData,8,16);
            int callbackCount=nativeActions.Count;
            var hook=typeof(GlobalHotkeyService).GetMethod("Keyboard",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            nint result=(nint)hook.Invoke(nativeInput,[0,(nint)0x100,keyboardData])!;
            Check(result==1&&nativeActions.Count==callbackCount+1&&nativeActions.Last()=="play","The real marshalled hook callback did not deliver external F2 through to playback dispatch.");
        }finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(keyboardData);}
    }
    var rule=new AutoKeyEntry {Key="K",OnlyHotkey=true,Condition="Always"};
    Check(AutoKeyConditions.Matches(rule,true,true,context)&&!AutoKeyConditions.Matches(rule,false,true,context)&&!AutoKeyConditions.Matches(rule,true,false,context),"Auto Keys ignored microphone or hotkey origin conditions.");
    rule.Condition="Certain application is running";rule.Applications="chat.exe";Check(AutoKeyConditions.Matches(rule,true,true,context),"Auto Keys failed the running application condition.");
    rule.Condition="Certain application is in foreground";Check(!AutoKeyConditions.Matches(rule,true,true,context),"Background application was mistaken for foreground application.");
    rule.Applications="game.exe";Check(AutoKeyConditions.Matches(rule,true,true,context),"Auto Keys failed the matching foreground condition.");
    rule.Condition="Foreground application is fullscreen";Check(AutoKeyConditions.Matches(rule,true,true,context)&&!AutoKeyConditions.Matches(rule,true,true,context with {Fullscreen=false}),"Fullscreen Auto Keys condition was not enforced.");
    var zeroLimit=new AppPreferences {RecordingLimitSeconds=0};PreferenceStore.Validate(zeroLimit);checks++;
    var rich=new PreferenceStore(Path.Combine(temp,"rich"));rich.Settings.KaraokeDelayMs=-500;rich.Settings.RecordingFormat="MP3";rich.Settings.AutoKeys.Add(rule);rich.Settings.RowHotkeys[2]="RCtrl+F2";rich.Settings.Theme="Navy";rich.Settings.CursorUpdatesPerSecond=120;rich.Save();var reloaded=new PreferenceStore(Path.Combine(temp,"rich"));reloaded.Load();
    Check(reloaded.Settings.KaraokeDelayMs==-500&&reloaded.Settings.AutoKeys.Count==1&&reloaded.Settings.RowHotkeys[2]=="RCtrl+F2"&&reloaded.Settings.Theme=="Navy"&&reloaded.Settings.RecordingFormat=="MP3","Extended preferences were not retained across a reload.");
    using var hotkeys = new GlobalHotkeyService();
    Throws<ArgumentException>(() => hotkeys.Configure([new("Ctrl+F1", "stop"), new("Control+F1", "pause")]), "Conflict rejection failed.");
    hotkeys.Configure([new("Ctrl+F1", "stop"), new("Ctrl+F1", "pause")], new() { ConflictPolicy = HotkeyConflictPolicy.LastWins }); checks++;
    hotkeys.Start();Check(hotkeys.IsStarted,"Windows rejected low-level hook registration.");hotkeys.Stop();Check(!hotkeys.IsStarted,"Windows hooks were not released.");
    Check(AutoKeyService.ParseGesture("Ctrl+Alt+V").SequenceEqual([17, 18, 86]), "Push-to-talk parse failed.");
    Check(AutoKeyService.ParseGesture("Ctrl+Mouse4").SequenceEqual([17,259]),"Mouse push-to-talk parse failed.");
    Throws<ArgumentException>(() => AutoKeyService.ParseGesture("WheelUp"),"Wheel cannot be held for push-to-talk.");
    var keyEvents = new List<(int Key,bool Up)>();
    using(var simulated = new AutoKeyService(new(){Enabled=true,Gesture="Ctrl+V",LeadDelayMs=0,TailDelayMs=0},(key,up)=>keyEvents.Add((key,up)),_=>false,_=>true)) {
        Check(await simulated.BeginPlaybackAsync(),"Push-to-talk did not begin.");
        Check(keyEvents.SequenceEqual([(17,false),(86,false)]),"Push-to-talk key acquisition order is wrong.");
        simulated.Options.LeadDelayMs=10000;
        Check(await simulated.BeginPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(1)),"Retriggering held push-to-talk keys repeated the lead delay.");
        Check(keyEvents.SequenceEqual([(17,false),(86,false)]),"Retriggering push-to-talk released or pressed the held keys again.");
        await simulated.EndPlaybackAsync(); Check(keyEvents.SequenceEqual([(17,false),(86,false),(86,true),(17,true)]),"Push-to-talk release order is wrong.");
    }
    keyEvents.Clear();
    using(var held = new AutoKeyService(new(){Enabled=true,Gesture="Ctrl+V",LeadDelayMs=0,TailDelayMs=0},(key,up)=>keyEvents.Add((key,up)),key=>key==17,_=>true)) { await held.BeginPlaybackAsync(); await held.EndPlaybackAsync(); Check(keyEvents.SequenceEqual([(86,false),(86,true)]),"User-held modifier was released."); }
    keyEvents.Clear();
    using(var cancelled = new AutoKeyService(new(){Enabled=true,Gesture="V",LeadDelayMs=10000,TailDelayMs=0},(key,up)=>keyEvents.Add((key,up)),_=>false,_=>true)) {
        using var cancellation = new CancellationTokenSource(30); Check(!await cancelled.BeginPlaybackAsync(cancellation.Token),"Cancelled lead delay did not cancel."); Check(keyEvents.SequenceEqual([(86,false),(86,true)]),"Cancelled push-to-talk left a key held.");
    }
    keyEvents.Clear();
    using(var restricted = new AutoKeyService(new(){Enabled=true,Gesture="V",LeadDelayMs=0,TailDelayMs=0},(key,up)=>keyEvents.Add((key,up)),_=>false,_=>false)) Check(!await restricted.BeginPlaybackAsync() && keyEvents.Count==0,"Process restriction still emitted keys.");
    using var auto = new AutoKeyService(new() { Enabled = false, LeadDelayMs = 0, TailDelayMs = 0 });
    Check(await auto.BeginPlaybackAsync(), "Disabled auto-key playback failed."); await auto.EndPlaybackAsync();
    string pipe = "SoundboardLab-test-" + Guid.NewGuid().ToString("N"); using var remote = new RemoteControlServer(pipe);
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    remote.CommandReceived += json => received.TrySetResult(json); remote.Start();
    string response = await RemoteControlServer.SendAsync("{\"command\":\"play\",\"id\":\"abc\"}", pipe);
    Check(JsonDocument.Parse(response).RootElement.GetProperty("ok").GetBoolean(), "Remote acknowledgement failed.");
    Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).Contains("abc"), "Remote event failed.");
    using var duplicateRemote = new RemoteControlServer(pipe);
    Throws<IOException>(() => duplicateRemote.Start(), "Duplicate primary remote instance was accepted.");
    string secondResponse = await RemoteControlServer.SendAsync("{\"action\":\"stop\"}", pipe);
    Check(JsonDocument.Parse(secondResponse).RootElement.GetProperty("ok").GetBoolean(), "Remote listener failed on second command.");
    Check(changed > 5, "Changed notifications missing.");
    string spl = Path.Combine(temp, "fixture.spl");
    File.WriteAllText(spl, "<Soundlist><Sound url=\"clip.wav\" title=\"Sample\" duration=\"0:02\"/><Categories><Category name=\"Parent\"><Category name=\"Child\"><Sound id=\"0\"/></Category></Category></Categories><Hotbar><HotbarPage/></Hotbar></Soundlist>");
    store.ImportSoundpad(spl); Check(store.Document.Entries.Single().DurationSeconds == 2 && store.Document.Entries.Single().Category == "Parent/Child", "Verified SPL schema import failed.");
    Check(store.Document.SourceMetadataXml.Contains("Soundlist") && store.ImportWarnings.Count == 0, "SPL metadata preservation failed.");
    File.WriteAllText(spl,"<Soundlist><Sound url=\"clip.wav\"/><Hotbar><HotbarPage><HotbarItem id=\"2\" soundId=\"0\"/></HotbarPage><HotbarPage><HotbarItem id=\"5\" soundId=\"0\"/></HotbarPage></Hotbar></Soundlist>");
    store.ImportSoundpad(spl);Check(store.Document.HotbarPins.Count==2 && store.Document.HotbarPins[1].Page==1 && store.Document.HotbarPins[1].Slot==5,"Recovered SPL hotbar references did not import.");
    string? actualSpl = Environment.GetEnvironmentVariable("SB_SPL_FIXTURE");
    if (!string.IsNullOrWhiteSpace(actualSpl) && File.Exists(actualSpl)) { store.ImportSoundpad(actualSpl); Check(store.Document.Entries.Count > 0 && store.Document.Entries.Any(s => s.Category == "My Sounds"), "Real local Soundpad list import failed."); }
    var specialLimits=new AppPreferences();specialLimits.ActionValues["jumpBack1"]=3601;
    Throws<InvalidDataException>(()=>PreferenceStore.Validate(specialLimits),"Jump values beyond the original 3600-second limit were accepted.");
    specialLimits.ActionValues.Clear();specialLimits.ActionValues["volumeRaise1"]=101;
    Throws<InvalidDataException>(()=>PreferenceStore.Validate(specialLimits),"Volume increments above 100 percent were accepted.");
    specialLimits.ActionValues.Clear();specialLimits.ActionValues["volumeSet1"]=0;PreferenceStore.Validate(specialLimits);
    Check(specialLimits.ActionValues["volumeSet1"]==0,"Setting volume to zero must remain valid.");
    var legacyBar=new AppPreferences{HotbarRows=2,HotbarColumns=4,HotbarPages=8};PreferenceStore.UpgradeHotbarDefaults(legacyBar,[]);
    Check(legacyBar.HotbarColumns==6&&legacyBar.HotbarPages==1,"Legacy invented hotbar defaults were not corrected.");
    var pinnedBar=new AppPreferences{HotbarRows=2,HotbarColumns=4,HotbarPages=8};PreferenceStore.UpgradeHotbarDefaults(pinnedBar,[new HotbarPin{Page=3}]);
    Check(pinnedBar.HotbarColumns==6&&pinnedBar.HotbarPages==4,"Hotbar upgrade discarded a page containing a pin.");
    var customBar=new AppPreferences{HotbarRows=3,HotbarColumns=5,HotbarPages=2};PreferenceStore.UpgradeHotbarDefaults(customBar,[]);
    Check(customBar.HotbarRows==3&&customBar.HotbarColumns==5&&customBar.HotbarPages==2,"Hotbar upgrade overwrote a custom format.");
    Console.WriteLine($"PASS: {checks} service checks; temporary hook registration verified, no user keys injected.");
}
finally { Directory.Delete(temp, true); }
