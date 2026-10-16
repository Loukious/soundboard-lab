# Special Hotkeys reference

The reference application was inspected with the user's permission to interact
with its Preferences window. All 44 original actions were reviewed by scrolling
the action list. Set hotkey and Edit hotkey were captured separately. A temporary
draft shortcut used during inspection was removed afterward; Preferences was
not applied and the audio service was not restarted.

The original action table has an Action column, a narrow unnamed value column,
and a Hotkey column. Values such as `5s` and `20%` are separate from the action
label. The application now uses this arrangement.

Playback, random playback, recording, volume, play-mode, Auto Keys, Hotkeys and
TTS actions use their reference icons. Recording retains the original red/blue
artwork, and disabled Auto Keys/Hotkeys have the red disabled overlay. Selection,
category selection, seek jumps, play-mode switching, toggle actions and hotbar
page selection retain the reference's blank icon positions. The two forwarding
actions are this project's additions and retain their own arrow icon.

`app/assets/special-hotkeys/` contains the corresponding PE icon groups copied
from the same local reference executable as the headphones icons. Source SHA-256:
`45c0b385e633d8eba46c79928914227779c33fba322181b799b367d192c73cf4`.
The icon factory at `0x1400daad0` references these named resources. Extraction
preserves the original resolution variants. Normal glyphs use their alpha masks
with the Preferences colour; recording glyphs retain their original colours.

The empty Set hotkey window is 266 logical pixels wide. Edit hotkey is 294 pixels
wide and has an inline X to remove the assignment. Both use the Press a key group,
a white capture field with a blue focus underline, and OK/Cancel at the lower
right. These measurements come from the captured windows at 150% display scaling.

Special-action conflicts offer `Hotkey is already in use. Overwrite?` as requested.
Acceptance clears the previous action in the Preferences draft. Declining or
cancelling preserves it; live assignments remain unchanged until Apply. Ordinary
sound assignments retain their existing special-hotkey protection.

PE extraction and Capstone observations are ordinary local analysis, not REA
Evidence. No original code was loaded for extraction. Hidden-control checks
exercise all 46 action icon states, the three table columns, dialog layout,
overwrite acceptance/decline, draft isolation and the existing capture behavior.
