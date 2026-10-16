using System.Windows;
using System.Windows.Input;
using SoundboardLab.Models;
using SoundboardLab.Services;

namespace SoundboardLab;

public partial class MainWindow
{
    private List<HotkeyBinding> BuildHotkeyBindings()
    {
        var bindings=new List<HotkeyBinding>();
        if(library.Document.RootCategory.Hotkey.Length>0)bindings.Add(new(library.Document.RootCategory.Hotkey,"random-all"));
        bool Numeric(string gesture)=>gesture.Split('+').LastOrDefault() is {} text&&text.Length>1&&text.All(char.IsAsciiDigit);
        HotkeyBinding FileBinding(string gesture,string action){bool sequence=Numeric(gesture);return new(sequence?Settings.NumcodeModifier+"+"+gesture.Split('+').Last():gesture,action,sequence);}
        if(Settings.SameHotkeysInAllCategories){
            var rows=new Dictionary<int,string>(Settings.RowHotkeys);
            foreach(var category in library.Document.Entries.GroupBy(x=>x.Category)){int index=0;foreach(var sound in category){if(sound.Hotkey.Length>0)rows.TryAdd(index,sound.Hotkey);index++;}}
            foreach(var row in rows)if(row.Value.Length>0&&(Settings.AltNumcodeHotkeys||!Numeric(row.Value)))bindings.Add(FileBinding(row.Value,"row:"+row.Key));
        }else foreach(var sound in library.Document.Entries.Where(x=>x.Hotkey.Length>0))if(Settings.AltNumcodeHotkeys||!Numeric(sound.Hotkey))bindings.Add(FileBinding(sound.Hotkey,"play:"+sound.Id));
        bindings.AddRange(Settings.ActionHotkeys.Select(x=>new HotkeyBinding(x.Value,x.Key,Priority:HotkeyPriority.Special,ReleaseAction:SpecialHotkeyActions.NormalizeKey(x.Key)=="forward-push"?"forward-release":null,Repeatable:!SpecialHotkeyActions.NormalizeKey(x.Key).StartsWith("forward-"))));
        bindings.AddRange(library.Document.Categories.Where(x=>x.Hotkey.Length>0).Select(x=>new HotkeyBinding(x.Hotkey,"random-category:"+x.Name)));
        bindings.AddRange(Settings.HotbarHotkeys.Select(x=>new HotkeyBinding(x.Value,"hotbar:"+x.Key,Priority:HotkeyPriority.Hotbar)));
        if(Settings.IndexHotkeys){
            if(!Settings.NumcodeCategoryIndex)bindings.AddRange(library.Document.Entries.Select((x,i)=>new HotkeyBinding(Settings.IndexModifier+"+"+(i+1),"play:"+x.Id,true)));
            else {int category=1;foreach(var name in new[]{""}.Concat(library.Document.Categories.Select(x=>x.Name))){int sound=1;foreach(var entry in library.Document.Entries.Where(x=>x.Category==name)){string digits=category.ToString()+sound.ToString(Settings.NumcodeTwoDigits?"D2":"D1");bindings.Add(new(Settings.IndexModifier+"+"+digits,"play:"+entry.Id,true));sound++;}category++;}}
        }
        return bindings;
    }
    private bool HotkeyOwnsLocalShortcut(Key key, ModifierKeys modifiers)
    {
        int flags=0;
        foreach(var (modifier,leftKey,rightKey,leftBit,rightBit) in new[]{
            (ModifierKeys.Control,Key.LeftCtrl,Key.RightCtrl,1,16),(ModifierKeys.Shift,Key.LeftShift,Key.RightShift,2,32),
            (ModifierKeys.Alt,Key.LeftAlt,Key.RightAlt,4,64),(ModifierKeys.Windows,Key.LWin,Key.RWin,8,128)}) {
            if(!modifiers.HasFlag(modifier))continue;
            bool right=Settings.AllowLeftRightModifiers&&Keyboard.IsKeyDown(rightKey);
            if(right)flags|=rightBit;
            if(!right||Keyboard.IsKeyDown(leftKey))flags|=leftBit;
        }
        int vk=KeyInterop.VirtualKeyFromKey(key);
        return vk!=0&&hotkeys.BlocksLocalShortcut(GlobalHotkeyService.GestureForKey(vk,flags,Settings.AllowLeftRightModifiers));
    }
    private bool TryHandleGlobalHotkey(KeyEventArgs e,ModifierKeys modifiers)
    {
        Key key=e.Key==Key.System?e.SystemKey:e.Key;
        if(!HotkeyOwnsLocalShortcut(key,modifiers))return false;
        e.Handled=true;return true;
    }
    private void UpdateHotkeyIndicator()
    {
        HotkeysBox.ToolTip=Settings.HotkeysEnabled?"Hotkeys enabled. Double-click to disable.":"Hotkeys disabled. Double-click to enable.";
        HotkeysBox.Opacity=Settings.HotkeysEnabled?1:.45;
    }
    private void Hotkeys_Pressed(object sender,MouseButtonEventArgs e)
    {
        // The original H status icon toggles on double-click, not selection.
        CancelStatusMenu();e.Handled=true;
        if(e.ClickCount%2==0)HotkeysBox.IsChecked=HotkeysBox.IsChecked!=true;
    }
    private string SoundHotkeyTarget(SoundEntry entry) => Settings.SameHotkeysInAllCategories
        ? "row:" + Array.IndexOf(view?.Cast<SoundEntry>().ToArray() ?? [], entry) : "sound:" + entry.Id;
    private IEnumerable<HotkeyAssignment> HotkeyAssignmentsForEditing(IEnumerable<HotkeyAssignment>? actions = null)
    {
        if (Settings.SameHotkeysInAllCategories) {
            // Match ConfigureHotkeys' legacy fallback, including explicit empty rows.
            var rows = new Dictionary<int, string>(Settings.RowHotkeys);
            foreach (var category in library.Document.Entries.GroupBy(x => x.Category)) {
                int index = 0;
                foreach (var sound in category) { if (sound.Hotkey.Length > 0) rows.TryAdd(index, sound.Hotkey); index++; }
            }
            int count = Math.Max(rows.Keys.DefaultIfEmpty(-1).Max() + 1, view?.Cast<SoundEntry>().Count() ?? 0);
            for (int row = 0; row < count; row++) {
                int index = row; string fallback = rows.GetValueOrDefault(index, "");
                yield return new("row:" + index, () => Settings.RowHotkeys.GetValueOrDefault(index, fallback), value => Settings.RowHotkeys[index] = value);
            }
        } else foreach (var sound in library.Document.Entries)
            yield return new("sound:" + sound.Id, () => sound.Hotkey, value => sound.Hotkey = value);
        yield return new("category:*", () => library.Document.RootCategory.Hotkey, value => library.Document.RootCategory.Hotkey = value);
        foreach (var category in library.Document.Categories)
            yield return new("category:" + category.Name, () => category.Hotkey, value => category.Hotkey = value);
        foreach (int slot in Enumerable.Range(0, Settings.HotbarRows * Settings.HotbarColumns).Union(Settings.HotbarHotkeys.Keys)) {
            int index = slot;
            yield return new("hotbar:" + index, () => Settings.HotbarHotkeys.GetValueOrDefault(index, ""), value => {
                if (value.Length == 0) Settings.HotbarHotkeys.Remove(index); else Settings.HotbarHotkeys[index] = value;
            });
        }
        if (actions is not null) { foreach (var action in actions) yield return action; }
        else foreach (string action in Settings.ActionHotkeys.Keys.ToArray())
            yield return new("action:" + action, () => Settings.ActionHotkeys[action], value => Settings.ActionHotkeys[action] = value, true);
    }
    internal HotkeyCaptureWindow CreateHotkeyEditor(Window owner, string current, string target,
        IEnumerable<HotkeyAssignment>? actions = null, bool? distinguishSides = null)
    {
        var assignments = HotkeyAssignmentsForEditing(actions).ToArray();
        bool sides = distinguishSides ?? Settings.AllowLeftRightModifiers;
        return new(current, hotkeys, sides, gesture => HotkeyAssignments.Check(assignments, target, gesture, sides)) { Owner = owner };
    }
    internal string? EditHotkey(Window owner, string current, string target,
        IEnumerable<HotkeyAssignment>? actions = null, bool? distinguishSides = null)
    {
        var dialog = CreateHotkeyEditor(owner, current, target, actions, distinguishSides);
        return dialog.ShowDialog() == true ? dialog.Gesture : null;
    }
    private void AssignHotkey(string target, string gesture)
    {
        var assignments = HotkeyAssignmentsForEditing().ToArray();
        var destination = assignments.Single(x => x.Id == target);
        HotkeyAssignments.ClearConflicts(assignments, target, gesture, Settings.AllowLeftRightModifiers);
        destination.Write(gesture);
        preferenceStore.Save(); ConfigureHotkeys(); SoundGrid.Items.Refresh(); RefreshHotbar();
    }
    // Preferences is a draft. Only clear overwritten ordinary bindings when Apply
    // succeeds, and retain a rollback for failures in device/settings application.
    internal Action PrepareSpecialHotkeyAssignments(AppPreferences draft)
    {
        var previousRows = new Dictionary<int,string>(Settings.RowHotkeys);
        var previousSlots = new Dictionary<int,string>(Settings.HotbarHotkeys);
        var ordinary = HotkeyAssignmentsForEditing().Where(x => !x.Special)
            .Concat(library.Document.Entries.Select(sound => new HotkeyAssignment("legacy:"+sound.Id, () => sound.Hotkey, value => sound.Hotkey = value))).ToArray();
        var changes = draft.ActionHotkeys.Where(x => !HotkeyAssignments.Equal(
            Settings.ActionHotkeys.GetValueOrDefault(x.Key, ""), x.Value, draft.AllowLeftRightModifiers)).Select(x => x.Value).ToArray();
        var overwritten = ordinary.Where(x => changes.Any(key => HotkeyAssignments.Equal(x.Read(), key, draft.AllowLeftRightModifiers)))
            .Select(x => (Assignment: x, Previous: x.Read())).ToArray();
        foreach (var item in overwritten) {
            item.Assignment.Write("");
            if (item.Assignment.Id.StartsWith("row:")) draft.RowHotkeys[int.Parse(item.Assignment.Id[4..])] = "";
        }
        // Row/slot assignments live in the preferences clone as well.
        foreach (int row in draft.RowHotkeys.Keys.ToArray())
            if (changes.Any(key => HotkeyAssignments.Equal(draft.RowHotkeys[row], key, draft.AllowLeftRightModifiers))) draft.RowHotkeys[row] = "";
        foreach (int slot in draft.HotbarHotkeys.Keys.ToArray())
            if (changes.Any(key => HotkeyAssignments.Equal(draft.HotbarHotkeys[slot], key, draft.AllowLeftRightModifiers))) draft.HotbarHotkeys.Remove(slot);
        return () => {
            foreach (var item in overwritten) item.Assignment.Write(item.Previous);
            Settings.RowHotkeys = previousRows; Settings.HotbarHotkeys = previousSlots;
        };
    }
}
