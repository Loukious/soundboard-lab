using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundboardLab.Audio;

public sealed partial class AudioService : IDisposable
{
    internal static IApplicationSessionVolume? ApplicationVolume {get;set;}
    private bool speakerUsesSessionVolume;
    internal float SpeakerPcmGain=>(speakerUsesSessionVolume?1:speakerVolume)*clipVolume;
    internal void AttachSpeakerSession(string endpointId)=>speakerUsesSessionVolume=ApplicationVolume?.TryAttach(endpointId)==true;
    private readonly object gate = new();
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly PlaybackCache cache = new();
    private const int RenderLatencyMs = 10;
    private readonly MMDeviceEnumerator enumerator = new();
    private CancellationTokenSource? playbackCancel;
    private Task? playbackTask;
    private WasapiOut? player;
    private BufferedWaveProvider? speakerBuffer;
    private PlaybackGainProvider? counter;
    private float[] samples = [], microphoneSamples = [];
    private readonly float[] primeMicrophone = new float[9600];
    public PlaybackRoutingOptions RoutingOptions { get; set; } = new();
    public RecordingExportOptions RecordingOptions { get; set; } = new();
    private PlaybackDestination destination;
    private string? inputId, outputId;
    private int submittedFrames, baseFrame, requestedSeek = -1;
    private int timelineVersion;
    private bool paused, playing, disposed;
    private bool nativeOpened=>nativeClients.Count>0;
    private readonly List<NativeAudioClient> nativeClients=[];
    private string[] captureIds=[];
    private Dictionary<string,double> deviceBoosts=[];
    private float speakerVolume = 1, clipVolume = 1, microphoneGain = 1;
    private bool blockVoice;
    private RecordingSession? recording;
    private Task recordingFinalization=Task.CompletedTask;
    public event EventHandler<PlaybackProgressEventArgs>? Progress;
    public event EventHandler<AudioErrorEventArgs>? Error;
    public event EventHandler? RecordingCompleted;
    public string? LastRecordingPath { get; private set; }
    public bool IsRecording => (recording?.IsRecording ?? false)||!recordingFinalization.IsCompleted;
    public IReadOnlyList<AudioDevice> Devices
    {
        get
        {
            var result = new List<AudioDevice>();
            foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    using (device) result.Add(new(device.ID, device.FriendlyName, flow == DataFlow.Capture));
            return result;
        }
    }
    public TimeSpan Duration => TimeSpan.FromSeconds(samples.Length / 96000d);
    public int TimelineVersion => Volatile.Read(ref timelineVersion);
    public TimeSpan Position { get { lock (gate) return TimeSpan.FromSeconds(CurrentFrame() / 48000d); } }
    public bool IsPlaying => playing && !paused;
    public bool IsPaused => playing && paused;
    public float SpeakerVolume { get => speakerVolume; set {speakerVolume=ValidateGain(value);ApplicationVolume?.TrySetVolume(Math.Clamp(value,0,1));} }
    public float ClipVolume { get => clipVolume; set { clipVolume = ValidateGain(value); UpdateControls(); } }
    public float MicrophoneGain { get => microphoneGain; set { microphoneGain = ValidateGain(value); UpdateControls(); } }
    public bool BlockVoice { get => blockVoice; set { blockVoice = value; UpdateControls(); } }
    private static float ValidateGain(float value) => float.IsFinite(value) && value is >= 0 and <= 4 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    public void SelectDevices(string? captureId, string? renderId,IEnumerable<string>? selectedCaptures=null,IReadOnlyDictionary<string,double>? boosts=null)
    {
        if (playing || IsRecording || IsForwarding) throw new InvalidOperationException("Stop playback, recording and forwarding before changing devices.");
        CloseNativeClients();
        inputId = captureId; outputId = renderId;captureIds=selectedCaptures?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()??[];deviceBoosts=boosts?.ToDictionary(x=>x.Key,x=>x.Value)??[];
    }
    internal MMDevice GetInputDevice()
    {
        var current=enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Communications);
        if(captureIds.Length>0){if(captureIds.Contains(current.ID,StringComparer.OrdinalIgnoreCase))return current;current.Dispose();return enumerator.GetDevice(captureIds[0]);}
        if(inputId is null||inputId.Equals(current.ID,StringComparison.OrdinalIgnoreCase))return current;
        current.Dispose();return enumerator.GetDevice(inputId);
    }
    internal MMDevice GetOutputDevice() => outputId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(outputId);

    public InjectionStatus GetInjectionStatus()
    {
        try
        {
            if (!nativeOpened)
            {
                using var device=GetInputDevice();var ids=captureIds.Length>0?captureIds:[device.ID];
                try{foreach(string id in ids)nativeClients.Add(new(id));}catch{CloseNativeClients();throw;}
                UpdateControls();
            }
            var states=nativeClients.Select(x=>x.Status).ToArray();bool connected=states.All(x=>x.Connected!=0&&x.ActiveConsumers>0);
            return new(connected,states[0].SampleRate,states[0].Channels,states.Max(x=>x.QueuedFrames),states.Sum(x=>x.ActiveConsumers),states.Select(x=>x.LastError).FirstOrDefault(x=>x!=0),connected?"Microphone mixing is ready.":"Microphone mixing is waiting for a selected device. Reopen your recording app or run the microphone test.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or COMException or InvalidOperationException)
        { return new(false, 0, 0, 0, 0, ex.HResult, ex is InvalidOperationException?ex.Message:"The microphone component could not load. Reinstall the complete test package; diagnostics include the Windows error code."); }
    }

    public async Task PlayAsync(string path, PlaybackDestination target, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var prepared = await cache.GetAsync(path, target, RoutingOptions, cancellationToken).ConfigureAwait(false);
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (playing && destination == target && playbackTask is { IsCompleted: false })
                {
                    samples = prepared.Speakers; microphoneSamples = prepared.Microphone;
                    baseFrame = submittedFrames = 0; requestedSeek = -1; paused = false;
                    if (nativeOpened) { StopNativeClients(); UpdateControls(); }
                    Interlocked.Increment(ref timelineVersion);
                    PrimePlayback();
                    player?.Play();
                    return;
                }
            }
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            destination = target;
            if (target != PlaybackDestination.Speakers)
            {
                var status = GetInjectionStatus();
                if (!status.Connected) throw new InvalidOperationException(status.Message);
            }
            samples = prepared.Speakers;microphoneSamples=prepared.Microphone; baseFrame = submittedFrames = 0; requestedSeek = -1; paused = false;
            Interlocked.Increment(ref timelineVersion);
            if (target != PlaybackDestination.Microphone)
            {
                speakerBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
                { BufferDuration = TimeSpan.FromMilliseconds(500), ReadFully = true, DiscardOnBufferOverflow = false };
                counter = new PlaybackGainProvider(speakerBuffer, () => SpeakerPcmGain);
                using var device = GetOutputDevice();
                player = new WasapiOut(device, AudioClientShareMode.Shared, true, RenderLatencyMs);
                player.Init(counter);
                AttachSpeakerSession(device.ID);
                player.PlaybackStopped += (_, e) => { if (e.Exception is not null) ReportError(e.Exception); };
            }
            if (nativeOpened) { StopNativeClients(); UpdateControls(); }
            lock(gate){
                // Prime before starting WASAPI: its first read must see PCM,
                // rather than fill the render buffer with an initial silence.
                PrimePlayback();playing = true;UpdateControls();player?.Play();
                // The caller's token cancels preparation/startup. Once started,
                // the stream belongs to this service until stopped or replaced.
                playbackCancel = new CancellationTokenSource();
                playbackTask = Task.Run(() => PumpAsync(playbackCancel.Token));
            }
        }
        catch { await StopCoreAsync().ConfigureAwait(false); throw; }
        finally { transition.Release(); }
    }

    // Called under gate after resetting the timeline. Supply the initial batch
    // synchronously so retriggers do not wait on the worker's polling interval.
    private void PrimePlayback()
    {
        int frames=Math.Min(4800,samples.Length/2-baseFrame);
        if(destination!=PlaybackDestination.Speakers&&frames>0){
            Array.Copy(microphoneSamples,baseFrame*2,primeMicrophone,0,frames*2);
            frames=PushNativeClients(primeMicrophone,frames);
            if(frames<0)throw new InvalidOperationException($"Microphone transport rejected audio ({NativeInjection.SB_GetLastError()}).");
        }
        counter?.Restart(samples,baseFrame,frames);
        submittedFrames=frames;
    }

    public void Pause()
    {
        lock(gate){
            if(!playing)return;
            int position=CurrentFrame();paused=true;player?.Pause();
            if(forwarding is not null&&destination!=PlaybackDestination.Speakers){
                // Discard already mixed packets, then retain their unconsumed clip
                // frames for Resume while live forwarding continues independently.
                baseFrame=position;submittedFrames=0;requestedSeek=-1;StopNativeClients();PrimePlayback();Interlocked.Increment(ref timelineVersion);
            }
            UpdateControls();
        }
        RaiseProgress();
    }
    public void Resume() { lock (gate) { if (!playing) return; paused = false; UpdateControls(); player?.Play(); } RaiseProgress(); }
    public void Seek(TimeSpan position)
    {
        lock (gate) requestedSeek = (int)Math.Clamp(position.TotalSeconds * 48000, 0, samples.Length / 2);
    }
    public bool TryReplay()
    {
        lock(gate){
            if(disposed||!playing||paused||playbackCancel is not {IsCancellationRequested:false}||playbackTask is not {IsCompleted:false})return false;
            Seek(TimeSpan.Zero);
            return true;
        }
    }
    public void Stop() => StopAsync().GetAwaiter().GetResult();
    public async Task StopAsync()
    {
        await transition.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { transition.Release(); }
    }
    private async Task StopCoreAsync()
    {
        playbackCancel?.Cancel();
        if (playbackTask is not null) { try { await playbackTask.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        playbackTask = null; playbackCancel?.Dispose(); playbackCancel = null;
        lock (gate)
        {
            if (nativeOpened) StopNativeClients();
            DisposePlayer(); playing = paused = false; baseFrame = submittedFrames = 0; requestedSeek = -1;
            Interlocked.Increment(ref timelineVersion);
        }
        RaiseProgress();
    }
    private async Task PumpAsync(CancellationToken token)
    {
        var chunk = new float[9600];var microphoneChunk=new float[9600]; // Fill ahead in batches; Windows timer granularity must not slow playback.
        var lastProgress = Environment.TickCount64;
        long speakerDrainStarted = -1;
        int observedTimelineVersion = -1;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool ended = false;
                int iterationVersion;
                lock (gate)
                {
                    iterationVersion = timelineVersion;
                    if (observedTimelineVersion != timelineVersion){speakerDrainStarted = -1;observedTimelineVersion = timelineVersion;}
                    if (requestedSeek >= 0)
                    {
                        baseFrame = requestedSeek; submittedFrames = 0; requestedSeek = -1;
                        Interlocked.Increment(ref timelineVersion);
                        speakerDrainStarted = -1;
                        if (destination != PlaybackDestination.Speakers) StopNativeClients();
                        PrimePlayback();
                    }
                    if (!paused)
                    {
                        var pending = destination == PlaybackDestination.Speakers ? (speakerBuffer?.BufferedBytes ?? 0) / 8 : NativeQueued();
                        if (destination != PlaybackDestination.Speakers && !GetInjectionStatus().Connected)
                            throw new InvalidOperationException("Microphone injection disconnected during playback.");
                        if (baseFrame + submittedFrames >= samples.Length / 2)
                            ended = pending == 0 && (speakerBuffer?.BufferedBytes ?? 0) == 0;
                        if (ended && speakerBuffer is not null)
                        {
                            // Provider reads are ahead of the physical speaker clock. Let the final
                            // WASAPI buffer render before stopping its client.
                            if (speakerDrainStarted < 0) speakerDrainStarted = Environment.TickCount64;
                            ended = Environment.TickCount64 - speakerDrainStarted >= 60;
                        }
                        else if (pending < 4800 && (speakerBuffer?.BufferedBytes ?? 0) < 4800 * 8)
                        {
                            var frames = Math.Min(4800, samples.Length / 2 - baseFrame - submittedFrames);
                            Array.Copy(samples, (baseFrame + submittedFrames) * 2, chunk, 0, frames * 2);
                            if (destination != PlaybackDestination.Speakers)
                            {
                                Array.Copy(microphoneSamples,(baseFrame+submittedFrames)*2,microphoneChunk,0,frames*2);
                                frames = PushNativeClients(microphoneChunk, frames);
                                if (frames < 0) throw new InvalidOperationException($"Microphone transport rejected audio ({NativeInjection.SB_GetLastError()}).");
                            }
                            if (frames > 0 && speakerBuffer is not null)
                            {
                                var bytes = new byte[frames * 8]; Buffer.BlockCopy(chunk, 0, bytes, 0, bytes.Length);
                                speakerBuffer.AddSamples(bytes, 0, bytes.Length);
                            }
                            submittedFrames += frames;
                        }
                    }
                }
                if (Environment.TickCount64 - lastProgress >= 50) { RaiseProgress(); lastProgress = Environment.TickCount64; }
                if (ended)
                {
                    int completedVersion;
                    lock (gate)
                    {
                        // A trigger may have replaced the timeline after the
                        // worker observed the previous sound's final frame.
                        if (timelineVersion != iterationVersion || requestedSeek >= 0) continue;
                        playing = paused = false; DisposePlayer();UpdateControls();
                        baseFrame = submittedFrames = 0; requestedSeek = -1;
                        completedVersion = Interlocked.Increment(ref timelineVersion);
                    }
                    RaiseProgress(completed: true, version: completedVersion);
                    return;
                }
                await Task.Delay(5, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (gate) { playing = false; DisposePlayer(); if (nativeOpened) StopNativeClients(); } ReportError(ex); RaiseProgress(); }
    }
    private int NativeTransportQueued()=>nativeClients.Select(x=>Math.Max(0,x.Status.QueuedFrames)).DefaultIfEmpty(0).Max();
    private int NativeQueued()=>forwarding is {} session?session.Mixer.PendingClipFrames(NativeTransportQueued()):NativeTransportQueued();
    private void StopNativeTransport(){foreach(var client in nativeClients)client.Stop();}
    private void StopNativeClients(){forwarding?.Mixer.ResetClip();StopNativeTransport();}
    private void CloseNativeClients(){foreach(var client in nativeClients)client.Dispose();nativeClients.Clear();}
    private int PushNativeClients(float[] data,int count)=>forwarding is {} session?session.Mixer.QueueClip(data,count):PushNativeTransport(data,count);
    private int PushNativeTransport(float[] data,int count){int accepted=Math.Min(count,Math.Max(0,192000-NativeTransportQueued()));foreach(var client in nativeClients){int written=client.Push(data,accepted);if(written!=accepted)throw new InvalidOperationException("A recording device could not accept synchronized audio.");}return accepted;}
    private int CurrentFrame()
    {
        if (samples.Length == 0) return 0;
        var consumed = destination == PlaybackDestination.Speakers ? (int)(counter?.ConsumedFrames ?? submittedFrames)
            : submittedFrames - (nativeOpened ? NativeQueued() : 0);
        return Math.Clamp(baseFrame + consumed, 0, samples.Length / 2);
    }
    private void UpdateControls(){foreach(var client in nativeClients)client.Controls(microphoneGain*(float)Math.Pow(10,deviceBoosts.GetValueOrDefault(client.EndpointId)/20),forwarding is null?clipVolume:1,forwarding is null?blockVoice:ForwardingMuteVoice||(playing&&!paused&&destination!=PlaybackDestination.Speakers&&blockVoice),forwarding is null&&paused);}
    private void DisposePlayer() { player?.Stop(); player?.Dispose(); player = null; speakerBuffer = null; counter = null; }
    private void RaiseProgress(bool completed = false, int? version = null)
    {
        PlaybackProgressEventArgs progress;
        lock (gate)
        {
            if (version.HasValue && version != timelineVersion) return;
            progress = new(Position, Duration, IsPlaying, completed, timelineVersion);
        }
        try { Progress?.Invoke(this, progress); } catch { /* UI listeners must not terminate the audio worker. */ }
    }
    private void ReportError(Exception error) { try { Error?.Invoke(this, new(error.Message, error)); } catch { } }

    public void StartRecording(RecordingSource source, string wavPath, TimeSpan? maximumDuration = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsRecording) throw new InvalidOperationException("A recording is already running.");
        recording?.Dispose();
        recording = new RecordingSession(GetInputDevice, GetOutputDevice, source, wavPath, maximumDuration);
        var exportOptions=RecordingOptions;
        recording.Completed += (_, e) => recordingFinalization=FinalizeRecording(e);
        async Task FinalizeRecording(Exception? error){try{if(error is not null)throw error;LastRecordingPath=await RecordingExport.SaveAsync(wavPath,exportOptions).ConfigureAwait(false);RecordingCompleted?.Invoke(this,EventArgs.Empty);}catch(Exception ex){ReportError(ex);}}
        recording.Start();
    }
    public string? StopRecording() => StopRecordingAsync().GetAwaiter().GetResult();
    public async Task<string?> StopRecordingAsync()
    {
        if (recording is null) return LastRecordingPath;
        await recording.StopAsync().ConfigureAwait(false);await recordingFinalization.ConfigureAwait(false);
        return LastRecordingPath;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; StopForwardingAsync().GetAwaiter().GetResult();Stop(); StopRecordingAsync().GetAwaiter().GetResult();recording?.Dispose();
        CloseNativeClients();
        enumerator.Dispose(); transition.Dispose();
    }
}
