using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.IO;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SoundboardLab.Models;

namespace SoundboardLab.Services;

public sealed class LibraryStore : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string stateDirectory;
    private readonly Stack<string> undo = new(), redo = new();
    private readonly HashSet<(string From,string To)> fileMoves=[];
    private readonly HashSet<INotifyPropertyChanged> subscribed = [];
    private string snapshot = "";
    private bool restoring;
    private bool updatingPlaybackStatistics;
    private bool disposed;
    private readonly object recoveryGate = new();
    private readonly Timer playbackRecoveryTimer;
    private string? pendingPlaybackRecovery;
    public LibraryDocument Document { get; } = new();
    public string? CurrentPath { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool HasUnsavedChanges { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<string> ImportWarnings { get; private set; } = [];
    public event EventHandler? Changed;
    public event EventHandler? PlaybackStatisticsChanged;
    public LibraryStore(string? stateDir = null)
    {
        stateDirectory = stateDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundboardLab");
        Directory.CreateDirectory(stateDirectory);
        playbackRecoveryTimer = new(_ => FlushPlaybackStatistics(), null, Timeout.Infinite, Timeout.Infinite);
        Document.Entries.CollectionChanged += CollectionChanged;
        Document.Categories.CollectionChanged += CollectionChanged;
        Document.HotbarPins.CollectionChanged += CollectionChanged;
        snapshot = Serialize();
        string recovery = Path.Combine(stateDirectory, "autosave.json");
        if (File.Exists(recovery))
            try { Restore(Parse(File.ReadAllText(recovery), stateDirectory)); HasUnsavedChanges = true; }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { LastError = ex.Message; }
    }
    public void New() { Checkpoint(); Restore(new()); CurrentPath = null; Commit(false); }
    public void Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (Path.GetExtension(full).Equals(".spl", StringComparison.OrdinalIgnoreCase)) { ImportSoundpad(full); return; }
        LibraryDocument next = Parse(File.ReadAllText(full), Path.GetDirectoryName(full)!);
        Checkpoint(); Restore(next); CurrentPath = full; HasUnsavedChanges = false; WriteRecovery(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Save(string? path = null)
    {
        string target = path ?? CurrentPath ?? Path.Combine(stateDirectory, "library.json");
        AtomicWrite(target, Serialize()); CurrentPath = Path.GetFullPath(target); HasUnsavedChanges = false; WriteRecovery(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public IReadOnlyList<SoundEntry> AddFiles(IEnumerable<string> paths, string category = "")
    {
        var added = new List<SoundEntry>();
        var existing = Document.Entries.Select(x => Path.GetFullPath(x.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in files) if (!File.Exists(file)) throw new FileNotFoundException("Sound file not found.", file);
        Batch(() => {
            foreach (string file in files) if (existing.Add(file))
            {
                var entry = new SoundEntry { FilePath = file, Title = Path.GetFileNameWithoutExtension(file), Category = category, AddedOn = DateTime.UtcNow };
                Document.Entries.Add(entry); added.Add(entry);
            }
            AddCategoryCore(category);
        });
        return added;
    }
    public void Remove(IEnumerable<SoundEntry> entries) => Batch(() => { foreach (var entry in entries.ToArray()) { Document.Entries.Remove(entry); foreach(var pin in Document.HotbarPins.Where(p=>p.SoundId==entry.Id).ToArray()) Document.HotbarPins.Remove(pin); } });
    public void Pin(SoundEntry entry,int page,int slot) => Batch(() => { if(page<0 || slot<0) throw new ArgumentOutOfRangeException(nameof(slot)); foreach(var old in Document.HotbarPins.Where(p=>p.Page==page && p.Slot==slot).ToArray()) Document.HotbarPins.Remove(old); Document.HotbarPins.Add(new(){SoundId=entry.Id,Page=page,Slot=slot,Color=entry.Color}); });
    public void Unpin(SoundEntry entry,int page) => Batch(() => { foreach(var pin in Document.HotbarPins.Where(p=>p.SoundId==entry.Id && p.Page==page).ToArray()) Document.HotbarPins.Remove(pin); });
    public void MovePin(HotbarPin pin,int page,int slot) => Batch(() => { if(page<0 || slot<0 || !Document.HotbarPins.Contains(pin)) throw new ArgumentException("Invalid hotbar position."); foreach(var old in Document.HotbarPins.Where(p=>p!=pin && p.Page==page && p.Slot==slot).ToArray()) Document.HotbarPins.Remove(old); pin.Page=page;pin.Slot=slot; });
    public void Rename(SoundEntry entry, string title) { if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A title is required."); entry.Title = title.Trim(); }
    public void RenameFile(SoundEntry entry,string destination)
    {
        string previous=Path.GetFullPath(entry.FilePath), next=Path.GetFullPath(destination);
        if(string.Equals(previous,next,StringComparison.Ordinal)) return;
        if(File.Exists(next)) throw new IOException("A file already exists at the new location.");
        File.Move(previous,next);
        fileMoves.Add((previous,next));fileMoves.Add((next,previous));
        Batch(()=>{foreach(var sound in Document.Entries.Where(s=>string.Equals(s.FilePath,previous,StringComparison.OrdinalIgnoreCase))) sound.FilePath=next;entry.Title=Path.GetFileNameWithoutExtension(next);});
    }
    public void AddCategory(string name) => Batch(() => AddCategoryCore(name));
    public void RenameCategory(string oldName,string newName)=>Batch(()=>RenameCategoryCore(oldName,newName));
    private void RenameCategoryCore(string oldName,string newName) {
        newName = newName.Trim().Trim('/'); if (newName.Length == 0) throw new ArgumentException("A category name is required.");
        if(newName==oldName) return;
        if(newName.StartsWith(oldName+"/",StringComparison.Ordinal)) throw new ArgumentException("A category cannot be moved into itself.");
        if(Document.Categories.Any(c=>c.Name==newName)) throw new ArgumentException("A category already exists at that location.");
        foreach (var c in Document.Categories.Where(c => c.Name == oldName || c.Name.StartsWith(oldName + "/", StringComparison.Ordinal)).ToArray()) {
            c.Name = newName + c.Name[oldName.Length..]; c.Parent = Parent(c.Name);
        }
        foreach (var e in Document.Entries.Where(e => e.Category == oldName || e.Category.StartsWith(oldName + "/", StringComparison.Ordinal))) e.Category = newName + e.Category[oldName.Length..];
    }
    public void UpdateCategory(string path,CategoryEntry settings)=>Batch(()=>{
        if(string.IsNullOrWhiteSpace(settings.Name))throw new ArgumentException("A category name is required.");
        var category=path=="*"?Document.RootCategory:Document.Categories.Single(x=>x.Name==path);
        if(path!="*"&&path!=settings.Name)RenameCategoryCore(path,settings.Name);
        category.Name=settings.Name;category.Hotkey=settings.Hotkey;category.IconMode=settings.IconMode;category.StockIcon=settings.StockIcon;category.IconPath=settings.IconPath;category.IconColor=settings.IconColor;
    });
    public void RemoveCategory(string name) => Batch(() => {
        foreach (var c in Document.Categories.Where(c => c.Name == name || c.Name.StartsWith(name + "/", StringComparison.Ordinal)).ToArray()) Document.Categories.Remove(c);
        foreach (var e in Document.Entries.Where(e => e.Category == name || e.Category.StartsWith(name + "/", StringComparison.Ordinal))) e.Category = "";
    });
    public void MarkPlayed(SoundEntry entry)
    {
        updatingPlaybackStatistics = true;
        try { Batch(() => { entry.PlayCount++; entry.LastPlayed = DateTime.UtcNow; }); }
        finally { updatingPlaybackStatistics = false; }
    }
    public bool Undo() { if (!undo.TryPop(out string? previous)) return false; try { var target=Parse(previous,stateDirectory);RestoreMovedFiles(target);redo.Push(Serialize());Restore(target);Commit(false);return true; } catch {undo.Push(previous);throw;} }
    public bool Redo() { if (!redo.TryPop(out string? next)) return false; try { var target=Parse(next,stateDirectory);RestoreMovedFiles(target);undo.Push(Serialize());Restore(target);Commit(false);return true; } catch {redo.Push(next);throw;} }
    private void RestoreMovedFiles(LibraryDocument target)
    {
        var moved=new List<(string From,string To)>();
        try { foreach(var pair in target.Entries.Select(e=>(Target:e,Current:Document.Entries.FirstOrDefault(c=>c.Id==e.Id))).Where(p=>p.Current is not null).Select(p=>(From:p.Current!.FilePath,To:p.Target.FilePath)).Distinct())
            if(pair.From!=pair.To && fileMoves.Contains(pair)) { File.Move(pair.From,pair.To);moved.Add(pair); }
        } catch { foreach(var pair in moved.AsEnumerable().Reverse()) File.Move(pair.To,pair.From);throw; }
    }
    public void Export(string folder)
    {
        folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
        string soundDir = Path.Combine(folder, "sounds"); Directory.CreateDirectory(soundDir);
        LibraryDocument portable = Parse(Serialize(), stateDirectory);
        foreach (var entry in portable.Entries)
        {
            if (!File.Exists(entry.FilePath)) throw new FileNotFoundException("Cannot export a missing sound.", entry.FilePath);
            string name = entry.Id.ToString("N") + Path.GetExtension(entry.FilePath);
            File.Copy(entry.FilePath, Path.Combine(soundDir, name), true); entry.FilePath = Path.Combine("sounds", name);
        }
        foreach(var category in portable.Categories.Prepend(portable.RootCategory).Where(c=>c.IconMode=="Custom"&&File.Exists(c.IconPath))){string icons=Path.Combine(folder,"icons");Directory.CreateDirectory(icons);string file=Guid.NewGuid().ToString("N")+Path.GetExtension(category.IconPath);File.Copy(category.IconPath,Path.Combine(icons,file),true);category.IconPath=Path.Combine("icons",file);}
        AtomicWrite(Path.Combine(folder, "library.json"), JsonSerializer.Serialize(portable, JsonOptions));
    }
    public void ImportSoundpad(string path)
    {
        path = Path.GetFullPath(path); var warnings = new List<string>();
        XDocument xml;
        try {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            xml = XDocument.Load(reader);
        } catch (XmlException ex) { throw new NotSupportedException("This file is not a supported XML Soundpad list. Compressed or unknown SPL formats are not supported; the current library was preserved.", ex); }
        if (xml.Root?.Name != "Soundlist") throw new NotSupportedException("The SPL file is not an XML Soundlist; the current library was preserved.");
        var next = new LibraryDocument { SourceMetadataXml = xml.ToString() };
        var sounds = xml.Root.Elements("Sound").ToArray();
        string directory = Path.GetDirectoryName(path)!;
        foreach (XElement sound in sounds)
        {
            string url = (string?)sound.Attribute("url") ?? throw new InvalidDataException("An SPL sound has no url attribute.");
            if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("An SPL sound has an empty url attribute.");
            string file = url;
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) file = new Uri(url).LocalPath;
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile) throw new NotSupportedException("Remote audio URLs cannot be imported as local sound files.");
            file = Path.GetFullPath(file, directory);
            string duration = (string?)sound.Attribute("duration") ?? "";
            double seconds = 0;
            if (duration.Length > 0) {
                string[] components = duration.Split(':');
                if (components.Length is 2 or 3 && components.All(c => double.TryParse(c, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
                    foreach (string component in components) seconds = seconds * 60 + double.Parse(component, CultureInfo.InvariantCulture);
                else warnings.Add($"Duration '{duration}' on '{Path.GetFileName(file)}' was not recognized.");
            }
            var entry = new SoundEntry { FilePath = file, Title = (string?)sound.Attribute("title") ?? Path.GetFileNameWithoutExtension(file), DurationSeconds = seconds, Tags = (string?)sound.Attribute("artist") ?? "", Artist=(string?)sound.Attribute("artist")??"",MetadataTitle=(string?)sound.Attribute("title")??"", Order = next.Entries.Count };
            if (!File.Exists(file)) warnings.Add($"Missing sound: {file}");
            if (sound.Attributes().Any(a => a.Name.LocalName.Contains("hotkey", StringComparison.OrdinalIgnoreCase)) || sound.Elements().Any(e => e.Name.LocalName.Contains("hotkey", StringComparison.OrdinalIgnoreCase))) warnings.Add($"Hotkey metadata for '{entry.Title}' is retained in source XML but requires a verified decoder before activation.");
            next.Entries.Add(entry);
        }
        void Category(XElement node, string parent)
        {
            string name = (string?)node.Attribute("name") ?? "";
            if (name.Length == 0) { foreach (var child in node.Elements("Category")) Category(child, parent); return; }
            string full = parent.Length == 0 ? name : parent + "/" + name;
            if (!next.Categories.Any(c => c.Name == full)) next.Categories.Add(new() { Name = full, Parent = parent });
            foreach (var reference in node.Elements("Sound")) {
                if (!int.TryParse((string?)reference.Attribute("id"), out int index) || index < 0 || index >= next.Entries.Count) throw new InvalidDataException($"Category '{full}' references an invalid sound index.");
                if (next.Entries[index].Category.Length == 0) next.Entries[index].Category = full;
                else if (next.Entries[index].Category != full) warnings.Add($"'{next.Entries[index].Title}' belongs to multiple Soundpad categories; '{next.Entries[index].Category}' is used for filtering and all memberships remain in source XML.");
            }
            foreach (var child in node.Elements("Category")) Category(child, full);
        }
        foreach (var category in xml.Root.Element("Categories")?.Elements("Category") ?? []) Category(category, "");
        int hotbarPage=0;
        foreach(var page in xml.Root.Element("Hotbar")?.Elements("HotbarPage") ?? []) {
            foreach(var item in page.Elements("HotbarItem")) {
                if(!int.TryParse((string?)item.Attribute("id"),out int slot) || slot<0 || !int.TryParse((string?)item.Attribute("soundId"),out int soundId) || soundId<0 || soundId>=next.Entries.Count) throw new InvalidDataException("Hotbar item references an invalid sound or position.");
                if(next.HotbarPins.Any(p=>p.Page==hotbarPage && p.Slot==slot)) throw new InvalidDataException("Hotbar page contains duplicate slot identifiers.");
                next.HotbarPins.Add(new(){SoundId=next.Entries[soundId].Id,Page=hotbarPage,Slot=slot,Color=next.Entries[soundId].Color});
                if(item.Attributes().Any(a=>a.Name.LocalName is not "id" and not "soundId")) warnings.Add("Additional hotbar item attributes were retained in the original XML.");
            }
            hotbarPage++;
        }
        string statsPath = Path.Combine(directory, "stats.spl");
        if (File.Exists(statsPath)) {
            try {
                using var statsReader = XmlReader.Create(statsPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                var stats = XDocument.Load(statsReader);
                if (stats.Root?.Name == "Stats") {
                    var byHash = stats.Root.Elements("Sound").Where(s => s.Attribute("hash") is not null).GroupBy(s => (string)s.Attribute("hash")!).ToDictionary(g => g.Key, g => g.First());
                    for (int i = 0; i < sounds.Length; i++) if ((string?)sounds[i].Attribute("hash") is string hash && byHash.TryGetValue(hash, out var stat)) {
                        if (int.TryParse((string?)stat.Attribute("playCount"), out int count)) next.Entries[i].PlayCount = count;
                        if (DateTime.TryParseExact((string?)stat.Attribute("lastPlayedOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)) next.Entries[i].LastPlayed = last;
                    }
                }
            } catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { warnings.Add("Adjacent Soundpad playback statistics could not be imported: " + ex.Message); }
        }
        Checkpoint(); Restore(next); CurrentPath = null; ImportWarnings = warnings; Commit(false);
    }
    private void AddCategoryCore(string name)
    {
        name = name.Trim().Trim('/'); if (name.Length == 0) return;
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 1; i <= parts.Length; i++) { string part = string.Join('/', parts.Take(i)); if (!Document.Categories.Any(c => c.Name == part)) Document.Categories.Add(new() { Name = part, Parent = Parent(part) }); }
    }
    private static string Parent(string value) => value.LastIndexOf('/') is var index && index >= 0 ? value[..index] : "";
    private void Batch(Action action)
    {
        string prior = Serialize(); restoring = true;
        try { action(); } catch { Restore(Parse(prior, stateDirectory)); throw; }
        finally { restoring = false; SubscribeItems(); }
        if (prior != Serialize()) { undo.Push(prior); redo.Clear(); Commit(false); }
    }
    private void Checkpoint() { undo.Push(Serialize()); redo.Clear(); }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { SubscribeItems(); if (!restoring) Commit(true); }
    private void PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (!restoring) Commit(true); }
    private void SubscribeItems()
    {
        var current = Document.Entries.Cast<INotifyPropertyChanged>().Concat(Document.Categories).Concat(Document.HotbarPins).Append(Document.RootCategory).ToHashSet();
        foreach (var removed in subscribed.Except(current).ToArray()) { removed.PropertyChanged -= PropertyChanged; subscribed.Remove(removed); }
        foreach (var added in current.Except(subscribed)) { added.PropertyChanged += PropertyChanged; subscribed.Add(added); }
    }
    private void Commit(bool checkpoint)
    {
        if (checkpoint) { undo.Push(snapshot); redo.Clear(); }
        snapshot = Serialize(); HasUnsavedChanges = true;
        if(updatingPlaybackStatistics){
            // Capture on the model's owning thread; only the immutable JSON is
            // written in the background, once the burst of playback has settled.
            Interlocked.Exchange(ref pendingPlaybackRecovery,snapshot);
            playbackRecoveryTimer.Change(250,Timeout.Infinite);
            PlaybackStatisticsChanged?.Invoke(this,EventArgs.Empty);
        }
        else { WriteRecovery(); Changed?.Invoke(this, EventArgs.Empty); }
    }
    private void Restore(LibraryDocument next)
    {
        bool wasRestoring = restoring; restoring = true;
        try { Document.Entries.Clear(); foreach (var entry in next.Entries) Document.Entries.Add(entry); Document.Categories.Clear(); foreach (var category in next.Categories) Document.Categories.Add(category); Document.HotbarPins.Clear(); foreach(var pin in next.HotbarPins) Document.HotbarPins.Add(pin); Document.RootCategory=next.RootCategory; Document.Version = next.Version; Document.SourceMetadataXml = next.SourceMetadataXml; }
        finally { restoring = wasRestoring; SubscribeItems(); snapshot = Serialize(); }
    }
    private string Serialize() => JsonSerializer.Serialize(Document, JsonOptions);
    private static LibraryDocument Parse(string json, string directory)
    {
        var document = JsonSerializer.Deserialize<LibraryDocument>(json, JsonOptions) ?? throw new InvalidDataException("Library is empty.");
        if (document.Version != 1) throw new InvalidDataException($"Unsupported library version {document.Version}.");
        if (document.Entries is null || document.Categories is null) throw new InvalidDataException("Library collections are missing.");
        var ids = new HashSet<Guid>();
        foreach (var entry in document.Entries) {
            if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath)) throw new InvalidDataException("A sound entry has no file path.");
            if (entry.Id == Guid.Empty || !ids.Add(entry.Id)) throw new InvalidDataException("Library contains invalid or duplicate sound identifiers.");
            entry.FilePath = Path.GetFullPath(entry.FilePath, directory);
        }
        document.RootCategory ??=new(){Name="My Sounds",StockIcon="Home"};
        if (document.Categories.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name))) throw new InvalidDataException("Library contains an invalid category.");
        foreach(var category in document.Categories.Prepend(document.RootCategory))if(!string.IsNullOrWhiteSpace(category.IconPath))category.IconPath=Path.GetFullPath(category.IconPath,directory);
        document.HotbarPins ??= [];
        if(document.HotbarPins.Count==0) foreach(var entry in document.Entries.Where(e=>e.HotbarSlot>=0)) document.HotbarPins.Add(new(){SoundId=entry.Id,Page=entry.HotbarPage,Slot=entry.HotbarSlot,Color=entry.Color});
        foreach(var entry in document.Entries) entry.HotbarSlot=-1;
        var positions=new HashSet<(int,int)>(); var pinIds=new HashSet<Guid>();
        foreach(var pin in document.HotbarPins) if(pin is null || pin.Id==Guid.Empty || !pinIds.Add(pin.Id) || !ids.Contains(pin.SoundId) || pin.Page<0 || pin.Slot<0 || !positions.Add((pin.Page,pin.Slot))) throw new InvalidDataException("Library contains an invalid hotbar pin.");
        return document;
    }
    private void WriteRecovery()
    {
        string content=Serialize();playbackRecoveryTimer.Change(Timeout.Infinite,Timeout.Infinite);
        lock(recoveryGate){Interlocked.Exchange(ref pendingPlaybackRecovery,null);WriteRecoveryContent(content);}
    }
    public void FlushPlaybackStatistics()
    {
        lock(recoveryGate){
            var content=Interlocked.Exchange(ref pendingPlaybackRecovery,null);
            if(content is not null&&!WriteRecoveryContent(content))Interlocked.CompareExchange(ref pendingPlaybackRecovery,content,null);
        }
    }
    private bool WriteRecoveryContent(string content)
    {
        try {
            AtomicWrite(Path.Combine(stateDirectory, "autosave.json"), content);
            string backups = Path.Combine(stateDirectory, "backups"); Directory.CreateDirectory(backups);
            string backup = Path.Combine(backups, $"library-{DateTime.Now:yyyy-MM-dd}.json");
            if (!File.Exists(backup)) AtomicWrite(backup, content);
            foreach (string obsolete in Directory.EnumerateFiles(backups, "library-*.json").OrderDescending().Skip(30)) File.Delete(obsolete);
            LastError = null;
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; return false; }
    }
    internal static void AtomicWrite(string path, string content)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { if(disposed)return;disposed=true;playbackRecoveryTimer.Change(Timeout.Infinite,Timeout.Infinite);FlushPlaybackStatistics();playbackRecoveryTimer.Dispose();Document.Entries.CollectionChanged -= CollectionChanged; Document.Categories.CollectionChanged -= CollectionChanged; Document.HotbarPins.CollectionChanged -= CollectionChanged; foreach (var item in subscribed) item.PropertyChanged -= PropertyChanged; subscribed.Clear(); }
}
