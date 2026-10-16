using System.Text.Json;
using System.IO;
using SoundboardLab.Models;

namespace SoundboardLab.Services;

public sealed class PreferenceStore
{
    private readonly string path;
    public AppPreferences Settings { get; private set; } = new();
    public PreferenceStore(string? stateDir = null) => path = Path.Combine(stateDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundboardLab"), "preferences.json");
    public void Load() { if (File.Exists(path)) Import(path); }
    public void Save() => Export(path);
    public void Import(string file)
    {
        var next = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(file), LibraryStore.JsonOptions) ?? throw new InvalidDataException("Preferences are empty.");
        Validate(next);
        Settings=next;
    }
    public static void UpgradeHotbarDefaults(AppPreferences settings,IEnumerable<HotbarPin> pins)
    {
        if(settings.HotbarDefaultsVersion>=1)return;
        if(settings.HotbarRows==2&&settings.HotbarColumns==4&&settings.HotbarPages==8){settings.HotbarColumns=6;settings.HotbarPages=Math.Max(1,pins.Select(x=>x.Page+1).DefaultIfEmpty(1).Max());}
        settings.HotbarDefaultsVersion=1;
    }
    public static void Validate(AppPreferences next)
    {
        if(next.ForwardingSource is not ("Foreground app" or "Whole PC")||!double.IsFinite(next.ForwardingVolume)||next.ForwardingVolume is <0 or >2)throw new InvalidDataException("Invalid audio forwarding source or volume.");
        if (!double.IsFinite(next.Volume) || next.Volume < 0 || next.Volume > 4 || !double.IsFinite(next.MicrophoneGain) || next.MicrophoneGain < 0 || next.MicrophoneGain > 8) throw new InvalidDataException("Invalid audio gain in preferences.");
        if (next.AutoKeyDelayMs < 0 || next.AutoKeyDelayMs > 10000 || next.RecordingLimitSeconds < 0 || next.RecordingLimitSeconds > 86400) throw new InvalidDataException("Invalid timing limit in preferences.");
        if (next.PushToTalkLeadMs < 0 || next.PushToTalkLeadMs > 10000 || next.PushToTalkTailMs < 0 || next.PushToTalkTailMs > 10000) throw new InvalidDataException("Push-to-talk delays must be between 0 and 10000 ms.");
        if (!double.IsFinite(next.SpeakerVolume) || next.SpeakerVolume is < 0 or > 1 || !double.IsFinite(next.ClipVolume) || next.ClipVolume is < 0 or > 2 || !double.IsFinite(next.TargetLufs) || next.TargetLufs is < -70 or > 0 || next.MicrophoneGain > 4) throw new InvalidDataException("Invalid playback volume or loudness target.");
        next.Notifications ??= new();next.AutoKeys??=[];next.CaptureDeviceIds??=[];next.DeviceBoosts??=[];next.RowHotkeys??=[];next.ActionValues??=[];
        if(!double.IsFinite(next.FixedAdjustmentDb)||next.FixedAdjustmentDb is <-30 or >30||next.KaraokeDelayMs is <-10000 or >10000||next.DeviceBoosts.Values.Any(x=>!double.IsFinite(x)||x is <-30 or >30))throw new InvalidDataException("Invalid sound adjustment or device boost.");
        if(next.RecordingFormat is not ("M4A" or "MP3" or "WAV")||!int.TryParse(next.RecordingBitrate.Split(' ')[0],out int bitrate)||bitrate is <96 or >320)throw new InvalidDataException("Invalid recording format or bitrate.");
        if(next.PlaybackDelayMs is <0 or >10000 || next.CursorUpdatesPerSecond is <15 or >144)throw new InvalidDataException("Invalid playback delay or cursor update rate.");
        next.ActionHotkeys ??= new(); next.HotbarHotkeys ??= new(); next.RecentLists ??= [];
        if(next.HotbarRows is < 1 or > 12 || next.HotbarColumns is < 1 or > 12 || next.HotbarPages is < 1 or > 128) throw new InvalidDataException("Invalid hotbar dimensions.");
        if(!double.IsFinite(next.RecordingTargetDb)||next.RecordingTargetDb is <42 or >112||next.ActionValues.Values.Any(x=>!double.IsFinite(x)||x<0))throw new InvalidDataException("Invalid recording loudness or special hotkey value.");
        if(next.ToolbarColor.Length==0)throw new InvalidDataException("Choose a toolbar color.");
        foreach(var action in SpecialHotkeyActions.All.Where(x=>x.DefaultValue.HasValue)){
            double value=next.ActionValues.GetValueOrDefault(action.Key,action.DefaultValue!.Value);
            if(value< (action.Key.StartsWith("volumeSet")?0:1)||value>(action.Unit=="s"?3600:100))throw new InvalidDataException("Invalid value for "+action.Title+".");
        }
    }
    public void Export(string file) {Validate(Settings);LibraryStore.AtomicWrite(file, JsonSerializer.Serialize(Settings, LibraryStore.JsonOptions));}
}
