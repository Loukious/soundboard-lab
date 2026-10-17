using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SoundboardLab.Models;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name)=>PropertyChanged?.Invoke(this,new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new(name)); return true;
    }
}

public sealed class SoundEntry : ObservableModel
{
    private Guid id = Guid.NewGuid();
    private string filePath = "", title = "", category = "", hotkey = "", tags = "", color = "#334155";
    private int playCount, hotbarPage, hotbarSlot = -1, order;
    private DateTime? lastPlayed;
    private double durationSeconds;
    private bool isMissing;
    public string SourceUrl { get; set; } = "";
    public string Artist { get; set; } = "";
    public string MetadataTitle { get; set; } = "";
    public DateTime? AddedOn { get; set; }
    public Guid Id { get => id; set => Set(ref id, value); }
    public string FilePath { get => filePath; set { if(Set(ref filePath, value ?? ""))IsMissing=false; } }
    [JsonIgnore]
    public bool IsMissing { get => isMissing; set => Set(ref isMissing, value); }
    public string Title { get => title; set => Set(ref title, value ?? ""); }
    public string Category { get => category; set => Set(ref category, value ?? ""); }
    public string Hotkey { get => hotkey; set => Set(ref hotkey, value ?? ""); }
    public string Tags { get => tags; set => Set(ref tags, value ?? ""); }
    public string Color { get => color; set => Set(ref color, value ?? "#334155"); }
    public int PlayCount { get => playCount; set => Set(ref playCount, Math.Max(0, value)); }
    public DateTime? LastPlayed { get => lastPlayed; set => Set(ref lastPlayed, value); }
    public double DurationSeconds { get => durationSeconds; set => Set(ref durationSeconds, double.IsFinite(value) ? Math.Max(0, value) : 0); }
    public int HotbarPage { get => hotbarPage; set => Set(ref hotbarPage, Math.Max(0, value)); }
    public int HotbarSlot { get => hotbarSlot; set => Set(ref hotbarSlot, value); }
    public int Order { get => order; set => Set(ref order, value); }
}

public sealed class CategoryEntry : ObservableModel
{
    private string name = "", parent = "", hotkey = "",iconMode="Generated",stockIcon="Folder",iconPath="",iconColor="";
    public string Name { get => name; set => Set(ref name, value ?? ""); }
    public string Parent { get => parent; set => Set(ref parent, value ?? ""); }
    public string Hotkey { get => hotkey; set => Set(ref hotkey, value ?? ""); }
    public string IconMode {get=>iconMode;set=>Set(ref iconMode,value??"Generated");}
    public bool IconModeExplicit {get;set;}
    public string StockIcon {get=>stockIcon;set=>Set(ref stockIcon,value??"Folder");}
    public string IconPath {get=>iconPath;set=>Set(ref iconPath,value??"");}
    public string IconColor {get=>iconColor;set=>Set(ref iconColor,value??"");}
    public override string ToString() => Name;
}

public sealed class LibraryDocument
{
    public int Version { get; set; } = 1;
    public CategoryEntry RootCategory {get;set;}=new(){Name="My Sounds",IconMode="Stock",StockIcon="Home"};
    public ObservableCollection<SoundEntry> Entries { get; set; } = [];
    public ObservableCollection<CategoryEntry> Categories { get; set; } = [];
    public ObservableCollection<HotbarPin> HotbarPins { get; set; } = [];
    public string SourceMetadataXml { get; set; } = "";
}

public sealed class HotbarPin : ObservableModel
{
    private Guid id=Guid.NewGuid(), soundId;
    private int page, slot;
    private string color="#334155";
    public Guid Id { get=>id; set=>Set(ref id,value); }
    public Guid SoundId { get=>soundId; set=>Set(ref soundId,value); }
    public int Page { get=>page; set=>Set(ref page,value); }
    public int Slot { get=>slot; set=>Set(ref slot,value); }
    public string Color { get=>color; set=>Set(ref color,value); }
}

public sealed class AppPreferences
{
    public List<string> SoundColorSlots { get; set; } = SoundColorPalette.CreateDefaultSlots();
    public string CaptureDeviceId { get; set; } = "";
    public string RenderDeviceId { get; set; } = "";
    public string ForwardingSource {get;set;}="Foreground app";
    public double ForwardingVolume {get;set;}=1;
    public bool ForwardingMuteVoice {get;set;}
    public double SpeakerVolume { get; set; } = .8;
    public double ClipVolume { get; set; } = 1;
    public string AutoKeyGesture { get; set; } = "";
    public string AutoKeyProcesses { get; set; } = "";
    public int AutoKeyDelayMs { get; set; } = 50;
    public int RecordingLimitSeconds { get; set; } = 300;
    public double TargetLufs { get; set; } = -23;
    public string DefaultPlaybackMode { get; set; } = "Both";
    public bool ContinuePlayback { get; set; }
    public List<string> RecentLists { get; set; } = [];
    public bool HotkeysEnabled { get; set; } = true;
    public bool PassHotkeys { get; set; }
    public bool RememberHotkeyStatus { get; set; } = true;
    public bool AllowHotkeyRepeat { get; set; }
    public bool AutoStopOnKeyRelease { get; set; }
    public string HotkeyConflictPolicy { get; set; } = "Reject";
    public Dictionary<string, string> ActionHotkeys { get; set; } = new();
    public Dictionary<int, string> HotbarHotkeys { get; set; } = new();
    public bool SelectHotbarSound { get; set; } = true;
    public bool ShowHotbar { get; set; }
    public int HotbarRows { get; set; } = 2;
    public int HotbarColumns { get; set; } = 6;
    public int HotbarPages { get; set; } = 1;
    public int HotbarDefaultsVersion { get; set; }
    public bool HotbarLocked { get; set; }
    public bool SameHotbarColors { get; set; } = true;
    public bool AutoKeysEnabled { get; set; }
    public string PushToTalkKeys { get; set; } = "";
    public string PushToTalkProcesses { get; set; } = "";
    public int PushToTalkLeadMs { get; set; } = 50;
    public int PushToTalkTailMs { get; set; } = 100;
    public double Volume { get; set; } = 1;
    public double MicrophoneGain { get; set; } = 1;
    public bool BlockVoice { get; set; }
    public bool Repeat { get; set; }
    public bool RepeatList { get; set; }
    public string PlaybackMode { get; set; } = "Speakers";
    public string OutputDeviceId { get; set; } = "";
    public string MicrophoneDeviceId { get; set; } = "";
    public bool Normalize { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool RemoteEnabled { get; set; } = true;
    public bool StopOnNewSound { get; set; } = true;
    public string RecordingFolder { get; set; } = "";
    public string TtsVoice { get; set; } = "";
    public int TtsRate { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool VoiceActivationEnabled { get; set; }
    public bool ReduceOtherApplications { get; set; }
    public string OtherApplicationsVolume { get; set; } = "Reduce the volume of other sounds by 80%";
    public double FixedAdjustmentDb { get; set; }
    public bool AllowLeftRightModifiers { get; set; } = true;
    public bool SameHotkeysInAllCategories { get; set; }
    public string NumcodeModifier { get; set; } = "Alt";
    public string IndexModifier { get; set; } = "Ctrl+Alt";
    public bool NumcodeCategoryIndex { get; set; }
    public bool NumcodeTwoDigits { get; set; }
    public Dictionary<int,string> RowHotkeys { get; set; } = new();
    public Dictionary<string,double> ActionValues { get; set; } = new();
    public List<AutoKeyEntry> AutoKeys { get; set; } = [];
    public string Language { get; set; } = "System default";
    public string DateFormat { get; set; } = "Language default";
    public string Zoom { get; set; } = "100%";
    public string ToolbarSize { get; set; } = "Small";
    public string ToolbarColor { get; set; } = "Blue";
    public bool ShowMicrophoneLevel { get; set; }
    public bool LinearVolumeSlider { get; set; } = true;
    public bool AllowResumeInAnotherMode { get; set; }
    public string ModeSwitch { get; set; } = "default and speakers";
    public bool RunOnStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    public bool MinimizeInsteadOfClose { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool DisableSorting { get; set; }
    public bool AlternatingRowColors { get; set; }
    public bool TypingBeginsSearch { get; set; } = true;
    public bool TypingPrefersTts { get; set; }
    public bool RememberPlaybackState { get; set; } = true;
    public Guid? LastSoundId { get; set; }
    public double LastSoundPosition { get; set; }
    public bool LastSoundPaused { get; set; }
    public bool CheckDevicesOnStartup { get; set; } = true;
    public List<string> CaptureDeviceIds { get; set; } = [];
    public Dictionary<string,double> DeviceBoosts { get; set; } = new();
    public string RecordingFormat { get; set; } = "M4A";
    public string RecordingBitrate { get; set; } = "320 Kbps";
    public string RecordingCategory { get; set; } = "Selected category";
    public string RecordingInsertPosition { get; set; } = "End";
    public bool NormalizeRecordings { get; set; } = true;
    public double RecordingTargetDb { get; set; } = 89;
    public bool TrimRecordingSilence { get; set; } = true;
    public bool NotifyFirstSound { get; set; } = true;
    public int PlaybackDelayMs { get; set; }
    public string VoiceActivationPath { get; set; } = "";
    public int KaraokeDelayMs { get; set; }
    public int CursorUpdatesPerSecond { get; set; } = 60;
    public bool AltNumcodeHotkeys { get; set; }
    public bool IndexHotkeys { get; set; }
    public bool UseExternalEditor { get; set; }
    public string ExternalEditorPath { get; set; } = "";
    public string ExternalEditorArguments { get; set; } = "{file}";
    public Dictionary<string, NotificationPreference> Notifications { get; set; } = new();
}

public sealed class NotificationPreference
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "";
}

public sealed class AutoKeyEntry
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "Press and hold";
    public bool Enabled { get; set; } = true;
    public bool OnlyHotkey { get; set; } = true;
    public string Condition { get; set; } = "Always";
    public string Applications { get; set; } = "";
}
