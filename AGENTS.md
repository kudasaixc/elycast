# ElyCast engineering guide

Read this file before exploring the repository. It is the compact architectural source of truth for future work. Update it when a change alters component ownership, a data flow, a backend contract, persistence, threading or build requirements.

## 1. Product and platform

ElyCast is a Windows-only x64 WPF application targeting `net8.0-windows`. It plays Xtream/M3U IPTV and local media. The managed application owns navigation, state and controls; playback is behind `IVideoBackend`; ELYCORE adds a C++20 GPU renderer. There is no separate audio backend: the chosen media backend decodes/plays audio, while NAudio independently analyzes local audio for the WPF visualizer.

Primary technologies:

- WPF/XAML for UI and composition.
- libmpv through LibMPVSharp plus direct P/Invoke for reliable property access.
- LibVLCSharp as the compatibility fallback.
- NAudio for local-file FFT/beat analysis, not primary playback.
- C++20, libmpv render API, OpenGL, WGL/D3D11 interop, D3D11 video processing and DXGI in ELYCORE.
- NVIDIA NvOFFRUC loaded dynamically when its proprietary runtime is present.

Terminology matters:

- **ELYCORE** is the user-facing custom renderer/backend (`VideoBackend = "elycore"`).
- **ELYFLOW** is the frame-interpolation feature and the historical backend name. Persisted `elyflow` values migrate to `elycore`.
- Native filenames and C exports remain `ElyFlow.Native` / `ElyFlowRenderer_*` for ABI compatibility.

## 2. System map

```text
App startup
  App.xaml.cs
    ├─ StateStore.Load → ThemeManager.Apply
    ├─ DebugConsole boot/commands
    └─ MainWindow
         ├─ IptvService → Xtream HTTP API or M3U parser
         ├─ ProfileStore / StateStore → %APPDATA%\ElyCast (DPAPI)
         ├─ VideoBackendFactory → IVideoBackend
         │    ├─ MpvHwndBackend (mpv-gpu / rtx-sdk / elycore)
         │    ├─ MpvBackend (legacy WPF mpv render view)
         │    └─ VlcBackend → VlcVideo bitmap surface
         ├─ ShaderInstaller / ShaderCatalog → mpv GLSL chains
         ├─ MagpieUpscalerService → external window scaler
         └─ AudioVisualEngine → AudioVisualizerSurface (local audio only)

ELYCORE
  MpvHwndBackend (managed control plane)
    → ElyFlowRendererInterop (dynamic C ABI binding)
      → ElyFlow.Native dedicated render thread
        → libmpv OpenGL FBO
        → WGL_NV_DX_interop2 shared D3D11 texture
        → optional D3D11 RTX VSR
        → previous/current texture ring
        → optional NvOFFRUC interpolated frame
        → composed DXGI HWND swapchain
```

## 3. Files and ownership

### Application shell

- `App.xaml`: global brushes, styles, templates and theme resources. Runtime accent brushes are updated by `ThemeManager`.
- `App.xaml.cs`: startup ordering, top-level WPF exception handling and debug-console startup.
- `MainWindow.xaml`: login, navigation, media lists, player, settings, ELYCOLOR/ELYSOUND+/ELYFLOW editors and the reparentable `OverlayRoot`.
- `MainWindow.xaml.cs`: feature coordinator. It owns cancellation/generation tokens, current section/item/profile, backend lifetime, playback UI, OSD, airspace overlay, settings synchronization and feature application. Its section comments are the fastest navigation index.
- `AudioVisualizerSurface.cs`: allocation-free-ish WPF `OnRender` scene for FFT bars, particles and shockwaves.
- `VlcVideo.cs`: LibVLC video callback surface that copies decoded frames to a WPF `WriteableBitmap`.

### Domain and persistence models

- `Models/Channel.cs`: Xtream/M3U live category and channel DTOs.
- `Models/MediaModels.cs`: VOD, series, episode, EPG and unified `PlayItem`; local file classification lives here.
- `Models/Profile.cs`: Xtream/M3U connection profile. `ProtectedPassword` is DPAPI material, never clear text.
- `Models/Settings.cs`: all global feature flags and ELYCOLOR/ELYSOUND+ profiles. Adding a setting requires normalization/default handling and UI load/save wiring.
- `Models/BulkObservableCollection.cs`: one-reset list replacement to avoid per-item WPF churn.
- `StateStore`: global `AppState`, per-profile favourites/resume and local library in `%APPDATA%\ElyCast\state.json`; the entire serialized payload is DPAPI-protected and written atomically.
- `ProfileStore`: `%APPDATA%\ElyCast\profiles.json`; DPAPI-protected and atomic. Legacy clear JSON can be read and is rewritten protected on save.

Never place real profiles, playlists, stream URLs, user data or `%APPDATA%\ElyCast` content in the repository.

### Media acquisition

- `IptvService`: validates HTTP(S) Xtream endpoints, calls `player_api.php`, maps categories, builds live/movie/series URLs, fetches short EPG and parses local/remote M3U. Credentials exist in memory because Xtream embeds them in requests/stream URLs; do not log request URLs.
- `MpvNativeInstaller`: downloads a current x64 libmpv development archive from SourceForge and extracts it under `%APPDATA%\ElyCast\tools\mpv`; requires 7-Zip.
- `ShaderCatalog` / `ShaderInstaller`: define shader chains, download known shaders on demand, validate mpv shader content and generate sharpen-tuned variants under the data folder.
- `MagpieUpscalerService`: installs/locates Magpie, updates its per-user configuration atomically, starts it hidden and triggers its scaling hotkey.

### Video abstraction

`IVideoBackend` is the UI/backend boundary. It exposes a WPF `View`, playback/seek/volume/fullscreen, track selection, state events and `VideoStats`. MainWindow must not call backend-specific APIs unless guarded by a concrete type check.

`VideoBackendFactory` selection/fallback order:

1. `elycore`: require libmpv, native DLL and D3D11/WGL preflight; otherwise fall back to plain mpv HWND, then VLC.
2. `rtx-sdk`: use `MpvHwndBackend` with NVIDIA D3D11 VPP/VSR when an NVIDIA driver exists.
3. `mpv-gpu`: use `MpvHwndBackend` with mpv `gpu-next` and D3D11.
4. Anything unavailable ends in `VlcBackend`.

Backend implementations:

- `MpvHwndBackend`: main production backend. In normal mode mpv owns rendering into `MpvHwndHost`. In ELYCORE mode mpv uses `vo=libmpv` and the native DLL owns rendering/presentation. It applies scaler/shader/audio filters, probes source dimensions/FPS, calculates per-playback counter baselines and controls VSR/FRUC live.
- `MpvHwndHost`: custom no-background Win32 child class hosted via WPF `HwndHost`. Only DXGI/mpv paints it. It relays native mouse activity to MainWindow so the WPF HUD appears across the HWND airspace boundary.
- `MpvInterop`: direct libmpv P/Invoke for string/numeric properties and commands. Prefer it over unreliable wrapper accessors.
- `MpvBackend` + `LoggingVideoView`: older WPF LibMPVSharp render path; retain as a diagnostic/legacy implementation, not the default production path.
- `VlcBackend` + `VlcVideo`: compatibility path; decoded RGBA frames are copied to WPF, so it is more CPU-heavy.
- `ElyFlowRendererInterop`: dynamically loads `ElyFlow.Native.dll`, validates every required export and mirrors the native state struct. Managed/native struct layout changes must be made on both sides together.
- `ElyFlowService`: runtime/GPU/driver diagnostic probe for NVIDIA Optical Flow and NvOFFRUC availability.

## 4. ELYCORE renderer invariants

Native sources live in `native/ElyFlow.Native`:

- `ElyFlowRenderer.cpp`: renderer lifecycle, mpv OpenGL context, zero-copy interop, VSR, FRUC scheduling, swapchain and diagnostics.
- `ElyFlowNative.cpp`: stable public FRUC/runtime C API and global adapter ownership.
- `NvidiaRuntimeLoader.*`: secure discovery/loading of NVIDIA driver and NvOFFRUC DLLs.
- `NvidiaFrucAdapter.*`: translates app textures/fences into the official NvOFFRUC API.
- `include/ElyFlowNative.h`: public ABI shared conceptually with `ElyFlowRendererInterop`.
- `include/mpv/*`: ISC-licensed libmpv client/render headers.

Do not violate these rules:

1. The libmpv render context is created, used and destroyed on the dedicated native render thread.
2. Destroy ELYCORE before disposing the managed mpv player/handle.
3. The WGL context must be current for GL setup/render calls; WGL/D3D interop objects must be locked before mpv renders and unlocked before D3D consumes them.
4. Avoid CPU readback. The intended path is GPU texture sharing/copy only.
5. `ResizeBuffers` is allowed only for a real target-size change. VSR/FRUC toggles must not discard the visible swapchain.
6. Recreate FRUC only after target dimensions have stabilized; NvOFFRUCCreate is expensive and can stall presentation.
7. Fence values must remain strictly monotonic even after a failed FRUC call.
8. Preserve the last composed frame across focus/occlusion. The HWND swapchain intentionally uses the composed sequential model to avoid NVIDIA MPO/independent-flip black flashes below a transparent WPF overlay.
9. Skip rendering for zero-sized/minimized targets without destroying the pipeline.
10. Report device/Present/VSR/FRUC failures through `ElyFlowRendererState.message`; do not silently loop on corrupted resources.

The NVIDIA SDK is optional at compile time. CMake defines `ELYFLOW_HAS_NVOFFRUC_SDK=0` when the header is absent, producing a functional renderer with FRUC unavailable. Do not commit the proprietary SDK, its ZIP or runtime DLLs.

## 5. WPF airspace and focus

A native child HWND always renders above WPF elements in the same top-level window. `OverlayRoot` is therefore declared in a collapsed XAML mount, detached once, and hosted in a transparent borderless owned `Window` aligned over `VideoStage`.

Critical rules:

- Keep the overlay window alive while connected. Hiding/showing it on activation forces DWM composition changes and can flash the player.
- The overlay uses `WS_EX_NOACTIVATE`/`MA_NOACTIVATE`: mouse controls work without stealing foreground focus from the owner.
- Reanchor/rescale it on location, size, DPI, state and Win32 move/size notifications.
- Do not replace it with an ordinary in-window Grid; HWND airspace will cover it.
- Native/WPF mouse-leave transitions are not a true player leave. Recheck the cursor against `VideoStage` before hiding the OSD.
- Do not restart opacity animations on every mouse-move; `_osdVisible` gates animation and movement only resets the timer.

## 6. Playback and feature flow

Connection:

1. MainWindow loads an Xtream or M3U profile.
2. `IptvService` fills live items and lazily fetches VOD/series.
3. All lists use `PlayItem`; favourites and last-played state are profile-scoped.
4. `Play` resolves a URL/path, increments `_playbackGeneration`, configures visualizer/EPG/tracks, applies backend settings and starts playback.
5. Generation IDs and cancellation tokens prevent stale async work from updating a newer playback or section.

Feature ownership:

- Internal upscaling and ELYCOLOR become mpv scalers, GLSL shader chains and video equalizer properties.
- ELYSOUND+ becomes mpv audio-filter/equalizer configuration; updates are debounced before applying.
- ELYFLOW settings control native FRUC and target cadence. ELYCORE VSR and FRUC are independently togglable.
- Magpie is external to the playback backend and scales the application window.
- Subtitle/audio preferences are matched by normalized track name and reapplied after tracks become available.
- Live reconnect uses a timer; EPG and media detail requests are cancellable.

Audio-only local playback:

- mpv/VLC still performs playback and decoding.
- `AudioVisualEngine` opens the same local path with NAudio on a background thread, follows player position, computes a 2048-point FFT into 56 bands and queues beats.
- `CompositionTarget.Rendering` reads snapshots and advances `AudioVisualizerSurface` plus WPF metadata/disc animations on the UI thread.
- Unsupported analysis formats fall back to idle waves; this must never stop playback.

## 7. Threading and lifecycle

- WPF controls and observable collections: UI dispatcher only.
- IPTV/download work: async with cancellation; never block the UI thread on network I/O.
- Audio analysis: background thread below normal priority; it never calls mpv or WPF.
- ELYCORE rendering: one native high-priority/MMCSS thread; keep expensive managed calls off it.
- `MainWindow.RecreateVideoBackend`: detach events, stop/dispose the old backend, replace the view, reapply settings, optionally replay. Preserve this order.
- Backend event handlers may arrive off-thread and must dispatch to WPF.
- Any async playback UI update must verify generation/backend identity after awaiting.

## 8. Build, runtime and validation

Tracked source must build without the proprietary NVIDIA SDK; FRUC will simply be unavailable. Build native before managed because the project intentionally fails Release/Debug compilation if `native/ElyFlow.Native/bin/<Configuration>/ElyFlow.Native.dll` is missing.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release
```

Equivalent manual sequence:

```powershell
cmake -S native/ElyFlow.Native -B native/ElyFlow.Native/build -A x64
cmake --build native/ElyFlow.Native/build --config Release
dotnet restore "ElyCast TV Player.csproj"
dotnet build "ElyCast TV Player.csproj" -c Release -p:Platform=x64
```

Useful smoke hook:

```powershell
$env:ELYCAST_DIAGNOSTIC_FILE = 'C:\absolute\path\video.mp4'
& '.\bin\Release\net8.0-windows\Elysium Cast IPTV.exe'
```

This enters the player and logs renderer stats after five seconds. For renderer changes validate: native and managed builds, real playback, focus churn, resize/fullscreen, OSD hover/click, VSR/FRUC toggles, `Present` errors, late presents and dropped frames.

There is currently no unit-test project. CI is a Windows x64 compile gate.

## 9. Safe-change checklist

Before committing:

1. Keep changes inside the owning component; avoid expanding `MainWindow` when a service/backend owns the behavior.
2. Preserve fallback behavior when optional tools, shaders, libmpv, NVIDIA hardware or FRUC are missing.
3. Never log full stream/request URLs or credentials.
4. Never commit local profiles, state, M3U files, media, logs, SDKs, downloaded tools, binaries or build directories.
5. Keep managed/native ABI field order, sizes and calling conventions synchronized.
6. Build native Release, then managed Release x64.
7. For renderer/UI changes, test the HWND overlay boundary and focus transitions, not only process stability.
8. Update this document and README when architecture, prerequisites or user-facing capabilities change.
