using SoundboardLab.Audio;
using NAudio.Wave;
using System.Runtime.InteropServices;

var temp = Path.Combine(Path.GetTempPath(), "SoundboardLab.EngineTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
int assertions = 0;
try
{
    void Check(bool value, string name) { if (!value) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
    var bounded=new StereoFrameBuffer(3);bounded.Write([1,2,3,4,5,6]);bounded.Write([7,8],keepNewest:true);var buffered=new float[6];bounded.Read(buffered);
    Check(buffered.SequenceEqual(new float[]{3,4,5,6,7,8}),"live capture drops stale stereo frames instead of accumulating latency");
    bounded.Write([float.NaN,float.PositiveInfinity]);bounded.Read(buffered);Check(buffered.All(x=>x==0),"live capture sanitizes nonfinite samples and zero-fills underflow");
    var liveMixer=new ForwardingMixer();liveMixer.QueueClip([.2f,-.2f,.4f,-.4f],2);float[] mixed=[.1f,.1f,.1f,.1f];liveMixer.Render(mixed,.5f,2,false);
    Check(mixed.Zip(new float[]{.3f,.1f,.4f,0},(a,b)=>Math.Abs(a-b)<.0001).All(x=>x),"forwarding and soundboard clips mix with independent gains on both channels");
    Check(liveMixer.PendingClipFrames(2)==2&&liveMixer.PendingClipFrames(0)==0,"clip completion follows consumed mixer frames while live forwarding continues");
    liveMixer.QueueClip([.5f,-.5f],1);float[] pausedMix=[.2f,-.2f];liveMixer.Render(pausedMix,1,1,true);
    Check(pausedMix.SequenceEqual(new float[]{.2f,-.2f})&&liveMixer.PendingClipFrames(0)==1,"pausing a sound holds only its clip while live forwarding continues");
    liveMixer.ResetClip();float[] clippedMix=[2,-2];liveMixer.Render(clippedMix,1,1,false);
    Check(clippedMix.SequenceEqual(new float[]{1,-1})&&liveMixer.PendingClipFrames(0)==0,"stopping a clip clears its queue and forwarded peaks remain bounded");
    Check(NativeAudioClient.OpenFailureMessage(unchecked((int)0x800700AA)).Contains("Another Soundboard Lab instance")&&!NativeAudioClient.OpenFailureMessage(unchecked((int)0x800700AA)).Contains("Repair"),"busy microphone mixer identifies the other instance instead of suggesting device repair");
    var cursor = new PlaybackPositionSmoother();
    var cursorSamples = Enumerable.Range(0,121).Select(i=>cursor.Update(Math.Floor((i/60d)/.05)*.05,10,true,1,i/60d)).ToArray();
    Check(cursorSamples.Zip(cursorSamples.Skip(1),(a,b)=>b>=a).All(x=>x),"display cursor never moves backwards between quantized position updates");
    Check(cursorSamples.Zip(cursorSamples.Skip(1),(a,b)=>b-a>.001).Count(x=>x)>110,"display cursor advances on render frames between 50 ms source updates");
    Check(cursorSamples.Zip(Enumerable.Range(0,121),(value,i)=>Math.Abs(value-i/60d)).Max()<.075,"smooth cursor remains within 75 ms of the playback timeline");
    Check(cursor.Update(2,10,false,1,2.1)==2 && cursor.Update(2,10,false,1,2.6)==2,"display cursor freezes immediately on pause");
    Check(cursor.Update(.1,10,true,2,2.7)==.1,"backward seek resets the display cursor immediately");
    Check(cursor.Update(8,10,true,3,2.8)==8,"forward seek resets the display cursor immediately");
    for(int i=1;i<=120;i++) cursor.Update(8,10,true,3,2.8+i/60d);
    Check(cursor.Update(8,10,true,3,5)<8.076,"display cursor cannot drift ahead indefinitely when the source stalls");
    Check(cursor.Update(0,10,false,4,5.1)==0 && cursor.Update(0,10,false,5,5.2)==0,"stop and natural completion return the display cursor to the start");
    var sine = new float[48000 * 2 * 3];
    for (int f = 0; f < sine.Length / 2; f++) sine[f * 2] = sine[f * 2 + 1] = .1f * (float)Math.Sin(2 * Math.PI * 1000 * f / 48000);
    var measurement = AudioFiles.Analyze(sine);
    Check(Math.Abs(measurement.LoudnessLufs - (-20.0)) < .08, "R128 dual-channel 1 kHz sine at amplitude .1 is -20 LUFS");
    Check(double.IsNegativeInfinity(LoudnessMeter.Measure(new float[96000])), "digital silence is below absolute loudness gate");
    var quietTail = sine.Concat(new float[96000 * 3]).ToArray();
    Check(Math.Abs(LoudnessMeter.Measure(quietTail) - measurement.LoudnessLufs) < .4, "relative gate excludes long trailing silence");
    var normalized = AudioFiles.Edit(sine, new(TargetLufs: -14));
    Check(Math.Abs(LoudnessMeter.Measure(normalized) + 14) < .05, "LUFS normalization reaches requested target");
    var trimmed = AudioFiles.Edit(sine, new(Start: TimeSpan.FromSeconds(.5), End: TimeSpan.FromSeconds(1.5), FadeIn: TimeSpan.FromSeconds(.1), FadeOut: TimeSpan.FromSeconds(.1)));
    Check(trimmed.Length == 96000, "trim boundaries preserve stereo frame count");
    Check(trimmed[0] == 0 && trimmed[^1] == 0 && trimmed[^2] == 0, "fades reach silence at both boundaries");
    var amplified = AudioFiles.Edit(sine, new(Gain: 100));
    Check(amplified.Max(Math.Abs) <= .98001, "gain ceiling prevents exported sample clipping");
    var path = Path.Combine(temp, "source.wav"); AudioFiles.WriteWav(path, sine);
    var decoded = await AudioFiles.DecodeAsync(path);
    Check(decoded.Length == sine.Length && Math.Abs(decoded[70] - sine[70]) < .00004, "16-bit WAV export round trip preserves duration and samples");
    var playbackCache=new PlaybackCache();
    var cached=await playbackCache.GetAsync(path,PlaybackDestination.Both,new(TargetLufs:-23),CancellationToken.None);
    Check(ReferenceEquals(cached,await playbackCache.GetAsync(path,PlaybackDestination.Both,new(TargetLufs:-23),CancellationToken.None)),"repeated playback reuses decoded and normalized PCM");
    var adjusted=await playbackCache.GetAsync(path,PlaybackDestination.Both,new(FixedAdjustmentDb:-6),CancellationToken.None);
    Check(!ReferenceEquals(cached,adjusted)&&Math.Abs(adjusted.Microphone[70]-decoded[70]*(float)Math.Pow(10,-6d/20))<.00001,"routing changes invalidate prepared audio and apply the new gain");
    string activationPath=Path.Combine(temp,"activation.wav");AudioFiles.WriteWav(activationPath,new float[960]);
    var withActivation=await playbackCache.GetAsync(path,PlaybackDestination.Both,new(ActivationPath:activationPath),CancellationToken.None);
    AudioFiles.WriteWav(activationPath,new float[1920]);
    var changedActivation=await playbackCache.GetAsync(path,PlaybackDestination.Both,new(ActivationPath:activationPath),CancellationToken.None);
    Check(changedActivation.Microphone.Length==withActivation.Microphone.Length+960,"editing the activation sound invalidates cached routing");
    string editedPath=Path.Combine(temp,"cached-edit.wav");AudioFiles.WriteWav(editedPath,new float[960]);
    var beforeEdit=await playbackCache.GetAsync(editedPath,PlaybackDestination.Speakers,new(),CancellationToken.None);
    AudioFiles.WriteWav(editedPath,new float[1920]);
    Check((await playbackCache.GetAsync(editedPath,PlaybackDestination.Speakers,new(),CancellationToken.None)).Speakers.Length==1920&&beforeEdit.Speakers.Length==960,"saving over the same audio path invalidates cached PCM");
    using(var cancelledCache=new CancellationTokenSource()){
        cancelledCache.Cancel();try{await playbackCache.GetAsync(path,PlaybackDestination.Speakers,new(),cancelledCache.Token);Check(false,"cancelled cache request");}catch(OperationCanceledException){Check(true,"cancelled preparation cannot start or return cached playback");}
    }
    var activation=Enumerable.Repeat(.4f,960).ToArray();
    var routed=PlaybackPreparation.Prepare(sine,activation,PlaybackDestination.Both,25,sine.Select(x=>x*.5f).ToArray());
    Check(routed.Speakers.Length==sine.Length+960+2400&&routed.Speakers.Take(960).All(x=>x==0),"activation padding keeps speakers silent before the actual clip");
    Check(routed.Microphone.Take(960).All(x=>x==.4f)&&routed.Microphone.Skip(960).Take(2400).All(x=>x==0),"activation plays only on microphone before positive karaoke delay");
    Check(Math.Abs(routed.Speakers[1030]-sine[70])<.00001&&Math.Abs(routed.Microphone[3430]-sine[70]*.5f)<.00001,"microphone normalization and delay leave speaker samples unchanged");
    var negative=PlaybackPreparation.Prepare(sine,[],PlaybackDestination.Both,-25);
    Check(negative.Speakers.Take(2400).All(x=>x==0)&&negative.Microphone.Take(sine.Length).SequenceEqual(sine),"negative karaoke delay starts the microphone before the speakers");
    var asymmetric=sine.Select((value,i)=>i%2==0?value:value*.1f).ToArray();var balanced=DynamicNormalizer.Normalize(asymmetric,-23);
    Check(balanced.Where((_,i)=>i%2==0).Zip(balanced.Where((_,i)=>i%2==1),(left,right)=>Math.Abs(left-right)).Max()<.00001,"dynamic normalization independently balances unequal stereo channels");
    var isolated=balanced.Select((value,i)=>i%2==0?value:0).ToArray();Check(Math.Abs(LoudnessMeter.Measure(isolated)+23)<.08,"89 dB microphone reference maps to minus 23 LU per channel");
    Check(DynamicNormalizer.Normalize(new float[96000],-23).All(x=>x==0),"dynamic normalization leaves silence unchanged");
    var shortSound=DynamicNormalizer.Normalize(sine.Take(9600).ToArray(),-23);Check(shortSound.Length==9600&&shortSound.Max(Math.Abs)>.01,"short sounds normalize without requiring a complete 400 ms block");
    var padded=new float[9600].Concat(sine).Concat(new float[9600]).ToArray();var trimmedRecording=RecordingExport.Process(padded,new(TrimSilence:true));
    Check(Math.Abs(trimmedRecording.Length-sine.Length)<50,"recording silence trimming removes only leading and trailing quiet frames");
    var normalizedRecording=RecordingExport.Process(sine,new(Normalize:true,TargetDb:89));Check(Math.Abs(LoudnessMeter.Measure(normalizedRecording)+23)<.08,"recording 89 dB normalization uses the reference loudness scale");
    foreach(string format in new[]{"WAV","MP3","M4A"}){
        string source=Path.Combine(temp,"recording-"+format+".wav");File.Copy(path,source);string encoded=await RecordingExport.SaveAsync(source,new(format));var info=await AudioFiles.AnalyzeAsync(encoded);
        Check(Path.GetExtension(encoded).Equals("."+format,StringComparison.OrdinalIgnoreCase)&&Math.Abs(info.Duration.TotalSeconds-3)<.12&&info.Peak>.05,"recording export writes decodable "+format+" audio");
    }
    float localGain=.25f;float[] volumeInput=[.4f,-.4f,.8f,-.8f];
    var volumeBuffer=new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000,2));
    var localVolume=new PlaybackGainProvider(volumeBuffer,()=>localGain);
    float[] ReadLocalVolume(){byte[] bytes=MemoryMarshal.AsBytes(volumeInput.AsSpan()).ToArray();volumeBuffer.AddSamples(bytes,0,bytes.Length);var output=new byte[bytes.Length];localVolume.Read(output,0,output.Length);return MemoryMarshal.Cast<byte,float>(output.AsSpan()).ToArray();}
    Check(ReadLocalVolume().SequenceEqual(volumeInput.Select(x=>x*.25f)),"speaker volume scales only the player's outgoing PCM samples");
    Check(volumeInput.SequenceEqual(new float[]{.4f,-.4f,.8f,-.8f}),"volume changes leave source audio unchanged");
    localGain=.75f;Check(ReadLocalVolume().SequenceEqual(volumeInput.Select(x=>x*.75f)),"live volume changes update the same player without touching Windows volume APIs");
    localGain=0;Check(ReadLocalVolume().All(x=>x==0),"muting this player silences only its own buffer");
    var independentBuffer=new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000,2));var independentVolume=new PlaybackGainProvider(independentBuffer,()=>1);byte[] independentBytes=MemoryMarshal.AsBytes(volumeInput.AsSpan()).ToArray();independentBuffer.AddSamples(independentBytes,0,independentBytes.Length);var independentOutput=new byte[independentBytes.Length];independentVolume.Read(independentOutput,0,independentOutput.Length);
    Check(MemoryMarshal.Cast<byte,float>(independentOutput.AsSpan()).SequenceEqual(volumeInput),"a separate audio player keeps its original volume when this player is muted");
    Check(localVolume.ConsumedFrames==6&&independentVolume.ConsumedFrames==2,"per-player frame counters stay independent across volume changes");
    localGain=1;localVolume.Restart([.2f,-.2f,.6f,-.6f],0,2);
    var primedBytes=new byte[16];localVolume.Read(primedBytes,0,primedBytes.Length);
    Check(MemoryMarshal.Cast<byte,float>(primedBytes).SequenceEqual(new float[]{.2f,-.2f,.6f,-.6f}),"the first render read after restart contains the new clip without a refill-worker wait");
    Check(localVolume.ConsumedFrames==2&&localVolume.FirstReadTimestamp>0,"restart resets the playback counter and timestamps the actual first PCM read");
    localVolume.Restart([.2f,-.2f,.6f,-.6f],1,1);var seekBytes=new byte[8];localVolume.Read(seekBytes,0,seekBytes.Length);
    Check(MemoryMarshal.Cast<byte,float>(seekBytes).SequenceEqual(new float[]{.6f,-.6f}),"priming a seek supplies the requested stereo frame without old queued samples");
    localVolume.Reset();Check(localVolume.ConsumedFrames==0&&localVolume.FirstReadTimestamp==0&&volumeBuffer.BufferedBytes==0,"reset clears the PCM queue, timeline and first-read timestamp together");
    foreach(int bitrate in new[]{96000,112000,128000,160000,192000,224000,256000,320000}){
        string bitrateSource=Path.Combine(temp,"bitrate-"+bitrate+".wav");File.Copy(path,bitrateSource);
        string encoded=await RecordingExport.SaveAsync(bitrateSource,new("MP3",bitrate));using var reader=new Mp3FileReader(encoded);
        Check(reader.ReadNextFrame()?.BitRate==bitrate,"MP3 export encodes the selected "+bitrate/1000+" Kbps bitrate");
    }
    if(args.Contains("--communications-stream")){
        await ApplicationVolumeDucker.ProbeAsync();Check(true,"opt-in Windows communications stream initializes");
        using var communication=new ApplicationVolumeDucker();await communication.Started.WaitAsync(TimeSpan.FromSeconds(3));communication.Update(1);Check(true,"opt-in communications stream starts and tears down through actual WASAPI rendering");
    }else Console.WriteLine("SKIP communications-stream tests: these can duck other applications and require --communications-stream.");
    var editDocument=new EditorDocument(sine){Start=1,End=2};
    editDocument.Crop();Check(editDocument.Duration==1&&editDocument.Modified&&editDocument.Samples.SequenceEqual(sine[96000..192000]),"editor crop preserves the exact selected stereo frames");
    editDocument.Undo();Check(editDocument.Duration==3&&!editDocument.Modified&&editDocument.SelectionStart==1&&editDocument.SelectionEnd==2,"editor undo restores audio, selection and saved state");
    editDocument.Redo();editDocument.MarkSaved();Check(!editDocument.Modified&&editDocument.CanUndo,"editor save marks current revision without clearing undo history");
    editDocument.Undo();Check(editDocument.Modified,"undoing a saved edit correctly marks the document modified");
    editDocument.Cut();Check(editDocument.Duration==2&&editDocument.Samples.SequenceEqual(sine[..96000].Concat(sine[192000..]))&&!editDocument.CanRedo,"editor cut joins surrounding audio and clears the abandoned redo branch");
    var stereoDocument=new EditorDocument([.1f,.2f,.3f,.4f,.5f,.6f]);stereoDocument.Effect("reverse");
    Check(stereoDocument.Samples.SequenceEqual(new float[]{.5f,.6f,.3f,.4f,.1f,.2f}),"editor reverse preserves left and right channel pairs");
    var fadeDocument=new EditorDocument(Enumerable.Repeat(.5f,96000).ToArray()){Start=.25,End=.75};fadeDocument.Effect("fade-in");
    Check(fadeDocument.Samples[23998]==.5f&&fadeDocument.Samples[24000]==0&&fadeDocument.Samples[72000]==.5f,"editor fade only changes the selected range");
    var output = Path.Combine(temp, "edited.wav");
    var result = await AudioFiles.ExportAsync(path, output, new(Start: TimeSpan.FromSeconds(1), End: TimeSpan.FromSeconds(2), TargetLufs: -18));
    Check(result.Duration == TimeSpan.FromSeconds(1) && File.Exists(output), "editor writes valid trimmed normalized WAV");
    var monoPath = Path.Combine(temp, "mono22050.wav");
    using (var writer = new WaveFileWriter(monoPath, new WaveFormat(22050, 16, 1)))
    { var mono = new float[22050]; for (int i = 0; i < mono.Length; i++) mono[i] = .2f * (float)Math.Sin(2 * Math.PI * 440 * i / 22050); writer.WriteSamples(mono, 0, mono.Length); }
    var resampled = await AudioFiles.DecodeAsync(monoPath);
    Check(Math.Abs(resampled.Length - 96000) < 100, "22.05 kHz mono decodes to canonical 48 kHz stereo");
    Check(resampled.Where((_,i) => i % 2 == 0).SequenceEqual(resampled.Where((_,i) => i % 2 == 1)), "mono conversion duplicates both channels");
    if (Environment.GetEnvironmentVariable("SB_CODEC_FIXTURES") is { } fixtures && Directory.Exists(fixtures))
        foreach (string fixture in Directory.GetFiles(fixtures))
        {
            var clip = await AudioFiles.AnalyzeAsync(fixture);
            Check(Math.Abs(clip.Duration.TotalSeconds - 1) < .08 && clip.Peak is > .02f and < .5f,
                "real codec fixture decodes: " + Path.GetExtension(fixture));
        }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await AudioFiles.DecodeAsync(path, cancellation.Token); Check(false, "cancelled decode"); } catch (OperationCanceledException) { Check(true, "cancelled decode propagates cancellation"); }
    if(args.Contains("--forwarding-capture")){
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach(bool wholePc in new[]{false,true}){
            using var captureSource=new ProcessLoopbackSource((uint)Environment.ProcessId,wholePc);await captureSource.StartAsync(timeout.Token);await Task.Delay(100);
            float[] captured=new float[960];captureSource.Read(captured);Check(captureSource.Failure is null,"actual process-loopback capture initializes and stops in "+(wholePc?"exclude-self":"include-self")+" mode without microphone injection");
        }
    }
    if(!args.Contains("--offline")){
    var voices = SpeechService.GetVoices(); Check(voices.Count > 0, "Windows SAPI enumerates installed voices");
    var speech = Path.Combine(temp, "speech.wav");
    await SpeechService.SynthesizeAsync("Soundboard audio engine test.", speech, voices[0]);
    Check((await AudioFiles.AnalyzeAsync(speech)).Duration > TimeSpan.Zero, "SAPI creates decodable speech WAV");
    using var engine = new AudioService();
    Check(engine.Devices.Count > 0, "stable Windows endpoint IDs enumerate");
    await engine.StopAsync(); await engine.StopRecordingAsync();
    Check(!engine.IsPlaying && !engine.IsRecording, "idle stop and recording lifecycle are safe");
    string? playbackError = null;
    engine.Error += (_, e) => playbackError = e.Message;
    engine.SpeakerVolume = 0; // Exercise the physical render path without playing audible test tones.
    await engine.PlayAsync(path, PlaybackDestination.Speakers);
    await Task.Delay(180);
    Check(engine.IsPlaying && engine.Position > TimeSpan.Zero, "speaker playback consumes frames and advances position");
    engine.Pause(); var pausedAt = engine.Position;
    await Task.Delay(100);
    Check(engine.IsPaused && Math.Abs((engine.Position - pausedAt).TotalMilliseconds) < 25, "pause holds speaker timeline");
    engine.Seek(TimeSpan.FromSeconds(2)); await Task.Delay(40);
    Check(Math.Abs((engine.Position - TimeSpan.FromSeconds(2)).TotalMilliseconds) < 25, "paused seek flushes buffers and updates position");
    engine.Resume(); await Task.Delay(150);
    Check(engine.IsPlaying && engine.Position > TimeSpan.FromSeconds(2.05), "resume continues from seek position");
    Check(engine.TryReplay(),"replaying an active sound accepts a zero-position seek");await Task.Delay(60);
    Check(engine.IsPlaying&&engine.Position<TimeSpan.FromSeconds(.2),"replay moves the existing active timeline back to the start");
    engine.Pause();Check(!engine.TryReplay()&&engine.IsPaused,"the active-replay shortcut leaves paused playback to the normal resume policy");engine.Resume();
    var playerField=typeof(AudioService).GetField("player",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    object? renderStream=playerField.GetValue(engine);bool reused=true;
    var clientField=typeof(WasapiOut).GetField("audioClient",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    var renderClient=(NAudio.CoreAudioApi.AudioClient)clientField.GetValue(renderStream)!;
    Console.WriteLine($"Active shared WASAPI render buffer: {renderClient.BufferSize*1000d/48000:F2} ms ({renderClient.BufferSize} frames); device period {renderClient.DefaultDevicePeriod/10000d:F2} ms.");
    var firstReadTimings=new List<double>();
    var counterField=typeof(AudioService).GetField("counter",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    for(int trigger=0;trigger<10;trigger++){
        long requested=System.Diagnostics.Stopwatch.GetTimestamp();await engine.PlayAsync(path,PlaybackDestination.Speakers);
        var provider=(PlaybackGainProvider)counterField.GetValue(engine)!;
        for(int attempt=0;attempt<200&&provider.FirstReadTimestamp==0;attempt++)await Task.Delay(1);
        if(provider.FirstReadTimestamp==0)throw new Exception("Restarted audio did not reach the render provider.");
        firstReadTimings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(requested,provider.FirstReadTimestamp).TotalMilliseconds);
    }
    Console.WriteLine($"Trigger to first render-provider read: median {firstReadTimings.Order().ElementAt(firstReadTimings.Count/2):F2} ms; maximum {firstReadTimings.Max():F2} ms (muted; excludes hardware/acoustic latency).");
    Check(firstReadTimings.All(x=>double.IsFinite(x)&&x>=0),"each muted retrigger reaches the actual PCM render callback after its request");
    var timings=new List<double>();
    for(int trigger=0;trigger<20;trigger++){
        var watch=System.Diagnostics.Stopwatch.StartNew();await engine.PlayAsync(path,PlaybackDestination.Speakers);timings.Add(watch.Elapsed.TotalMilliseconds);
        reused&=ReferenceEquals(renderStream,playerField.GetValue(engine))&&engine.IsPlaying&&engine.Position.TotalMilliseconds<50;
        await Task.Delay(10);
    }
    Check(reused,"twenty rapid triggers restart from zero on the same render stream without a device cooldown");
    Console.WriteLine($"Rapid retrigger latency: median {timings.Order().ElementAt(timings.Count/2):F2} ms; maximum {timings.Max():F2} ms (muted speakers).");
    await Task.Delay(100);Check(engine.IsPlaying&&engine.Position.TotalMilliseconds>50,"the reused playback worker continues advancing after rapid restarts");
    engine.Pause();await engine.PlayAsync(path,PlaybackDestination.Speakers);
    Check(engine.IsPlaying&&!engine.IsPaused&&engine.Position.TotalMilliseconds<50&&ReferenceEquals(renderStream,playerField.GetValue(engine)),"retriggering a paused sound resumes from the start without rebuilding the output");
    await engine.PlayAsync(output,PlaybackDestination.Speakers);
    Check(ReferenceEquals(renderStream,playerField.GetValue(engine))&&engine.Duration<TimeSpan.FromSeconds(2),"selecting a different sound replaces the PCM while preserving the active output stream");
    await engine.StopAsync();
    Check(playbackError is null && !engine.IsPlaying && engine.Position == TimeSpan.Zero, "stop clears speaker buffers and timeline without render errors");
    Check(!engine.TryReplay(),"the seek-only replay shortcut cannot restart an already stopped stream");
    await engine.PlayAsync(output, PlaybackDestination.Speakers);
    await Task.Delay(1250);
    Check(!engine.IsPlaying && engine.Position == TimeSpan.Zero, "natural speaker completion drains final buffer and resets position");
    var desktopRecording = Path.Combine(temp, "desktop-recording.wav");
    engine.StartRecording(RecordingSource.Desktop, desktopRecording, TimeSpan.FromSeconds(.5));
    Check(engine.IsRecording, "desktop loopback recording starts");
    await Task.Delay(800);
    await engine.StopRecordingAsync();
    var recordingAnalysis = await AudioFiles.AnalyzeAsync(desktopRecording);
    Check(!engine.IsRecording && Math.Abs((recordingAnalysis.Duration - TimeSpan.FromSeconds(.5)).TotalMilliseconds) < 2,
        "desktop recording finalizes exact maximum duration and WAV header");
    Check(playbackError is null && engine.LastRecordingPath == desktopRecording, "recording completion reports saved path without capture error");
    var status = engine.GetInjectionStatus();
    if (!status.Connected)
    {
        try { await engine.PlayAsync(path, PlaybackDestination.Microphone); Check(false, "missing injection rejected"); }
        catch (InvalidOperationException) { Check(!engine.IsPlaying, "unavailable microphone APO fails visibly instead of simulated playback"); }
    }
    Console.WriteLine($"Injection availability: {status.Connected}; {status.Message}");
    }else Console.WriteLine("SKIP hardware audio and SAPI checks: offline mode does not require recording/render endpoints or installed voices.");
    Console.WriteLine($"PASS {assertions} assertions. No microphone capture was started.");
}
finally { Directory.Delete(temp, true); }
