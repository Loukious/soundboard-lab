# Validation

[GitHub Actions runs](https://github.com/Loukious/soundboard-lab/actions/workflows/build-release.yml) contain build/test results. Releases include `build-info.json` with the source commit and test scope, and `sha256.json` with file hashes. `SHA256SUMS.txt` verifies the ZIP.

## CI scope

`build.ps1 -CI` runs on Windows x64 without physical audio devices:

| Suite | Coverage |
|---|---|
| Native | Actual APO COM callbacks, aggregation/lifetime, gains, voice blocking, pause/stop, formats, queue backpressure, multiple consumers, vendor-effect wrapping, concurrency, cross-process shared memory. |
| Audio engine, offline | PCM conversion, loudness/normalization, editing, encoding/MP3 bitrates, gain isolation, cursor arithmetic, bounded forwarding queues, independent clip/live gain, clipping, pause and clip completion. Simulated application sessions verify process isolation, Mixer readback, and gain applied once. |
| Services | Library recovery/export, persistence, simulated hotkey dispatch/capture/conflicts, Auto Keys, pipes, toggle/hold forwarding, key/modifier release, repeat rejection, cancellation during startup. |
| WPF, layout-only | Actual hidden controls, search navigation, category initials/editor labels/count alignment, status highlights, percentage feedback, missing sounds/color swatches, hotkey editors, menus, Preferences layouts/dependencies, forwarding controls/default-off state, unshown device setup. No playback/capture. |
| Installer plan | Pure fixtures preserving/chaining/restoring vendor effects across mode changes, repair, invalid-backup rejection. Scripts are parsed without running setup. |
| Compiled device setup | In-memory installation, repair, mode changes, selection changes and restoration, including rollback after partial writes, backup failures and removal failures. Published helper/IPC runs a read-only configuration check without elevation. |
| GUI helper | Repeated PowerShell helper loading and refusal to run interactive tests without explicit opt-in. |
| Release workflow | Offline publishing fixtures check shared ZIPs, automatic version tags, annotated tags, existing-release preservation, prereleases, and rejection of corrupt or mismatched build artifacts. |

CI does not install an APO, restart Windows Audio, change volume, send OS input, or show windows. It skips physical audio endpoints and installed speech voices; successful CI does not claim live microphone/voice-app compatibility.

## Local checks

Default `build.ps1` additionally runs muted WASAPI playback, seeking/retriggering, desktop loopback recording, SAPI synthesis, and hidden playback controls. These tests do not change Windows endpoint/session volume or start physical microphone capture.

Both actual process-loopback capture modes were initialized locally, then stopped without microphone injection:

```powershell
dotnet run --project tests/engine -- --offline --forwarding-capture
```

This opt-in capture test targets the test process and writes no recording. The forwarding mixer/state machine/hotkey handling have deterministic tests. Forwarding uses the existing native v1 transport; no native ABI change is required.

## Explicit opt-ins

- `dotnet run --project tests/controls -- --layout-only --preference-devices`: additionally reads the actual Windows device snapshot on a worker thread. No streams, volume changes, capture or desktop input. Default hidden-control tests use device fixtures, including a blocked scan, retries and closing during discovery.
- `dotnet run --project tests/engine -- --offline --session-volume`: starts a silent stream in the test process, changes only that process's Mixer session, verifies notifications for intermediate volume levels, and verifies the endpoint volume and mute state remain unchanged. No window, input, audible samples, or capture.
- `-RunInteractiveTests`: opens/activates windows and sends desktop input.
- `-RunCommunicationsTests`: can trigger Windows ducking through a communications stream.
- `tests/live/`: physical capture verification with deliberate endpoint selection/setup.

These are excluded from public CI. Device repair, live mode switching, voice calibration, actual global input delivery, and forwarding into third-party voice apps remain environmental checks.

## Screenshots

`tools/screenshots` renders actual controls with generated audio and a private temporary library. A separate hidden native window uses the app window's title, icon and resize style to paint its caption buttons and frame through Windows nonclient painting, then combines that with the WPF content. This keeps frame capture from changing the controls' layout. It does not show windows, take focus, send input, or play audio. Screenshots are retained in `docs/screenshots/` and embedded in the README.
