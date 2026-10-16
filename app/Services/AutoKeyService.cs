using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SoundboardLab.Services;

public sealed class AutoKeyOptions
{
    public bool Enabled { get; set; }
    public string Gesture { get; set; } = "";
    public string Processes { get; set; } = "";
    public int LeadDelayMs { get; set; } = 50;
    public int TailDelayMs { get; set; } = 100;
}

public sealed class AutoKeyService : IDisposable
{
    internal const nuint InputTag=0x53424C;
    private readonly object gate = new();
    private readonly List<int> acquired = [];
    private CancellationTokenSource? pending;
    private bool disposed;
    private readonly Action<int,bool> emitKey;
    private readonly Func<int,bool> keyHeld;
    private readonly Func<string,bool> processAllowed;
    internal bool OwnsKeys {get{lock(gate)return acquired.Count>0;}}
    public AutoKeyOptions Options { get; set; }
    public AutoKeyService(AutoKeyOptions? options = null)
    {
        Options = options ?? new(); emitKey = Send; processAllowed = AllowedProcess;
        keyHeld = key => (GetAsyncKeyState(key switch {256=>1,257=>2,258=>4,259=>5,260=>6,_=>key}) & 0x8000) != 0;
    }
    internal AutoKeyService(AutoKeyOptions options, Action<int,bool> emit, Func<int,bool> held, Func<string,bool> allowed) : this(options)
    { emitKey=emit;keyHeld=held;processAllowed=allowed; }
    public static int[] ParseGesture(string gesture)
    {
        var keys = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(GlobalHotkeyService.VirtualKey).Distinct().ToArray();
        if (keys.Length == 0 || keys.Any(k => k > 260)) throw new ArgumentException("Push-to-talk requires keyboard keys or held mouse buttons.");
        return keys;
    }
    public async Task<bool> BeginPlaybackAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken token;
        int lead;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending?.Cancel(); pending?.Dispose(); pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); token = pending.Token;
            if (!Options.Enabled) { Release(); return true; }
            if (!processAllowed(Options.Processes)) { Release(); return false; }
            int[] keys = ParseGesture(Options.Gesture);
            bool alreadyHeld = acquired.Count > 0 && keys.All(key => acquired.Contains(key) || keyHeld(key));
            lead = alreadyHeld ? 0 : Math.Clamp(Options.LeadDelayMs, 0, 10000);
            try {
                foreach (int key in keys)
                {
                    if (acquired.Contains(key) || keyHeld(key)) continue;
                    emitKey(key, false); acquired.Add(key);
                }
            } catch { Release(); throw; }
        }
        try { if (lead > 0) await Task.Delay(lead, token).ConfigureAwait(false); return !token.IsCancellationRequested; }
        catch (OperationCanceledException) { lock(gate) { if(pending is not null && pending.Token == token) Release(); } return false; }
    }
    public void EndPlayback() => _ = EndPlaybackAsync();
    public async Task EndPlaybackAsync()
    {
        CancellationToken token;
        int delay;
        lock (gate)
        {
            if (disposed) return;
            pending?.Cancel(); pending?.Dispose(); pending = new(); token = pending.Token; delay = Math.Clamp(Options.TailDelayMs, 0, 10000);
            if (acquired.Count == 0) return;
        }
        try {
            if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
            lock (gate) { if (!token.IsCancellationRequested) Release(); }
        } catch (OperationCanceledException) { }
    }
    private static bool AllowedProcess(string names)
    {
        var allowed = names.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (allowed.Length == 0) return true;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        if (processId == 0) return false;
        try {
            using var process = Process.GetProcessById((int)processId);
            return allowed.Any(name => Path.GetFileNameWithoutExtension(name).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase));
        } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } catch (Win32Exception) { return false; }
    }
    private void Release()
    {
        foreach (int key in acquired.AsEnumerable().Reverse()) {
            try { emitKey(key, true); } catch (Win32Exception) { /* Attempt every release even if one fails. */ }
        }
        acquired.Clear();
    }
    private static void Send(int key, bool up)
    {
        if (key >= 256)
        {
            uint flags = key switch { 256 => up ? 4u : 2u, 257 => up ? 16u : 8u, 258 => up ? 64u : 32u, 259 or 260 => up ? 256u : 128u, _ => throw new ArgumentException("Unsupported mouse button.") };
            var mouse = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { MouseData = key == 259 ? 1u : key == 260 ? 2u : 0, Flags=flags, ExtraInfo=InputTag } } };
            if (SendInput(1,[mouse],Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows rejected a push-to-talk mouse event.");
            return;
        }
        bool extended = key is 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 45 or 46 or 91 or 92 or 111 or 144;
        var input = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = (ushort)key, Flags = (up ? 2u : 0) | (extended ? 1u : 0), ExtraInfo = InputTag } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a push-to-talk key event.");
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending?.Cancel(); pending?.Dispose(); pending = null; Release(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
