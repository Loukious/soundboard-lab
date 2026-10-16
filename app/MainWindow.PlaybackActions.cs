using SoundboardLab.Models;

namespace SoundboardLab;

public partial class MainWindow
{
    private async Task StartPlaybackAsync(bool fromHotkey)
    {
        if(audio.IsPaused&&lastDestination==Destination){await TogglePauseAsync();return;}
        // A changed default mode applies to the next hotkey playback. Keeping
        // lastDestination here made the new status-menu selection ineffective.
        SoundEntry? entry=playingEntry??Selected??view?.Cast<SoundEntry>().FirstOrDefault();
        if(entry is null)return;
        if(playingEntry is null&&Selected is null)SoundGrid.SelectedItem=entry;
        await PlayEntryAsync(entry,fromHotkey:fromHotkey);
    }
}
