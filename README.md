# ElyCast

ElyCast is a Windows x64 IPTV and local-media player built with .NET 8 and WPF. It supports Xtream Codes accounts, M3U playlists, live TV, VOD, series and local audio/video, with several GPU rendering paths ranging from a compatible VLC fallback to the native ELYCORE pipeline.

## Highlights

- Xtream Codes and local/remote M3U sources
- Live TV, VOD, series, EPG, favourites and local media library
- mpv `gpu-next` playback with hardware decoding and high-quality scalers
- ELYCORE native renderer: libmpv render API, OpenGL/D3D11 zero-copy interop and DXGI presentation
- Optional NVIDIA RTX Video Super Resolution and NvOFFRUC frame interpolation
- ELYCOLOR image presets and custom mpv shader chains
- ELYSOUND+ equalization, dynamics and virtual-surround profiles
- Audio visualizer with FFT bands, particles and beat detection
- External Magpie integration for FSR, Anime4K and FSRCNNX scaling
- Encrypted local profiles and settings through Windows DPAPI

## Rendering backends

| Setting | Implementation | Intended use |
| --- | --- | --- |
| `elycore` | libmpv render API → OpenGL FBO → D3D11 → optional VSR/FRUC → DXGI | Best NVIDIA path and full ELYCORE features |
| `rtx-sdk` | mpv `gpu-next` in a native HWND with NVIDIA D3D11 video processing | RTX VSR without the custom renderer |
| `mpv-gpu` | mpv `gpu-next` in a native HWND | General high-quality GPU playback |
| VLC fallback | LibVLC decoded frames copied into WPF | Compatibility when mpv is unavailable |

Backends fail over automatically. ELYCORE falls back to the mpv HWND path when its native preflight fails; mpv falls back to VLC when `libmpv-2.dll` is unavailable.

## Requirements

- Windows 10 or Windows 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 with **Desktop development with C++**, or an equivalent MSVC/CMake toolchain
- 7-Zip for the in-app libmpv installer
- An NVIDIA RTX GPU and recent NVIDIA driver for RTX VSR and FRUC
- NVIDIA Optical Flow SDK 5.0.7 plus its NvOFFRUC runtime for native FRUC support

The NVIDIA SDK and runtime binaries are proprietary and are deliberately not stored in this repository.

## Build

The managed project requires `ElyFlow.Native.dll`, so build the native component first:

```powershell
cmake -S native/ElyFlow.Native -B native/ElyFlow.Native/build -A x64
cmake --build native/ElyFlow.Native/build --config Release
dotnet restore "ElyCast TV Player.csproj"
dotnet build "ElyCast TV Player.csproj" -c Release -p:Platform=x64
```

Alternatively:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release
```

Without the NVIDIA Optical Flow SDK, the native renderer still builds, but its FRUC adapter reports itself unavailable. To enable FRUC, extract the SDK so this header exists before configuring CMake:

```text
native/NVIDIA-Optical-Flow-SDK-5.0.7/
  Optical_Flow_SDK_5.0.7/NvOFFRUC/Interface/NvOFFRUC.h
```

At runtime ElyCast can install libmpv from its settings screen. Downloaded tools and shaders are stored outside the repository under `%APPDATA%\ElyCast\tools`.

## Repository layout

```text
App.xaml(.cs)                 Application startup, global resources and styles
MainWindow.xaml(.cs)          Main shell and feature orchestration
Models/                       Persisted settings, profiles and media models
Services/                     IPTV, persistence, theme, console and audio analysis
Services/Video/               Backend abstraction, mpv/VLC hosts and shader tooling
native/ElyFlow.Native/        C++20 ELYCORE renderer and NVIDIA FRUC adapter
Assets/                       Application-owned visual assets
scripts/                      Local build entry points
AGENTS.md                     Detailed architecture and engineering invariants
```

## Local data and privacy

ElyCast never ships IPTV credentials or playlists. Profiles, favourites, settings and the local library are written under `%APPDATA%\ElyCast` and protected with Windows DPAPI for the current user. Runtime logs intentionally avoid verbose libmpv output because stream URLs can contain credentials.

Do not commit `%APPDATA%\ElyCast`, M3U playlists, `.env` files, media, SDK archives, runtime DLLs or build output. The repository ignore rules cover these by default.

Use ElyCast only with media and IPTV services you are authorized to access.

## Architecture

See [AGENTS.md](AGENTS.md) for the complete component map, playback flows, threading rules, persistence model and safe change checklist.

## License

ElyCast source code is licensed under the [Mozilla Public License 2.0](LICENSE). Third-party components and downloaded runtimes remain subject to their respective licenses.
