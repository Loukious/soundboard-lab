# Third-party notices

Soundboard Lab uses the following packages. Their license notices are retained in [docs/licenses](docs/licenses/) and included in release archives.

| Component | Version | Source / notice |
|---|---|---|
| NAudio and NAudio.Asio/Core/Midi/Wasapi/WinForms/WinMM | 2.2.1 | [Source](https://github.com/naudio/NAudio), [MIT notice](docs/licenses/NAudio.txt) |
| NAudio.Vorbis | 1.5.0 | [Source](https://github.com/naudio/Vorbis), [MIT notice](docs/licenses/NAudio.Vorbis.txt) |
| NVorbis | 0.10.4 | [Source](https://github.com/NVorbis/NVorbis), [MIT notice](docs/licenses/NVorbis.txt) |
| Concentus | 2.2.2 | [Source](https://github.com/lostromb/concentus), [copyright/BSD-style notice](docs/licenses/Concentus.txt) |
| Concentus.Oggfile | 1.0.7 | [Source](https://github.com/lostromb/concentus.oggfile), [MIT notice](docs/licenses/Concentus.Oggfile.txt) |
| System.Speech | 10.0.0 | [Source](https://github.com/dotnet/dotnet), [MIT notice](docs/licenses/dotnet.txt), [third-party notices](docs/licenses/System.Speech-third-party.txt) |
| Self-contained .NET runtime | Selected .NET 10 servicing release | [Source](https://github.com/dotnet/runtime), [MIT notice](docs/licenses/dotnet.txt), [third-party notices](docs/licenses/dotnet-runtime-third-party.txt) |

The project uses Windows WASAPI, COM/APO and SAPI APIs. Public releases contain no original application executable, DLL, demo audio, notification recordings or external translation catalog. Notification and activation sounds are generated from this project's source at first launch. README screenshots use generated audio and a demonstration library.

The speaker-volume headphones artwork is copied from the reference application's embedded icon resource. Its source identity and rendering details are recorded in [Speaker-volume artwork](docs/reference-volume-icon.md).

Special Hotkeys also uses the reference application's embedded action icons, documented in [Special Hotkeys reference](docs/reference-special-hotkeys.md).
