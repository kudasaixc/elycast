<p align="center">
  <img src="docs/images/elycast-logo2.png" alt="ElyCast, an IPTV and media player for Windows" width="100%">
</p>

<p align="center">
  <strong>Live TV, movies, series, and personal media in one Windows player.</strong>
</p>

<p align="center">
  ElyCast connects to IPTV services and M3U playlists, organizes local media, and uses the GPU for sharper video, smoother motion, and live audio processing.
</p>

<p align="center">
  <a href="https://github.com/kudasaixc/elycast/actions/workflows/build.yml"><img alt="Build" src="https://github.com/kudasaixc/elycast/actions/workflows/build.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/kudasaixc/elycast/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/kudasaixc/elycast/actions/workflows/codeql.yml/badge.svg?branch=main"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast"><img alt="Quality Gate Status" src="https://sonarcloud.io/api/project_badges/measure?project=kudasaixc_elycast&metric=alert_status"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast"><img alt="Security Rating" src="https://sonarcloud.io/api/project_badges/measure?project=kudasaixc_elycast&metric=security_rating"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=kudasaixc_elycast"><img alt="Maintainability Rating" src="https://sonarcloud.io/api/project_badges/measure?project=kudasaixc_elycast&metric=sqale_rating"></a>
</p>

<p align="center">
  <img alt="Windows x64" src="https://img.shields.io/badge/Windows-x64-0078D4?logo=windows11&logoColor=white">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="ElyCast 1.3" src="https://img.shields.io/badge/ElyCast-1.3-27c4e8">
  <a href="LICENSE"><img alt="License MPL-2.0" src="https://img.shields.io/github/license/kudasaixc/elycast?color=2ea44f"></a>
  <a href="https://github.com/kudasaixc/elycast"><img alt="Open source" src="https://img.shields.io/badge/Open%20Source-GitHub-181717?logo=github"></a>
</p>

---

## Overview

ElyCast combines live IPTV, video on demand, and local music and video in a single Windows interface. Playback uses libmpv by default, while a native D3D11 renderer can add GPU upscaling, frame interpolation, shaders, and live audio processing.

The first-run wizard detects the CPU and GPU, downloads missing dependencies, benchmarks the system, and recommends a suitable playback engine. English is the default language. English and French can be switched instantly from onboarding or Settings, and the choice is saved in the protected application state.

> ElyCast does not provide subscriptions, channels, or media. You are responsible for using services and content that you are legally allowed to access.

## Features

### Sources and libraries

- IPTV through Xtream Codes or M3U playlists, with live categories and EPG.
- Movies, series, seasons, and episodes in the same navigation model.
- Recursive local video and music import, plus multi-file import and drag-and-drop.
- Album, artist, genre, playlist, queue, shuffle, repeat, and artwork support.
- Favorites, resume, categories, and search across remote and local content.
- Metadata editor for title, artist, album, genre, track number, disc number, and artwork.
- Selectable audio and subtitle tracks.
- Windows System Media Transport Controls for local audio only.
- Instant English and French interface switching with persisted selection.

### Video

- **ELYCORE**: native libmpv to OpenGL to shared D3D11 texture to DXGI presentation.
- **RTX Video Super Resolution** for compatible NVIDIA RTX GPUs.
- **ELYFLOW** frame interpolation through NVIDIA Optical Flow and FRUC.
- **ELYCOLOR** live image profiles for color, contrast, gamma, hue, and shader chains.
- GLSL shader and Magpie upscaling support.

### Audio

- **ELYSOUND+**: a stable, live-controlled libmpv audio graph with real dB equalization, preamp, gentle compression, anti-clipping limiter, and stereo width.

### Audio visualizer

- Real-time FFT bars, particles, beat shockwaves, artwork-derived palettes, animated backgrounds, blur, and dimming.
- **AudioCore+** renders the same canonical scene through D3D11 on the GPU when ELYCORE is active. It falls back to the classic WPF renderer when the native path is unavailable.

### Performance intelligence

- First-run hardware detection, benchmark, engine recommendation, and compatibility tests.
- **ELYSMART** measures the machine, explains recommendations, tracks sustained player health, exports diagnostics, and keeps detected, estimated, and measured facts distinct.

## Video backends

ElyCast automatically falls back to a compatible backend when the requested technology is unavailable.

| Backend | Pipeline | Best use |
| --- | --- | --- |
| **ELYCORE** | libmpv + OpenGL/D3D11 + VSR/FRUC + DXGI | Best available NVIDIA pipeline |
| **RTX SDK** | mpv `gpu-next` + NVIDIA D3D11 video processing | RTX VSR without ELYCORE |
| **mpv GPU** | mpv `gpu-next`, hardware decode, advanced scalers | General GPU playback |
| **VLC** | LibVLC decode to a WPF surface | Compatibility fallback |

```text
libmpv
  -> OpenGL render
  -> shared D3D11 texture without CPU readback
  -> RTX Video Super Resolution (optional)
  -> NVIDIA FRUC / ELYFLOW (optional)
  -> DXGI presentation
```

## Supported formats

- Video: formats supported by mpv or VLC, including MKV, MP4, TS, and HLS.
- Audio: MP3, FLAC, WAV, AAC, M4A, OGG, Opus, WMA, ALAC, AIFF, and APE.

## Installation

Download the latest archive from [Releases](https://github.com/kudasaixc/elycast/releases), extract it, and run `ElyCast.exe`.

Requirements:

- 64-bit Windows 10 or Windows 11.
- [.NET Desktop Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0) for framework-dependent archives.
- 7-Zip for first-run libmpv installation.
- A recent NVIDIA RTX GPU and driver for RTX VSR and ELYFLOW/FRUC.

Downloaded dependencies are installed under `%APPDATA%\ElyCast\tools`, never in the repository.

## Build from source

Build the native component before the WPF application:

```powershell
cmake -S native/ElyFlow.Native -B native/ElyFlow.Native/build -A x64
cmake --build native/ElyFlow.Native/build --config Release
dotnet restore "ElyCast TV Player.csproj"
dotnet build "ElyCast TV Player.csproj" -c Release -p:Platform=x64
```

Or use the build script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release
```

Build requirements:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio with Desktop development with C++ and CMake
- NVIDIA Optical Flow SDK 5.0.7 and NvOFFRUC runtime for optional FRUC support

Without the NVIDIA SDK, ELYCORE still builds and runs, but its FRUC adapter is unavailable. Proprietary SDK archives and runtime DLLs must not be committed.

## Repository layout

```text
App.xaml(.cs)                 Startup, global resources, theme, and language initialization
MainWindow.xaml               Main shell, player, settings, and overlays
MainWindow.*.cs               Domain-specific window coordination
Models/                       Settings, profiles, and media models
Services/Localization*.cs     Fast English/French runtime localization
Services/Audio/               Metadata, FFT analysis, artwork, and ELYSOUND+
Services/ElySmart/            Benchmarks, scoring, recommendations, and monitoring
Services/Video/               mpv/VLC backends, HWND hosting, shaders, and native interop
native/ElyFlow.Native/        ELYCORE C++20 renderer and AudioCore+ scene
Assets/                       Application visuals
scripts/                      Reproducible build commands
tests/                        Playback regressions and RTX VSR probes
AGENTS.md                     Architecture map and engineering invariants
```

Read [`AGENTS.md`](AGENTS.md) before making architectural changes.

## Quality, security, and privacy

- Windows x64 builds run on every push and pull request.
- CodeQL covers C# and C++.
- SonarQube Cloud tracks quality, security, and maintainability.
- Profiles and application state are protected with Windows DPAPI and written atomically.
- Logs intentionally avoid complete stream URLs because they may contain credentials.
- User media, playlists, profiles, secrets, downloaded tools, and proprietary SDK files must not be committed.

Local profiles, favorites, settings, and library entries stay under `%APPDATA%\ElyCast`.

## Contributing

1. Read [`AGENTS.md`](AGENTS.md) before substantial changes.
2. Preserve fallbacks when mpv, NVIDIA, FRUC, shaders, or Magpie are absent.
3. Build the native renderer, then the Release x64 application.
4. Test resizing, fullscreen, HUD, language switching, and focus behavior for renderer or UI changes.
5. Never publish IPTV credentials or complete stream URLs.

## License

ElyCast is distributed under the [Mozilla Public License 2.0](LICENSE). Third-party libraries, SDKs, and runtimes remain subject to their own licenses.

<p align="center">
  <strong>ElyCast: your content, your hardware, your experience.</strong>
</p>
