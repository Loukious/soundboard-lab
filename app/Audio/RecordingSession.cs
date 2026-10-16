using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundboardLab.Audio;

internal sealed class RecordingSession(Func<MMDevice> getInput, Func<MMDevice> getOutput,
    RecordingSource source, string path, TimeSpan? maximumDuration) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task? worker;
    private volatile bool running;
    public bool IsRecording => running;
    public event EventHandler<Exception?>? Completed;
    public void Start()
    {
        if (maximumDuration is { } limit && limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        running = true;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = Task.Run(() => RunAsync(initialized));
        initialized.Task.GetAwaiter().GetResult();
    }
    private async Task RunAsync(TaskCompletionSource initialized)
    {
        var captures = new List<WasapiCapture>();
        var providers = new List<ISampleProvider>();
        var captureDevices = new List<MMDevice>();
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
            if (source is RecordingSource.Microphone or RecordingSource.Both)
            {
                var device = getInput(); captureDevices.Add(device);
                var capture = new WasapiCapture(device) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) };
                AddCapture(capture);
            }
            if (source is RecordingSource.Desktop or RecordingSource.Both)
            {
                var device = getOutput(); captureDevices.Add(device);
                AddCapture(new WasapiLoopbackCapture(device));
            }
            foreach (var capture in captures) capture.StartRecording();
            initialized.TrySetResult();
            var clock = Stopwatch.StartNew();
            long writtenFrames = 0;
            var mix = new float[960];
            var temp = new float[960];
            var duration = maximumDuration ?? (TimeSpan.MaxValue-TimeSpan.FromSeconds(1));
            while (!cancellation.IsCancellationRequested)
            {
                // A 60 ms holdback lets independent microphone and loopback packets arrive before mixing.
                var targetFrames = (long)(Math.Min(duration.TotalSeconds, Math.Max(0, clock.Elapsed.TotalSeconds - .06)) * 48000);
                WriteUntil(targetFrames);
                if (clock.Elapsed >= duration + TimeSpan.FromMilliseconds(60)) break;
                await Task.Delay(5).ConfigureAwait(false);
            }
            // Stop and join captures before disposing their devices or writer.
            foreach (var capture in captures) capture.StopRecording();
            foreach (var capture in captures) capture.Dispose();
            captures.Clear();
            WriteUntil((long)(Math.Min(duration.TotalSeconds, clock.Elapsed.TotalSeconds) * 48000));
            void WriteUntil(long targetFrames)
            {
                while (writtenFrames < targetFrames)
                {
                    var frames = (int)Math.Min(480, targetFrames - writtenFrames);
                    Array.Clear(mix);
                    foreach (var provider in providers)
                    {
                        Array.Clear(temp);
                        provider.Read(temp, 0, frames * 2);
                        for (var i = 0; i < frames * 2; i++) mix[i] += temp[i];
                    }
                    for (var i = 0; i < frames * 2; i++) mix[i] = Math.Clamp(mix[i], -1, 1);
                    writer.WriteSamples(mix, 0, frames * 2);
                    writtenFrames += frames;
                }
            }
            void AddCapture(WasapiCapture capture)
            {
                captures.Add(capture);
                var buffer = new BufferedWaveProvider(capture.WaveFormat)
                { BufferDuration = TimeSpan.FromSeconds(3), ReadFully = true, DiscardOnBufferOverflow = false };
                capture.DataAvailable += (_, e) =>
                {
                    try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
                    catch (Exception ex) { failure = ex; cancellation.Cancel(); }
                };
                capture.RecordingStopped += (_, e) => { if (e.Exception is not null) { failure = e.Exception; cancellation.Cancel(); } };
                ISampleProvider provider = buffer.ToSampleProvider();
                if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
                else if (provider.WaveFormat.Channels > 2)
                {
                    var multiplex = new MultiplexingSampleProvider([provider], 2);
                    multiplex.ConnectInputToOutput(0, 0); multiplex.ConnectInputToOutput(1, 1);
                    provider = multiplex;
                }
                if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
                providers.Add(provider);
            }
        }
        catch (Exception ex) { failure = ex; initialized.TrySetException(ex); }
        finally
        {
            foreach (var capture in captures) { try { capture.Dispose(); } catch { } }
            foreach (var device in captureDevices) device.Dispose();
            running = false;
            Completed?.Invoke(this, failure);
        }
    }
    public async Task StopAsync()
    {
        cancellation.Cancel();
        if (worker is not null) await worker.ConfigureAwait(false);
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); cancellation.Dispose(); }
}
