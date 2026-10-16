using System.Runtime.InteropServices;

namespace SoundboardLab.Audio;

internal static class NativeInjection
{
    private const string Dll = "SoundboardLab.Audio.dll";
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { public int Connected, SampleRate, Channels, QueuedFrames, ActiveConsumers, LastError; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SB_Open(string endpointId);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void SB_Close();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int SB_GetStatus(out Status status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int SB_Push([In] float[] samples, int frames);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void SB_Stop();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void SB_SetControls(float micGain, float clipGain, int blockVoice, int paused);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern int SB_GetLastError();
}
