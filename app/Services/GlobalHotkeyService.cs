using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SoundboardLab.Services;

public enum HotkeyPriority { Normal, Hotbar, Special }
public sealed record HotkeyBinding(string Gesture, string Action, bool IsSequence=false, HotkeyPriority Priority=HotkeyPriority.Normal,string? ReleaseAction=null,bool Repeatable=true);
public enum HotkeyConflictPolicy { Reject, FirstWins, LastWins }
public sealed class HotkeyOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowLeftRightModifiers { get; set; }
    public bool AllowRepeat { get; set; }
    public bool AutoStopOnRelease { get; set; }
    public bool SuppressMatchingKeys { get; set; }
    public HotkeyConflictPolicy ConflictPolicy { get; set; } = HotkeyConflictPolicy.Reject;
}

public sealed class GlobalHotkeyService : IDisposable
{
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private readonly HookProc keyboardProc, mouseProc;
    private readonly Func<int,bool> keyHeld;
    private readonly Dictionary<string,HotkeyBinding> sequences=new(StringComparer.OrdinalIgnoreCase);
    private int sequenceFlags;
    private readonly Dictionary<string, HotkeyBinding> bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> pressed = [];
    private readonly HashSet<int> playbackTriggers = [];
    private readonly Dictionary<int,(string Action,int Modifiers)> heldActions=[];
    private HotkeyOptions options = new();
    private nint keyboardHook, mouseHook;
    private string sequence = "";
    private Func<HotkeyInput, bool>? capture;
    private readonly HashSet<int> capturedKeys = [];
    public event Action<string>? Triggered;
    public event Action<Exception>? Error;
    public IReadOnlyList<HotkeyBinding> Bindings=>bindings.Values.Concat(sequences.Values).ToArray();
    public string? ResolveAction(string gesture,bool sequence=false){var source=sequence?sequences:bindings;string normalized=NormalizeGesture(gesture);if(!options.AllowLeftRightModifiers)foreach(var modifier in new[]{"Ctrl","Shift","Alt","Win"})normalized=normalized.Replace("R"+modifier+"+",modifier+"+",StringComparison.OrdinalIgnoreCase);return source.TryGetValue(normalized,out var binding)&&CanDispatch(binding.Action)?binding.Action:null;}
    public bool BlocksLocalShortcut(string gesture)=>options.SuppressMatchingKeys&&!string.IsNullOrWhiteSpace(gesture)&&ResolveAction(gesture) is not null;
    public bool IsStarted => keyboardHook != 0;
    public GlobalHotkeyService() : this(key=>(GetAsyncKeyState(key)&0x8000)!=0) { }
    internal GlobalHotkeyService(Func<int,bool> keyHeld) { this.keyHeld=keyHeld;keyboardProc = Keyboard; mouseProc = Mouse; }
    public void Configure(IEnumerable<HotkeyBinding> definitions, HotkeyOptions? settings = null)
    {
        settings ??= new(); var next = new Dictionary<string, HotkeyBinding>(StringComparer.OrdinalIgnoreCase);var nextSequences=new Dictionary<string,HotkeyBinding>(StringComparer.OrdinalIgnoreCase);
        // Resolve higher classes first so even multiple shadowed lower-class
        // bindings from an imported list cannot invalidate the winning binding.
        foreach (var definition in definitions.OrderByDescending(x=>x.Priority))
        {
            if (string.IsNullOrWhiteSpace(definition.Gesture)) continue;
            string gesture = NormalizeGesture(definition.Gesture);
            if(!settings.AllowLeftRightModifiers)foreach(var modifier in new[]{"Ctrl","Shift","Alt","Win"})gesture=gesture.Replace("R"+modifier+"+",modifier+"+",StringComparison.OrdinalIgnoreCase);
            string last=gesture.Split('+').Last();bool isSequence=definition.IsSequence||(last.Length>1&&last.All(char.IsAsciiDigit));var destination=isSequence?nextSequences:next;
            if(definition.ReleaseAction is not null&&(isSequence||last is "WheelUp" or "WheelDown"))throw new ArgumentException("Push-to-forward requires a key or mouse button that can be held down.");
            if (destination.TryGetValue(gesture,out var previous)) {
                if (previous.Priority>definition.Priority) continue;
                if (previous.Priority==definition.Priority) {
                    if (settings.ConflictPolicy == HotkeyConflictPolicy.Reject) throw new ArgumentException($"Hotkey {gesture} is assigned more than once.");
                    if (settings.ConflictPolicy == HotkeyConflictPolicy.FirstWins) continue;
                }
            }
            if (string.IsNullOrWhiteSpace(definition.Action)) throw new ArgumentException("Hotkey action cannot be empty.");
            destination[gesture] = definition with {Gesture=gesture,IsSequence=isSequence};
        }
        ReleaseHeldActions();bindings.Clear(); foreach (var pair in next) bindings.Add(pair.Key, pair.Value); options = settings;sequences.Clear();foreach(var pair in nextSequences)sequences.Add(pair.Key,pair.Value);
    }
    public void Start()
    {
        if (IsStarted) return;
        nint module = GetModuleHandle(null);
        keyboardHook = SetWindowsHookEx(13, keyboardProc, module, 0);
        if (keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot install keyboard hook.");
        mouseHook = SetWindowsHookEx(14, mouseProc, module, 0);
        if (mouseHook == 0) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error, "Cannot install mouse hook."); }
    }
    public void Stop() { Dispose(); }
    public IDisposable BeginCapture(Func<HotkeyInput, bool> handler, bool installHooks = true)
    {
        if (capture is not null) throw new InvalidOperationException("A hotkey capture is already open.");
        bool started = IsStarted;
        if (installHooks) Start();
        ReleaseHeldActions();sequence = ""; playbackTriggers.Clear(); capturedKeys.Clear(); capture = handler;
        return new CaptureLease(() => {
            capture = null; capturedKeys.Clear(); sequence = ""; playbackTriggers.Clear(); pressed.Clear();
            if (!started) Stop();
        });
    }
    private sealed class CaptureLease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
    public static string GestureForKey(int key, int flags, bool distinguishSides = false)
    {
        if (!distinguishSides) flags = (flags & 15) | ((flags >> 4) & 15);
        // A modifier by itself is a preview, never a normal binding.
        if (key is 16 or 17 or 18 or 91 or 92 or >= 160 and <= 165)
            return Prefix(flags).TrimEnd('+');
        return Prefix(flags) + KeyName(key);
    }
    public static string NormalizeGesture(string gesture)
    {
        string[] parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("Hotkey is empty.");
        int modifiers = 0;
        foreach (string part in parts[..^1]) modifiers |= Modifier(part) is var m && m != 0 ? m : throw new ArgumentException($"Unknown hotkey modifier {part}.");
        string key = parts[^1];
        if (modifiers !=0 && key.Length > 1 && key.All(char.IsAsciiDigit)) return Prefix(modifiers) + key;
        int vk = VirtualKey(key); return Prefix(modifiers) + KeyName(vk);
    }
    internal static int VirtualKey(string name)
    {
        name = name.Trim().ToUpperInvariant();
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return name[0];
        if (name.StartsWith("F") && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) return 111 + f;
        if ((name.StartsWith("NUMPAD") || name.StartsWith("NUM")) && int.TryParse(name[(name.StartsWith("NUMPAD") ? 6 : 3)..], out int digit) && digit is >= 0 and <= 9) return 96 + digit;
        if (name.StartsWith("VK") && int.TryParse(name[2..], out int raw) && raw is > 0 and <= 255) return raw;
        return name switch {
            "CTRL" or "CONTROL" => 17,"LCTRL" => 162,"RCTRL"=>163, "SHIFT" => 16,"LSHIFT"=>160,"RSHIFT"=>161, "ALT"=>18,"LALT"=>164,"RALT"=>165, "WIN" or "WINDOWS" or "LWIN"=>91,"RWIN"=>92,
            "SPACE" => 32, "TAB" => 9, "ENTER" or "RETURN" => 13, "ESC" or "ESCAPE" => 27, "BACKSPACE" => 8,
            "DELETE" or "DEL" => 46, "INSERT" => 45, "HOME" => 36, "END" => 35, "PAGEUP" => 33, "PAGEDOWN" => 34,
            "LEFT" => 37, "UP" => 38, "RIGHT" => 39, "DOWN" => 40, "PAUSE" => 19, "CAPSLOCK" => 20, "PRINTSCREEN" => 44,
            "NUMLOCK" => 144, "SCROLLLOCK" => 145, "ADD" => 107, "SUBTRACT" => 109, "MULTIPLY" => 106, "DIVIDE" => 111, "DECIMAL" => 110,
            "MOUSELEFT" or "MOUSE1" => 256, "MOUSERIGHT" or "MOUSE2" => 257, "MOUSEMIDDLE" or "MOUSE3" => 258,
            "MOUSEX1" or "MOUSE4" => 259, "MOUSEX2" or "MOUSE5" => 260, "WHEELUP" => 261, "WHEELDOWN" => 262,
            "VOLUMEMUTE" => 173, "VOLUMEDOWN" => 174, "VOLUMEUP" => 175, "MEDIANEXT" => 176, "MEDIAPREVIOUS" => 177, "MEDIASTOP" => 178, "MEDIAPLAYPAUSE" => 179,
            _ => throw new ArgumentException($"Unsupported hotkey key '{name}'.")
        };
    }
    private static string KeyName(int vk) => vk switch {
        >= 48 and <= 90 => ((char)vk).ToString(), >= 112 and <= 135 => "F" + (vk - 111), >= 96 and <= 105 => "NumPad" + (vk - 96),
        160=>"Shift",161=>"RShift",162=>"Ctrl",163=>"RCtrl",164=>"Alt",165=>"RAlt",92=>"RWin",8 => "Backspace", 9 => "Tab", 13 => "Enter", 16 => "Shift", 17 => "Ctrl", 18 => "Alt", 19 => "Pause", 20 => "CapsLock", 27 => "Escape", 32 => "Space", 33 => "PageUp", 34 => "PageDown", 35 => "End", 36 => "Home", 37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down", 44 => "PrintScreen", 45 => "Insert", 46 => "Delete", 91 => "Win", 106 => "Multiply", 107 => "Add", 109 => "Subtract", 110 => "Decimal", 111 => "Divide", 144 => "NumLock", 145 => "ScrollLock", 173 => "VolumeMute", 174 => "VolumeDown", 175 => "VolumeUp", 176 => "MediaNext", 177 => "MediaPrevious", 178 => "MediaStop", 179 => "MediaPlayPause", 256 => "MouseLeft", 257 => "MouseRight", 258 => "MouseMiddle", 259 => "MouseX1", 260 => "MouseX2", 261 => "WheelUp", 262 => "WheelDown", _ => "VK" + vk
    };
    private static int Modifier(string part) => part.ToUpperInvariant() switch { "CTRL" or "CONTROL" => 1, "SHIFT" => 2, "ALT" => 4, "WIN" or "WINDOWS" or "LWIN" => 8,"RCTRL"=>16,"RSHIFT"=>32,"RALT"=>64,"RWIN"=>128,"LCTRL"=>1,"LSHIFT"=>2,"LALT"=>4, _ => 0 };
    private static string Prefix(int flags) => ((flags & 1) != 0 ? "Ctrl+" : "") + ((flags & 2) != 0 ? "Shift+" : "") + ((flags & 4) != 0 ? "Alt+" : "") + ((flags & 8) != 0 ? "Win+" : "")+((flags&16)!=0?"RCtrl+":"")+((flags&32)!=0?"RShift+":"")+((flags&64)!=0?"RAlt+":"")+((flags&128)!=0?"RWin+":"");
    private int Modifiers(int released=-1, bool? distinguishSides=null,int pressedKey=-1)
    {
        // Other applications/system shortcuts can swallow a modifier release.
        // Reconcile physical state instead of keeping that modifier stuck forever.
        // Consumed capture modifiers need their tracked state because Windows
        // does not update asynchronous state for a blocked key-down.
        bool HeldKey(int key)=>key!=released&&(key==pressedKey||capturedKeys.Contains(key)||keyHeld(key));
        int flags=0;foreach(var (left,right,bit,rightBit) in new[]{(162,163,1,16),(160,161,2,32),(164,165,4,64),(91,92,8,128)}){if(HeldKey(left))flags|=bit;if(HeldKey(right))flags|=(distinguishSides??options.AllowLeftRightModifiers)?rightBit:bit;}return flags;
    }
    private nint Keyboard(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(keyboardHook, code, message, data);
        try {
            var info = Marshal.PtrToStructure<KeyboardData>(data);
            if(ProcessKeyboardMessage((int)message,(int)info.Key,info.Flags,info.ExtraInfo))return 1;
        } catch (Exception ex) { Error?.Invoke(ex); }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    internal bool ProcessKeyboardMessage(int message,int vk,uint flags,nuint extraInfo)
    {
        // Auto Keys tags its own input. Keyboard/macro utilities may also mark
        // F2 as injected; rejecting every injected event makes those keys inert.
        if((flags&16)!=0&&extraInfo==AutoKeyService.InputTag)return false;
        bool down=message is 0x100 or 0x104,up=message is 0x101 or 0x105;
        if(!down&&!up)return false;
        bool repeat=down&&pressed.Contains(vk);
        if(down)pressed.Add(vk);else pressed.Remove(vk);
        return ProcessKeyboardInput(new(vk,Modifiers(up?vk:-1,capture is not null?true:null,down?vk:-1),down,repeat));
    }
    // The native hook and off-screen regressions use this same dispatch path.
    // Focus is deliberately absent: configured hotkeys are global.
    internal bool ProcessKeyboardInput(HotkeyInput input)
    {
        int vk=input.VirtualKey,flags=input.Modifiers;
        if (capture is {} recorder) {
            bool handled=recorder(input);
            if(input.IsDown&&handled)capturedKeys.Add(vk);
            bool wasCaptured=!input.IsDown&&capturedKeys.Remove(vk);
            return handled||wasCaptured;
        }
        if(input.IsDown){
            if(vk is 16 or 17 or 18 or 91 or 92 or >=160 and <=165)return false;
            if(options.Enabled&&flags!=0&&vk is >=96 and <=105&&sequences.Keys.Any(k=>k[..(k.LastIndexOf('+')+1)].Equals(Prefix(flags),StringComparison.OrdinalIgnoreCase))){
                if(sequence.Length==0)sequenceFlags=flags;
                if(!input.IsRepeat)sequence+=(vk-96).ToString();if(sequence.Length>16)sequence="";
                return options.SuppressMatchingKeys;
            }
            string gesture=GestureForKey(vk,flags,options.AllowLeftRightModifiers);
            // A repeated bound key is still owned by the hotkey, even when
            // repeating its action is disabled. It must not fall through to F2.
            bool block=BlocksLocalShortcut(gesture);
            if(!input.IsRepeat||(options.AllowRepeat&&bindings.TryGetValue(NormalizeGesture(gesture),out var repeated)&&repeated.Repeatable))Dispatch(gesture,vk);
            return block;
        }
        ReleaseHeldActions(vk,flags);
        if(sequence.Length>0&&flags!=sequenceFlags){string gesture=Prefix(sequenceFlags)+sequence;sequence="";if(ResolveAction(gesture,true) is {} action)Triggered?.Invoke(action);}
        if(options.AutoStopOnRelease&&playbackTriggers.Remove(vk))Triggered?.Invoke("stop");
        return false;
    }
    private nint Mouse(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(mouseHook, code, message, data);
        try {
            var info = Marshal.PtrToStructure<MouseData>(data); if ((info.Flags & 1) != 0&&info.ExtraInfo==AutoKeyService.InputTag) return CallNextHookEx(mouseHook, code, message, data);
            // The capture control receives pointer events through WPF. Never fire
            // a sound/action while an assignment dialog is open.
            if (capture is not null) return CallNextHookEx(mouseHook, code, message, data);
            int msg = (int)message;
            int key = msg switch { 0x201 or 0x202 => 256, 0x204 or 0x205 => 257, 0x207 or 0x208 => 258, 0x20B or 0x20C => ((info.Data >> 16) & 0xffff) == 1 ? 259 : 260, 0x20A => (short)(info.Data >> 16) > 0 ? 261 : 262, _ => 0 };
            bool up = msg is 0x202 or 0x205 or 0x208 or 0x20C;
            if (up) {ReleaseHeldActions(key,Modifiers());if (options.AutoStopOnRelease && playbackTriggers.Remove(key)) Triggered?.Invoke("stop"); }
            else if (key != 0 && Dispatch(Prefix(Modifiers()) + KeyName(key), key) && options.SuppressMatchingKeys) return 1;
        } catch (Exception ex) { Error?.Invoke(ex); }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    private bool CanDispatch(string action)=>capture is null&&(options.Enabled||action is "hotkeys-on" or "hotkeys-toggle" or "hotkeysEnable" or "hotkeysToggle");
    private bool Dispatch(string gesture, int key)
    {
        if (ResolveAction(gesture) is not {} action) return false;
        string normalized=NormalizeGesture(gesture);
        if(bindings.TryGetValue(normalized,out var binding)&&binding.ReleaseAction is {} release){
            if(heldActions.ContainsKey(key))return true;
            heldActions[key]=(release,ParseModifiers(normalized));
        }
        if (options.AutoStopOnRelease && key >= 0 && key is not 261 and not 262 && action.StartsWith("play:", StringComparison.Ordinal)) playbackTriggers.Add(key);
        Triggered?.Invoke(action); return true;
    }
    private static int ParseModifiers(string gesture){int flags=0;foreach(string modifier in gesture.Split('+')[..^1])flags|=Modifier(modifier);return flags;}
    private void ReleaseHeldActions(int key=-1,int flags=0)
    {
        foreach(var held in heldActions.Where(x=>key<0||x.Key==key||(flags&x.Value.Modifiers)!=x.Value.Modifiers).ToArray()){heldActions.Remove(held.Key);Triggered?.Invoke(held.Value.Action);}
    }
    public void Dispose()
    {
        ReleaseHeldActions();
        if (keyboardHook != 0) { UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; }
        if (mouseHook != 0) { UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
        pressed.Clear(); playbackTriggers.Clear(); sequence = "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
