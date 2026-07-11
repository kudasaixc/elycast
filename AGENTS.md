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
    ├─ OnboardingWindow (first run only: profile name, connection mode,
    │    accent, content tastes, HardwareProbe → engine recommendation,
    │    dependency download, RtxVsrTester)
    └─ MainWindow
         ├─ IptvService → Xtream HTTP API or M3U parser
         ├─ ProfileStore / StateStore → %APPDATA%\ElyCast (DPAPI)
         ├─ VideoBackendFactory → IVideoBackend
         │    ├─ MpvHwndBackend (mpv-gpu / rtx-sdk / elycore)
         │    ├─ MpvBackend (legacy WPF mpv render view)
         │    └─ VlcBackend → VlcVideo bitmap surface
         ├─ ShaderInstaller / ShaderCatalog → mpv GLSL chains
         ├─ MagpieUpscalerService → external window scaler
         ├─ ElySoundController → one labelled mpv/libavfilter graph + runtime commands
         ├─ WindowsMediaTransportService → native desktop SMTC bridge (Windows media flyout/keys)
         ├─ AudioVisualEngine → AudioVisualizerSurface (local audio only)
         └─ Services/ElySmart → measured benchmark, explainable recommendations,
              persisted report and low-frequency runtime health monitoring

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

- `App.xaml`: global brushes, styles, templates and theme resources. `ThemeManager.Apply` regenerates the ENTIRE derived palette from the accent colour at runtime — accent brushes, tinted `AppBackground` gradient, `CardStroke`/`FieldStroke`, hover/selection/popup/scrollbar tints (`HoverFaintBrush`, `HoverSoftBrush`, `HighlightBrush`, `SelectionBrush`, `ScrollThumbBrush`, `PopupBrush`), tinted text (`TextBrush`/`MutedBrush`/`FaintBrush`), `AccentSoftBrush`/`AccentDeepBrush` and the `GlowColor` login shadow, plus the console accent (`DebugConsole.AccentColor`). Themed brushes must be consumed with DynamicResource and colours must never be hardcoded in XAML — add a derived resource instead. Only `CardBrush`/`FieldBrush` (neutral surfaces) stay static.
- `App.xaml.cs`: startup ordering, top-level WPF exception handling and debug-console startup. Shows `OnboardingWindow` modally before `MainWindow` when `Settings.OnboardingCompleted` is false.
- `OnboardingWindow.xaml(.cs)`: first-run wizard (6 steps). Collects display name, preferred connection (`xtream`/`m3u`/`local`) **with inline credentials/playlist and a live connection test**, accent colour and content interests. Step 5 maps the selected interests to an ELYSMART workload, runs the same asynchronous/cancellable `BenchmarkEngine` used by Settings, displays its measured score and explanations, and uses its proposed configuration to prefill editable renderer/VSR/FRUC choices. Step 6 downloads libmpv/shaders and runs `RtxVsrTester` when the chosen pipeline uses VSR. Settings and credentials are persisted only on finish/skip (the standalone ELYSMART report may already be written atomically). Credentials become a normal DPAPI profile plus `Settings.AutoConnectProfile`: MainWindow connects with it automatically at startup (cleared if the profile is deleted). `local` mode — and the "Continuer sans IPTV" button on the login screen — enter the local library directly without an IPTV login.
- `MainWindow.xaml`: login, navigation, media lists, player, settings, ELYCOLOR/ELYSOUND+/ELYFLOW editors and the reparentable `OverlayRoot`.
- `MainWindow.xaml.cs`: root of the partial `MainWindow` coordinator. Keep only shared state/lifetime plus startup, diagnostic playback hooks, profiles/connection, fullscreen/OSD and window chrome here. It owns the cancellation/generation tokens, active profile/item/backend and overlay window. Do not put catalogue, playback, backend-host, settings-editor or console code back into this file.
- `MainWindow.VideoHost.cs`: HWND airspace overlay geometry/lifetime, backend creation/replacement-facing events and playback termination callbacks. It is the WPF/native boundary; preserve overlay no-activate behavior and dispatch backend events before touching controls.
- `MainWindow.Playback.cs`: playback start/stop/pause, reconnect/zapping, EPG, seek, subtitle/audio track synchronization and stats formatting. Every delayed/async update must retain the playback-generation and expected-backend checks described below.
- `MainWindow.Catalog.cs`: navigation and media-library presentation. Owns `Nav_Changed`, lazy Films/Séries acquisition, list/filter/count rebuilding, local-library mutations, favourites, series drill-down and local-audio visualizer coordination. The audio scene resolves the persisted background mode (`solid`, embedded `cover`, or one of `Assets/AudioBackgrounds`), extracts a small dominant-colour palette from the effective bitmap, applies blur/dimming and drives slow zoom/pan/optional mouse parallax. Navigation is keyed from the checked button's `Tag`; `NavSettings` must have only `Checked="Nav_Changed"` (never `Unchecked`) or WPF creates a second, racing navigation. Failed lazy loads leave `_movieItems`/`_seriesItems` null so the next click retries instead of caching a false empty result.
- `MainWindow.Settings.cs`: general settings UI, backend recreation, mpv/Magpie installation and activation, settings-panel lifecycle and external-upscaler window hooks. `RecreateVideoBackend` remains the only UI-level backend replacement path and must keep detach → stop/dispose → replace view → reapply settings → optional replay ordering.
- `MainWindow.Upscaling.cs`: internal mpv upscale selection, OSD mode list synchronization and shader installation/application. External Magpie lifecycle stays in `MainWindow.Settings.cs`.
- `MainWindow.ElyColor.cs`: ELYCOLOR built-ins/custom profiles, editor capture/save/delete and backend application. Pipeline-affecting profile changes may deliberately call the replacement path in `MainWindow.Settings.cs`.
- `MainWindow.ElySound.cs`: ELYSOUND+ preset/editor/OSD synchronization, debounce state and calls to `IElySoundBackend`. The actual labelled filter topology and runtime command policy remain owned by `Services/Audio/ElySoundController.cs`.
- `MainWindow.ElyFlow.cs`: ELYFLOW/ELYCORE controls, target cadence/buffer UI, VSR/FRUC gating, persistence and live backend application. Native renderer implementation and truth counters remain outside the window.
- `MainWindow.Console.cs`: registration and formatting for all debug-console commands, including playback, tracks, pipeline controls and GPU/hardware diagnostics. Command implementations may dispatch into the owning partial but must not duplicate backend policy.
- `MainWindow.ElySmart.cs`: UI adapter only for ELYSMART lifecycle, progress/cancellation, consented application, report export and notification presentation. Benchmark, scoring, detection, history and optimization policy must stay under `Services/ElySmart`; do not move decision rules into this partial.

Partial-class placement rule: XAML event handlers belong in the partial matching the feature above; shared fields should stay in `MainWindow.xaml.cs` unless they are used by exactly one partial. A partial may call private members of another partial, but backend-specific operations still require the appropriate interface/concrete-type guard. Keep each new partial focused and preferably below ~600 lines; extract a service when logic can be expressed without direct WPF-control access.
- `AudioVisualizerSurface.cs`: high-cadence WPF `OnRender` scene for FFT bars, particles and shockwaves. It accepts the effective image palette plus runtime particle count/distance; keep bitmap decoding/palette extraction outside this render surface. Fixed bar trigonometry is precomputed, layout scale changes only on `SizeChanged`, and palette styles use bounded direct-array caches (32 hues × 9 alpha × 10 thickness × cap style), never per-frame dictionaries or unbounded caches. `AverageRenderTimeMs` is the measured CPU submission time shown in audio stats; 240 FPS requires comfortably below 4.17 ms. Palette extraction treats artwork with less than 12% meaningfully chromatic samples as monochrome and derives silver/gray luminance accents; the surface must not force saturation onto an achromatic palette. The target FPS, VSync and shake/background animation policy are owned by `MainWindow.Catalog.cs`, with persisted controls in the `audio-player` settings category. `CompositionTarget.Rendering` is always physically compositor-bound: VSync ON uses a phase accumulator to hit arbitrary targets exactly (notably 240 on 360 Hz); VSync OFF unlocks updates up to the compositor refresh. The audio stats panel reports the measured accepted-frame rate from a rolling window, alongside target FPS, VSync, FFT publication rate and render CPU time; never label the configured target as real FPS. The packaged-background selector stays enabled in every mode because it also configures Cover mode's fallback when metadata has no embedded art. Both `_audioLastTickSeconds` and `_audioLastRenderedSeconds` must reset whenever its per-track stopwatch restarts; retaining the FPS-gate timestamp across tracks freezes rendering until the new stopwatch catches the previous track's elapsed time. Reset phase/player-sync/FPS counters there too. The full-screen blurred image uses `BitmapCache` at reduced render scale: animate only its cached transform, never force a Gaussian blur rerender each frame.
- `VlcVideo.cs`: LibVLC video callback surface that copies decoded frames to a WPF `WriteableBitmap`.

### Domain and persistence models

- `Models/Channel.cs`: Xtream/M3U live category and channel DTOs.
- `Models/MediaModels.cs`: VOD, series, episode, EPG and unified `PlayItem`; local audio tag fields and the persisted `LocalPlaylist` path schema live here.
- `Models/Profile.cs`: Xtream/M3U connection profile. `ProtectedPassword` is DPAPI material, never clear text.
- `Models/Settings.cs`: all global feature flags and ELYCOLOR/ELYSOUND+ profiles. Adding a setting requires normalization/default handling and UI load/save wiring. ELYSOUND+ profile schema v2 stores a real `LimiterCeilingDb`; `StateStore.Normalize` migrates the legacy linear `Limiter` percentage and clamps every DSP control. Onboarding fields: `OnboardingCompleted`, `UserDisplayName`, `PreferredConnection`, `ContentInterests`. `StateStore.Normalize` strips `fsr` from persisted `OsdUpscaleModes` (removed from OSD defaults).
- `Models/BulkObservableCollection.cs`: one-reset list replacement to avoid per-item WPF churn.
- `StateStore`: global `AppState`, per-profile favourites/resume, separate `LocalAudioLibrary` / `LocalVideoLibrary` collections and audio playlists in `%APPDATA%\ElyCast\state.json`; it migrates and clears the legacy mixed `LocalLibrary` once. The entire serialized payload is DPAPI-protected and written atomically.
- `ProfileStore`: `%APPDATA%\ElyCast\profiles.json`; DPAPI-protected and atomic. Legacy clear JSON can be read and is rewritten protected on save.

Never place real profiles, playlists, stream URLs, user data or `%APPDATA%\ElyCast` content in the repository.

### Media acquisition

- `IptvService`: validates HTTP(S) Xtream endpoints, calls `player_api.php`, maps categories, builds live/movie/series URLs, fetches short EPG and parses local/remote M3U. Credentials exist in memory because Xtream embeds them in requests/stream URLs; do not log request URLs.
- `MpvNativeInstaller`: downloads a current x64 libmpv development archive from SourceForge and extracts it under `%APPDATA%\ElyCast\tools\mpv`; requires 7-Zip.
- `ShaderCatalog` / `ShaderInstaller`: define shader chains, download known shaders on demand, validate mpv shader content and generate sharpen-tuned variants under the data folder.
- `MagpieUpscalerService`: installs/locates Magpie, updates its per-user configuration atomically, starts it hidden and triggers its scaling hotkey. Off by default; the OSD FSR/Magpie quick button is kept in the tree but collapsed (state tracking still references it) — activation lives in the video settings only.
- `ElySoundController`: backend-owned ELYSOUND+ control plane. It owns the single labelled `@elysound` lavfi entry, the typed band/compander/limiter mapping and built-in presets. A channel-layout change may rebuild this one entry; preset/slider changes use `af-command` against named FFmpeg instances. Failure removes only `@elysound`, never seeks/reloads/stops the media.
- `WindowsMediaTransportService`: managed/native bridge for Windows System Media Transport Controls, exclusively for playable local audio files (never live, films, episodes or local video). `AudioMetadataReader` is the shared source of TagLib title/artist/album/embedded-cover data; filename is only the missing-title fallback. The service caches embedded artwork under `%LOCALAPPDATA%\ElyCast\cache`, dynamically binds the `ElyMediaTransport_*` C ABI from `ElyFlow.Native.dll`, mirrors play/pause/closed state, and dispatches Windows play/pause/next/previous commands back to MainWindow's existing backend/navigation methods. WinRT/COM ownership, ElyCast AppUserModelID/Start-menu shortcut registration and event tokens stay in `native/ElyFlow.Native/src/SystemMediaTransport.cpp`; the service never creates or owns a second media player.
- `HardwareProbe`: WMI probe (CPU/RAM/GPUs, NVIDIA driver release derivation) plus the pure recommendation logic (backend + VSR/FRUC + upscale method + ELYCOLOR preset from content interests) shared by the onboarding wizard and the `hw` console command.
- `RtxVsrTester`: real-pipeline VSR test — checks GPU/driver against NVIDIA's VSR spec (`HardwareProbe.SupportsRtxVsr`), then plays a muted Windows sample through the selected production pipeline inside a hidden off-screen Window. `rtx-sdk` verifies d3d11va + d3d11vpp; ELYCORE requires ABI-compatible native code and `vsrEffective` from the post-Blt NVIDIA status query. A windowless mpv (vo=null) must NOT be used: without a render context hwdec silently falls back to software decoding (false negative). UI thread only; never run while another backend owns libmpv; the wizard runs it before MainWindow exists.

### ELYSMART performance intelligence

`Services/ElySmart` is an independent layer; it must not reference `MainWindow` or WPF controls:

- `BenchmarkEngine.cs`: asynchronous, cancellable orchestrator. Runs safe CPU SIMD, managed-memory bandwidth, temporary sequential-storage, FFT and particle simulations plus real dependency/native preflights. It persists `%APPDATA%\ElyCast\elysmart-report.json` atomically. Media decode/shader rows remain explicitly `Skipped` until a deterministic benchmark media pack is available; never interrupt or reuse user playback as a benchmark.
- The onboarding and Settings entry points must call this same engine; do not reintroduce a parallel recommendation path through `HardwareProbe.Recommend`. Onboarding content chips map a single dominant interest to its matching ELYSMART workload and multiple/no selections to `Mixed`.
- `HardwareDetector.cs`: detailed WMI/runtime inventory and stable hardware fingerprint. Missing driver-only facts remain unknown rather than inferred. A fingerprint change (GPU/driver/RAM/display) prompts a rerun.
- `BenchmarkProfiles.cs`: workload priority weights (`Iptv`, `Films`, `Series`, `Anime`, `Audio`, `Mixed`).
- `CapabilityDatabase.cs`: centralized, labelled cost priors. These are estimates, never measurements.
- `QualityScorer.cs`: domain/global scores calculated only from successful results, then weighted by workload.
- A score must include only results containing at least one `Measured` metric. Capability preflights remain valuable evidence for compatibility/recommendations but must never manufacture a performance score.
- Missing/Skipped domains are displayed as `non mesuré` and excluded from the global calculation; never substitute a neutral 50. Global workload weights are renormalized over genuinely measured domains. CPU/memory microbenchmarks are calibrated as ElyCast readiness thresholds (full headroom at 8 GFLOP/s SIMD and 12 GB/s copy), not as an open-ended hardware leaderboard; keep each measurement window at least 400 ms to avoid JIT/timer noise.
- `BenchmarkReport.MeasurementCoveragePercent` reports the fraction of score domains backed by measurements. UI surfaces must label a partially covered global score as provisional and show this coverage separately; coverage affects confidence, never the measured performance value itself.
- `RecommendationEngine.cs`: explainable configuration proposal with gain, cost, confidence and critical/decorative classification. Critical settings require explicit UI application.
- `BenchmarkReport.cs`: versioned DTOs and the mandatory provenance enum: `Measured`, `Detected`, `Estimated`, `Unavailable`, `Skipped`. Never present a detected capability or database prior as a measured result.
- `RuntimeMonitor.cs`: one-second low-cost sampler. Backend/WPF telemetry is obtained through the UI dispatcher; process sampling/history work stays off-thread.
- `PerformanceHistory.cs` / `HealthAnalyzer.cs`: bounded five-minute history and sustained-window analysis; never react to a single spike.
- `NotificationEngine.cs`: cooldown/ignore policy, at most one issue emitted per evaluation. `MainWindow.ElySmart.cs` presents issues through one non-activating bottom-right `ElySmartNotificationWindow` toast with Optimize/Ignore/Always-ignore actions; always-ignore kinds persist in `Settings.ElySmartIgnoredHealthIssues` and are restored at startup.
- `RuntimeOptimizer.cs`: progressive and reversible decorative-only degradation order: parallax → particles → visualizer FPS → blur → background pan. It must never change renderer, codec, video engine or audio output automatically.
- `ElySmartDiagnosticSurface.cs`: lightweight WPF history graph (CPU, process RAM and measured visualizer FPS) fed from the bounded runtime history. It renders only; sampling and analysis stay in the services layer.

The current runtime telemetry contract exposes backend FPS/frame time/dropped frames, UI dispatcher delay, process CPU/private RAM and measured audio-visualizer FPS. GPU engine/VRAM, isolated shader time and universal audio underruns require future backend/vendor counters and must remain unavailable until genuinely instrumented.

### Video abstraction

`IVideoBackend` is the UI/backend boundary. It exposes a WPF `View`, playback/seek/volume/fullscreen, track selection, state events and `VideoStats`. `Ended` carries a `PlaybackEndReason`; infrastructure stops use `Replaced`/`Teardown` and must not mutate player UX. `PlaybackTerminationPolicy` is the single decision table for finite EOF, manual stop, live failure/reconnect and next-media replacement. `IElySoundBackend` is the optional audio-DSP control plane. MainWindow must not call backend-specific APIs unless guarded by a concrete type/interface check.

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
- `ElyFlowRendererInterop`: dynamically loads `ElyFlow.Native.dll`, requires the exact renderer ABI version before backend creation, validates every required export and mirrors the native state struct. ABI v4 reports VSR requested/available/effective state, processor/extension state, adapter/driver identity, DXGI formats/colour space, VSR processed/bypassed frames, Present errors, target rebuilds and real `ResizeBuffers` calls. Managed/native struct layout changes must be made on both sides together.
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

RTX VSR invariants (all three were established empirically on driver 610.62 / RTX 3060 Ti with the `tests/VsrProbe` matrix and runtime audits — violating any of them either leaves VSR silently disengaged or crashes the driver):

11. The NVIDIA driver only engages VSR on a YUV (NV12) input stream. RGBA input is classified as graphics: `VideoProcessorBlt` succeeds but `IsInUseForThisVP` stays false. ELYCORE therefore converts mpv's RGBA render target to NV12 with a first VideoProcessor pass before the VSR pass. Plain `D3D11_BIND_RENDER_TARGET` NV12 works; decoder bind flags and 16-alignment are NOT required.
12. The VSR pass's destination rect must equal its output rect (full-frame). A letterboxed dest rect crashes the driver asynchronously (device removed, `DXGI_ERROR_DRIVER_INTERNAL_ERROR`). The VP output texture is therefore sized to the aspect-fitted content and centering is done afterwards with `CopySubresourceRegion` (`vsrDestOffsetX/Y`).
13. VSR must not run on the WGL-interop'd main device (`wglDXOpenDeviceNV`): the kernel dispatch TDRs it. The renderer owns a dedicated D3D11 video device (`vsrDevice`); frames cross devices through keyed-mutex shared textures (`vsrCopy` in, `vsrOutput` out). `IDXGIKeyedMutex::AcquireSync` must be compared against `S_OK` — `WAIT_TIMEOUT` (0x102) is not a FAILED HRESULT.

Runtime VSR proof lives in `ElyFlowRendererState`: `vsrEffective` mirrors the driver's in-use bit read back via `VideoProcessorGetStreamExtension` (raw payload in `vsrQueryRaw`; bit0 available, bit1 fields valid, bit3 in-use, bits4-6 quality level — level matches the NVIDIA Control Panel setting), and `vsrFramesProcessed`/`vsrFramesBypassed` count engaged vs non-engaged frames. The decoding is double-validated by `tests/VsrProbe`'s A/B pixel comparison: identical NV12 input, extension off vs on → ~68% of output bytes differ (max per-channel delta ~160) exactly when the in-use bit is set. Two empirically proven driver behaviours (VsrProbe watch-mode bisection, driver of 2026-07, RTX 3060 Ti):
- **NVCP "Super Resolution — État" ignores VSR sessions whose input texture is written by another VideoProcessor.** `--watch-chain` (conversion VP writes the VSR VP's input) shows "Inactif" while the query reports in-use 0x4F; every other input source shows "Actif": CPU-uploaded (`--watch`), graphics-pipeline-written RTV clears on the NV12 planes (`--watch-renderchain`), cross-device with WGL-interop + present (`--watch-cross-interop`), with a resident CUDA context (`--watch-cuda`). ELYCORE therefore converts RGB→NV12 with two pixel-shader draws by default; the former conversion VP remains as a fallback and can be forced with `ELYFLOW_VSR_CONV_VP=1` for A/B diagnostics. The indicator is cosmetic either way — `vsrQueryRaw`/pixel proof remain the ground truth.
- **VSR engagement is globally arbitrated: while one process holds an engaged VSR session, no other process can engage** (their query stays 0x07, never in-use). Any probe run with ELYCAST open measures the arbitration, not the feature — close ELYCAST before probing. Debug switches: `ELYFLOW_VSR_NO_EXT=1` (NV12 chain without the extension) and `ELYFLOW_VSR_NO_QUERY=1` (no post-Blt query). `tests/VsrProbe` is a standalone matrix + A/B harness (fresh device per config) for future driver-behaviour investigations.
11. RTX VSR and FRUC are independent controls. `vsrEffective` is true only after `VideoProcessorGetStreamExtension` reports that NVIDIA VSR is in use; a successful `SetStreamExtension`/`VideoProcessorBlt` alone is not proof.

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
- ELYSOUND+ is one permanent, labelled lavfi topology: exact-dB preamp + six peaking EQ bands (64/320/950/2600/5200/9500 Hz), stereo-only conservative `extrastereo`, RMS soft-knee `acompressor`, and `alimiter` with `level_in=level_out=1`, `level=0`, PTS latency compensation and a sample-domain dBFS ceiling with inter-sample margin. `dynaudnorm`, `crossfeed` and `stereowiden` are intentionally absent. Debounced UI updates become runtime filter commands and do not rebuild `af`; transient named-instance refusals are retried without replacing the graph. Set `ELYCAST_AUDIT_ELYSOUND=1` for per-command position, graph, audio-parameter and timing traces.
- ELYFLOW settings control native FRUC and target cadence. ELYCORE VSR and FRUC are independently togglable.
- Magpie is external to the playback backend and scales the application window.
- Subtitle/audio preferences are matched by normalized track name and reapplied after tracks become available.
- Live reconnect uses a timer; EPG and media detail requests are cancellable.

Audio-only local playback:

- mpv/VLC still performs playback and decoding.
- `AudioVisualEngine` opens the same local path with NAudio on an above-normal background thread, follows player position, computes a Hann-windowed 2048-point FFT into 56 bands at a phase-scheduled 120 analyses/s and queues beats. Hann coefficients and log-band/bin maps are precomputed. UI snapshot reads use `Monitor.TryEnter` and retain the previous snapshot instead of ever blocking a rendered frame; backend position synchronization is capped at 120/s.
- `LocalLibraryService` exclusively owns recursive extension-filtered folder discovery, TagLib enrichment, deduplication, deterministic artist/album/disc/track ordering and playlist path resolution. `MainWindow.LocalMedia.cs` owns the WPF commands and session queue/shuffle/repeat state. Audio and video use distinct navigation sections and persisted collections; playlist/queue commands must never be exposed for local video.
- `CompositionTarget.Rendering` reads snapshots and advances `AudioVisualizerSurface` plus WPF metadata/disc animations on the UI thread.
- Unsupported analysis formats fall back to idle waves; this must never stop playback.

## 7. Threading and lifecycle

- WPF controls and observable collections: UI dispatcher only.
- IPTV/download work: async with cancellation; never block the UI thread on network I/O.
- Audio analysis: background thread below normal priority; it never calls mpv or WPF.
- ELYSMART benchmark work: background tasks with cancellation; only progress/report binding uses the UI dispatcher. RuntimeMonitor samples once per second and marshals the backend telemetry callback to WPF before reading it.
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
& '.\artifacts\Release\ElyCast.exe'
```

This enters the player and logs renderer stats after five seconds. For renderer changes validate: native and managed builds, real playback, focus churn, resize/fullscreen, OSD hover/click, VSR/FRUC toggles, `Present` errors, late presents and dropped frames.

There is no framework-based unit-test suite. CI provides a Windows x64 compile gate plus CodeQL analysis for the managed C# and native C++ code.

The dependency-free regression runner covers playback termination policy and ELYSOUND+ topology/fallback. Its audit modes probe every `af-command`, continuity on audio/video, user-supplied references and long runtime stress. Measurement mode generates deterministic stereo/mono/6-channel signals, renders them through installed libmpv `ao=pcm`, and writes sample peak, 4x true-peak estimate, RMS, gated BS.1770-style integrated/momentary/short-term loudness, EBU-style LRA, correlation and latency. These in-repo meters are standards-aligned estimates, not certified meters:

```powershell
dotnet run --project .\tests\ElyCast.Regression\ElyCast.Regression.csproj -c Release
dotnet run --project .\tests\ElyCast.Regression\ElyCast.Regression.csproj -c Release -- --measure-audio <libmpv-directory> .\artifacts\validation
dotnet run --project .\tests\ElyCast.Regression\ElyCast.Regression.csproj -c Release -- --probe-continuity-file <libmpv-directory> .\artifacts\validation <reference-media>
dotnet run --project .\tests\ElyCast.Regression\ElyCast.Regression.csproj -c Release -- --measure-references <libmpv-directory> .\artifacts\validation <user-reference> [...]
```

The debug console maps the whole domain: playback (`now`, `pause`, `resume`, `stop`, `seek`, `vol`, `zap`, `fullscreen`, `tracks`, `audio`, `sub`), connection (`profiles`, `connect`, `disconnect`), pipeline (`backend`, `upscale`, `elycolor`, `elysound`, `elyflow`, `vsr`, `magpie`, `mpv`) and diagnostics (`gpu` — the formatted ELYFLOW GPU panel with decode/VSR/FRUC/frame counters, `hw`, `deps`, `settings`, `logfile`, `onboarding reset`). Registration lives in `MainWindow.RegisterConsoleCommands` and its `RegisterPlayback/Pipeline/DiagnosticCommands` helpers.

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
