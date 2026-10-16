# Speaker-volume artwork

`app/assets/headphones-empty.ico` contains the reference application's embedded
`ICON_HEADPHONES_EMPTY` icon group (resource type 14, language 1031), copied at
the user's request. Its 16, 24, 32 and 48 pixel images retain their original
alpha masks; the application theme supplies the foreground colour.

Source executable SHA-256:
`45c0b385e633d8eba46c79928914227779c33fba322181b799b367d192c73cf4`

Reconstructed ICO SHA-256:
`d472a91145fba40adb1db3bb4d5787b9e9ba22aeeba81a594fbdfc91f385eb57`

The original muted icon renderer at `0x140107580` uses this empty-headphones
artwork with a separately drawn red cross for these native sizes. The cross
renderer at `0x140106d70` uses a two-pixel pen and diagonals from `(0,0)` to
`(width-1,height-1)` and from `(width-1,0)` to `(0,height-1)`.

| Icon size | Cross offset | Cross size |
|---|---|---|
| 16 | 5, 7 | 6 × 9 |
| 24 | 8, 12 | 8 × 11 |
| 32 | 11, 17 | 11 × 14 |
| 48 | 15, 27 | 18 × 19 |

These are read-only PE extraction and static disassembly observations using
local helpers, not REA Evidence. No target code was loaded or executed for the
extraction. The app selects the matching icon frame for display DPI and retains
the native cross dimensions before scaling to its logical control size.
