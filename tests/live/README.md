# Live microphone integration

These probes open the selected Windows capture endpoint. The recorder and tone
producer are separate processes. No speaker output or extra audio device is used.
Only quarter-second signal measurements are saved; microphone samples stay in
memory. A successful test requires an active hosted consumer, four seconds of
997 Hz audio in the recorder, and the tone ending after playback.

Build with `dotnet build LiveMicrophone.csproj -c Release`. Run the executable with
`devices endpoints.json` to identify the Windows communications default. Installing
the component and restarting Windows Audio require administrator access. Restarting
Windows Audio interrupts playback and recording in all applications and requires
the user's explicit choice.

After setup and audio-engine reload, run
`Test-LiveMicrophone.ps1 -EndpointId '<full endpoint ID>' -Label reloaded`.
The test emits no raw microphone recording. Reports are local development evidence,
not an assertion that every voice application or physical device is compatible.

`Invoke-ElevatedSetup.ps1` records install/restore/restart/diagnose transcripts and
results. Its EndpointFile must be the devices report identifying the intended
default. It does not request elevation itself; the operator must launch it using
Windows administrator consent. It never changes signing or security policy.
