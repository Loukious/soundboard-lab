using System.Text.Json;
using System.Text.Json.Nodes;
using SoundboardLab.Services;

internal static class RecoveryStateTests
{
    internal static void Run(string root,string audio,Action<bool,string> check)
    {
        string state=Path.Combine(root,"save-state"),saved=Path.Combine(root,"Named sound list.json");
        using(var store=new LibraryStore(state)){
            var sound=store.AddFiles([audio]).Single();store.Save(saved);store.MarkPlayed(sound);store.FlushPlaybackStatistics();
            check(!store.HasUnsavedChanges,"Statistics dirtied a saved library before restart.");
            check(!JsonNode.Parse(File.ReadAllText(saved))!.AsObject().ContainsKey("_Recovery"),"Recovery metadata leaked into the user's saved library.");
        }
        using(var restarted=new LibraryStore(state)){
            check(!restarted.HasUnsavedChanges&&restarted.CurrentPath==saved,"Save, exit and restart lost the clean state or saved filename.");
            check(restarted.Document.Entries.Single().PlayCount==1,"Restoring a clean saved library discarded later playback statistics.");
            restarted.Rename(restarted.Document.Entries.Single(),"Unsaved rename");
        }
        using(var restarted=new LibraryStore(state)){
            check(restarted.HasUnsavedChanges&&restarted.CurrentPath==saved&&restarted.Document.Entries.Single().Title=="Unsaved rename","Restart lost an unsaved edit, its dirty flag, or its save target.");
            restarted.Save();
            check(JsonNode.Parse(File.ReadAllText(saved))!["Entries"]![0]!["Title"]!.GetValue<string>()=="Unsaved rename","Save after restart wrote a different library file.");
        }
        using(var restarted=new LibraryStore(state))check(!restarted.HasUnsavedChanges,"Saving recovered edits did not persist the clean state.");
        var external=JsonNode.Parse(File.ReadAllText(saved))!;external["Entries"]![0]!["Title"]="External edit";File.WriteAllText(saved,external.ToJsonString());
        using(var restarted=new LibraryStore(state))check(restarted.HasUnsavedChanges&&restarted.Document.Entries.Single().Title=="Unsaved rename","An externally changed save file was silently treated as matching recovery.");
        File.Delete(saved);
        using(var restarted=new LibraryStore(state))check(restarted.HasUnsavedChanges&&restarted.CurrentPath==saved&&restarted.Document.Entries.Count==1,"A missing save file cleared the dirty flag or lost recovered sounds.");

        string legacy=Path.Combine(root,"legacy-save-state"),recovery=Path.Combine(legacy,"autosave.json");
        using(var store=new LibraryStore(legacy)){var sound=store.AddFiles([audio]).Single();store.Save();store.MarkPlayed(sound);store.FlushPlaybackStatistics();}
        var document=JsonNode.Parse(File.ReadAllText(recovery))!.AsObject();document.Remove("_Recovery");File.WriteAllText(recovery,document.ToJsonString());
        using(var restarted=new LibraryStore(legacy)){
            check(!restarted.HasUnsavedChanges&&restarted.CurrentPath==Path.Combine(legacy,"library.json"),"Legacy recovery could not recognize its matching default save.");
            check(restarted.Document.Entries.Single().PlayCount==1,"Legacy migration discarded recovered playback statistics.");
        }
        document["Entries"]![0]!["Title"]="Legacy unsaved edit";File.WriteAllText(recovery,document.ToJsonString());
        using(var restarted=new LibraryStore(legacy))check(restarted.HasUnsavedChanges&&restarted.Document.Entries.Single().Title=="Legacy unsaved edit","Legacy migration hid a real unsaved change.");
    }
}
