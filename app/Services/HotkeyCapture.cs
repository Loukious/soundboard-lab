namespace SoundboardLab.Services;

public readonly record struct HotkeyInput(int VirtualKey, int Modifiers, bool IsDown, bool IsRepeat = false);

// Independent of Windows input so capture, cancellation and conflicts can be checked
// without submitting keys to the desktop.
public sealed class HotkeyCaptureState(string initial, bool distinguishSides = false)
{
    public string Gesture { get; private set; } = initial;
    public string Display { get; private set; } = initial.Length > 0 ? initial : "Press a key";
    public bool Removed { get; private set; }
    public bool CanAccept => Removed || Gesture.Length > 0;
    public void Remove() { Gesture = ""; Display = "Press a key"; Removed = true; }
    public void Record(HotkeyInput input)
    {
        if (!input.IsDown || input.IsRepeat) return;
        Removed = false;
        string chord = GlobalHotkeyService.GestureForKey(input.VirtualKey, input.Modifiers, distinguishSides);
        if (input.VirtualKey is 16 or 17 or 18 or 91 or 92 or >= 160 and <= 165) {
            Gesture = ""; Display = chord; return;
        }
        Gesture = chord; Display = chord;
    }
}

public sealed record HotkeyAssignment(string Id, Func<string> Read, Action<string> Write, bool Special = false);
public readonly record struct HotkeyConflict(bool Blocked, bool Overwrite)
{
    public string Message => Blocked ? "This hotkey is already in use by a special hotkey." : Overwrite ? "Hotkey is already in use. Overwrite?" : "";
}

public static class HotkeyAssignments
{
    public static bool Equal(string first, string second, bool distinguishSides)
    {
        if (first.Length == 0 || second.Length == 0) return false;
        string Canonical(string value) {
            value = GlobalHotkeyService.NormalizeGesture(value);
            if (!distinguishSides) foreach (string modifier in new[] { "Ctrl", "Shift", "Alt", "Win" })
                value = value.Replace("R" + modifier + "+", modifier + "+", StringComparison.OrdinalIgnoreCase);
            return value;
        }
        return Canonical(first).Equals(Canonical(second), StringComparison.OrdinalIgnoreCase);
    }
    public static HotkeyConflict Check(IEnumerable<HotkeyAssignment> assignments, string target, string gesture, bool distinguishSides)
    {
        var all=assignments.ToArray();
        var matches = all.Where(x => x.Id != target && Equal(x.Read(), gesture, distinguishSides)).ToArray();
        bool blocked=matches.Any(x=>x.Special)&&!all.Any(x=>x.Id==target&&x.Special);
        return new(blocked,!blocked&&matches.Length>0);
    }
    public static void ClearConflicts(IEnumerable<HotkeyAssignment> assignments, string target, string gesture, bool distinguishSides)
    {
        var all=assignments.ToArray();
        var matches = all.Where(x => x.Id != target && Equal(x.Read(), gesture, distinguishSides)).ToArray();
        if (matches.Any(x => x.Special)&&!all.Any(x=>x.Id==target&&x.Special)) throw new ArgumentException("This hotkey is already in use by a special hotkey.");
        foreach (var assignment in matches) assignment.Write("");
    }
}
