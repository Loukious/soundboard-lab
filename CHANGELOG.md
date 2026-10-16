# Changelog

Numbered releases use Semantic Versioning: `MAJOR.MINOR.PATCH`. Before 1.0,
minor versions introduce features and patch versions contain fixes. Release tags
use `v` followed by the app version; prereleases use a suffix such as `-rc.1`.

## [0.1.0] - 2026-10-16

First numbered Windows x64 development release.

### Included

- Sound libraries, categories, global sound and special-action hotkeys, and a configurable hotbar.
- Sound editor, recorder, volume normalization, preferences, and remote controls.
- Microphone mixing through the existing capture endpoint, with device setup and restore tools.
- Foreground-app and whole-PC audio forwarding, with toggle and push-to-forward controls and hotkeys.
- README, app screenshots, and automated Windows builds and releases.

### Fixed

- Category and sound-list scrollbars now follow the app theme, including hover and drag states and the horizontal sound-list scrollbar.
- Load sound list defaults to SPL files; JSON libraries remain selectable.
- Foreground forwarding stops when the captured app exits.
- Forwarding volume responds proportionally to clicks, with bounded adjustment steps.
- Recorder controls share aligned columns across Windows layouts.

### Versioning

- App information, diagnostic reports, build metadata, release tags, and ZIP filenames use the same version.
- Tagged builds reject a release tag that differs from the app version.

### Validation

CI checks native callbacks, offline audio processing, services, and hidden WPF
layouts. Live microphone and voice-app forwarding compatibility require separate
checks. The Windows application and native mixer are unsigned.
