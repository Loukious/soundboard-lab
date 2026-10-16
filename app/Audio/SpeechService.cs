using System.Runtime.InteropServices;
using System.IO;

namespace SoundboardLab.Audio;

public static class SpeechService
{
    public static IReadOnlyList<string> GetVoices()
    {
        return RunSta(() =>
        {
            dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("Windows SAPI is unavailable."))!;
            object? tokens = null;
            try
            {
                tokens = voice.GetVoices(); dynamic collection = tokens;
                var result = new List<string>();
                for (var i = 0; i < collection.Count; i++)
                {
                    object token = collection.Item(i);
                    try { result.Add(((dynamic)token).GetDescription()); }
                    finally { Marshal.FinalReleaseComObject(token); }
                }
                return (IReadOnlyList<string>)result;
            }
            finally { if (tokens is not null) Marshal.FinalReleaseComObject(tokens); Marshal.FinalReleaseComObject(voice); }
        });
    }
    public static Task SynthesizeAsync(string text, string wavPath, string? voiceName = null, int rate = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (rate is < -10 or > 10) throw new ArgumentOutOfRangeException(nameof(rate));
        return Task.Run(() => RunSta(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(wavPath))!);
            dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice")!)!;
            dynamic stream = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpFileStream")!)!;
            object? tokens = null;
            try
            {
                if (voiceName is not null)
                {
                    tokens = voice.GetVoices(); dynamic collection = tokens; bool found = false;
                    for (var i = 0; i < collection.Count; i++)
                    {
                        object token = collection.Item(i);
                        try
                        {
                            if (((string)((dynamic)token).GetDescription()).Equals(voiceName, StringComparison.OrdinalIgnoreCase))
                            { voice.Voice = token; found = true; break; }
                        }
                        finally { Marshal.FinalReleaseComObject(token); }
                    }
                    if (!found) throw new ArgumentException("The selected speech voice is no longer installed.", nameof(voiceName));
                }
                voice.Rate = rate;
                stream.Open(Path.GetFullPath(wavPath), 3, false); // SSFMCreateForWrite.
                voice.AudioOutputStream = stream;
                voice.Speak(text, 0); // Synchronous on a dedicated STA, never the UI thread.
                stream.Close();
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            finally
            {
                if (tokens is not null) Marshal.FinalReleaseComObject(tokens);
                Marshal.FinalReleaseComObject(stream); Marshal.FinalReleaseComObject(voice);
            }
        }), cancellationToken);
    }
    private static T RunSta<T>(Func<T> action)
    {
        T? result = default; Exception? failure = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }
}
