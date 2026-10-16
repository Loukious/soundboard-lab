using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundboardLab.Audio;

static void Write(object value, string? path = null)
{
    var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    if (path is not null) File.WriteAllText(path, json);
    Console.WriteLine(json);
}
using var enumerator = new MMDeviceEnumerator();
if (args.Length > 0 && args[0] == "tone")
{
    using var writer = new WaveFileWriter(args[1], WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    var samples = new float[48000 * 4 * 2];
    for (int i = 0; i < samples.Length / 2; i++) samples[2*i] = samples[2*i+1] = .15f * (float)Math.Sin(2 * Math.PI * 997 * i / 48000);
    writer.WriteSamples(samples, 0, samples.Length);
    return;
}
if (args.Length == 0 || args[0] == "devices")
{
    using var communications = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    using var multimedia = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
    Write(new { Communications = new { communications.ID, communications.FriendlyName }, Multimedia = new { multimedia.ID, multimedia.FriendlyName },
        Capture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new { d.ID, d.FriendlyName }).ToArray() }, args.ElementAtOrDefault(1));
    return;
}
if (args[0] == "capture")
{
    using var device = enumerator.GetDevice(args[1]);
    using var capture = new WasapiCapture(device) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) };
    var bins = new List<object>();
    var total = 0L; var n = 0; double ss = 0, sine = 0, cosine = 0; Exception? error = null;
    capture.DataAvailable += (_, e) =>
    {
        for (int offset = 0; offset + 8 <= e.BytesRecorded; offset += 8)
        {
            double sample = (BitConverter.ToSingle(e.Buffer, offset) + BitConverter.ToSingle(e.Buffer, offset + 4)) * .5;
            double phase = 2 * Math.PI * 997 * total / 48000;
            ss += sample * sample; sine += sample * Math.Sin(phase); cosine += sample * Math.Cos(phase); n++; total++;
            if (n == 12000)
            {
                bins.Add(new { EndSeconds = total / 48000.0, Rms = Math.Sqrt(ss / n), ToneAmplitude = 2 * Math.Sqrt(sine * sine + cosine * cosine) / n });
                n = 0; ss = sine = cosine = 0;
            }
        }
    };
    capture.RecordingStopped += (_, e) => error = e.Exception;
    capture.StartRecording();
    File.WriteAllText(args[2] + ".ready", device.ID);
    await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)));
    capture.StopRecording();
    Write(new { ProcessId = Environment.ProcessId, EndpointId = device.ID, Format = capture.WaveFormat.ToString(), Frames = total, Error = error?.ToString(), Bins = bins }, args[2]);
    if (error is not null || total == 0) Environment.ExitCode = 1;
    return;
}
if (args[0] == "feed")
{
    int hr = NativeInjection.SB_Open(args[1]);
    if (hr < 0) { Write(new { OpenHResult = $"0x{hr:X8}", Error = "No hosted mixer transport is accessible." }, args[2]); Environment.ExitCode = 2; return; }
    var snapshots = new List<object>();
    try
    {
        NativeInjection.SB_Stop();
        NativeInjection.SB_SetControls(1, 1, 1, 0);
        long sent = 0; var samples = new float[9600]; var clock = Stopwatch.StartNew();
        while (sent < 48000 * 4)
        {
            NativeInjection.SB_GetStatus(out var status);
            if (status.QueuedFrames < 4800)
            {
                var frames = (int)Math.Min(4800, 48000 * 4 - sent);
                for (int i = 0; i < frames; i++) samples[2*i] = samples[2*i+1] = .15f * (float)Math.Sin(2 * Math.PI * 997 * (sent + i) / 48000);
                var accepted = NativeInjection.SB_Push(samples, frames);
                if (accepted < 0) throw new InvalidOperationException($"Push failed: 0x{accepted:X8}");
                sent += accepted;
            }
            if (snapshots.Count == 0 || clock.Elapsed.TotalSeconds > snapshots.Count)
                snapshots.Add(new { Seconds = clock.Elapsed.TotalSeconds, status.Connected, status.ActiveConsumers, status.QueuedFrames, status.LastError });
            if (clock.Elapsed.TotalSeconds > 8) throw new TimeoutException("Hosted consumer did not drain the tone.");
            await Task.Delay(5);
        }
        await Task.Delay(300);
        Write(new { ProcessId = Environment.ProcessId, FramesSent = sent, Snapshots = snapshots }, args[2]);
    }
    finally { NativeInjection.SB_Stop(); NativeInjection.SB_SetControls(1, 1, 0, 0); NativeInjection.SB_Close(); }
    return;
}
throw new ArgumentException("Expected devices, capture or feed.");
