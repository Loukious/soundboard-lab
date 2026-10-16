# Changelog

Numbered releases use Semantic Versioning: `MAJOR.MINOR.PATCH`. Before 1.0,
minor versions introduce features and patch versions contain fixes. Release tags
use `v` followed by the app version; prereleases use a suffix such as `-rc.1`.

## [Unreleased]

## [0.1.1] - 2026-10-16

### Fixed

- Restore Special Hotkeys action icons and the separate action/value/hotkey columns from the reference.
- Allow confirmed overwrites between special actions within the Preferences draft and match the compact Set/Edit hotkey capture layout.
- Keep Preferences responsive during playback by discovering devices once in the background and creating preview audio only on demand.
- Change the headphones artwork at the original volume bands: muted at 0%, low at 1–33%, medium at 34–66%, and high at 67–100%.
- Keep held volume clicks accurate before the thumb layout updates, avoiding a jump to zero.
- Use the original headphones artwork and native mute-cross placement at each display DPI.
- Match the compact right-aligned search strip and its bordered buttons.
- Put grey volume percentages beneath the pointer and keep dragging outside the slider until release.
- Update the app slider during Windows Mixer changes through Core Audio notifications, without feedback writes.
- Show a red cross inside the headphones when speaker volume is zero.
- Restore incremental search with previous/next buttons, keyboard navigation, numeric index lookup, and reset/close.
- Show live percentages while adjusting speaker and microphone volume sliders.
- Synchronize speaker volume with this process's Windows Mixer sessions, without applying speaker gain twice.
- Generate category icons from their first letter, including legacy default folder icons.
- Prevent the Generated icon option from being cropped in Edit category.
- Draw enabled status highlights above and below the H and A icons.
- Import the SPL home category without adding a second My Sounds category, and normalize previously imported copies.
- Align the category count header with the visible count column when a scrollbar appears.
- Grey and strike through missing sounds after playback fails; restore the numbered sound color palette, swatches, and color tool.

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
