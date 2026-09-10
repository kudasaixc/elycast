# Changelog

All notable ElyCast changes are documented here. This project follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.3.1] - 2026-09-10

### Added

- Instant English and French interface switching, persisted in the protected application state.
- Language selection in the first-run wizard and in Settings > Interface.
- Accent-insensitive multiword search across titles, artists, albums, and categories, with Ctrl+F focusing the relevant search field.
- Automatic recovery from the last valid encrypted state or profile backup.

### Changed

- English is now the default application, repository, documentation, and release-note language.
- Static and dynamic UI text, diagnostics, installers, backend messages, and native renderer messages now use the shared localization catalog.
- Em dashes were removed from tracked text and public release notes.
- Folder and drag-and-drop imports now run through one cancellable background discovery path and preserve navigation changes made while scanning.
- Settings descriptions focus on user choices, and the developer boot-delay control and planned-feature list have been removed from the interface.
- Switching playback backends preserves the current position and paused state.

### Fixed

- Xtream authentication is validated explicitly, numeric identifiers are accepted, duplicate categories no longer abort loading, and credentials are encoded safely in playback URLs.
- Authenticated accounts without live channels can still open their movie and series catalogues.
- M3U playlists now support bare entries, relative local or remote paths, quoted commas, and stable favourites after playlist reordering.
- Invalid EPG rows no longer discard valid programme entries.
- Local imports skip inaccessible folders and directory links instead of freezing or failing before feedback can be shown.
- Removing local media also clears stale resume entries, playlists, queues, favourites, and playback context.
- Albums with the same name but different artists remain separate.
- The Next action now honours manually queued tracks and skips missing files.
- Late catalogue, track, backend, and playback callbacks can no longer update a newer session.
- Corrupt saved data is preserved when neither the primary file nor its backup can be read, preventing silent replacement with defaults.

### Internal

- Added a 33-check production-code regression suite for M3U, Xtream, search, local discovery, identity, normalization, DPAPI migration, backup recovery, and corruption handling.
- Backend callbacks are marshalled asynchronously to WPF with backend identity and playback-generation checks.

## [1.3.0-canary] - 2026-07-12

### Added

- **File import**: Music and Video now offer multi-file selection with media-specific filters in addition to folder import.
- **Drag and drop**: files and folders can be dropped directly into the local list. Mixed audio and video drops are routed to the correct library by extension. Unsupported files are ignored with feedback instead of causing an error.

### Internal

- Folder and file import now share the parallel import engine in `LocalLibraryService.BuildItems` and `ImportFilesAsync`.

## [1.3.0] - 2026-07-12

### Added

- **AudioCore+**: a native D3D11 audio visualizer scene through ELYCORE, including the background, separable Gaussian blur, dimmer, zoom, pan, parallax, bars, particles, and shader-rasterized shockwaves.
- The classic WPF renderer and AudioCore+ now share one canonical simulation, including positions, quantized colors, pen widths, and settings.

### Changed

- AudioCore+ visualizer cadence is decoupled from display refresh and can target 30 to 360 FPS.
- Native rendering snapshots managed state under a short lock, then performs GPU work without holding that lock.
- FRUC and RTX VSR are bypassed automatically while the audio scene is active.

### Fixed

- Background parallax no longer resets abruptly. Pointer position uses `GetCursorPos` and `PointFromScreen`, and the offset is smoothed over time.
- Background framing no longer applies the internal margin that caused excess zoom and shifted pan behavior.
- The debug console now degrades safely when console allocation fails instead of aborting startup.

### Internal

- `SystemMediaTransport` now uses RAII, the rule of five, and `unique_ptr` instead of explicit `new` and `delete`.
- Renderer atomic operations now use sequential consistency.

## [1.2.0]

### Added

- Redesigned local Music area with album, artist, genre, and playlist groups, artwork, details, contextual playback, queueing, shuffle, and repeat.
- Metadata editor that writes title, artist, album, genre, track number, disc number, and artwork directly to files.
- AudioCore+ foundations, including native renderer selection, ELYCORE gating, and FRUC bypass for audio.

### Changed

- Audio import now enriches metadata in the background and merges results by path into the live list.
- Artist grouping recognizes semicolon-separated collaborators.

## [1.1]

### Added

- **ELYSMART** hardware detection, benchmark, explainable recommendations, bounded history, diagnostics, and player health monitoring.
- First-run onboarding for user preferences, detection, benchmark, backend selection, dependency installation, and compatibility tests.
- Redesigned audio player with a real-time FFT visualizer, particles, artwork palettes, animated backgrounds, VSync, and targets up to 360 FPS.
- Separate local audio and video libraries, plus Windows media controls for local audio only.
- Application identity with `ElyCast.exe`, icon, AppUserModelID, and Shell shortcut.

[1.3.0]: https://github.com/kudasaixc/elycast/releases/tag/v1.3.0
[1.3.1]: https://github.com/kudasaixc/elycast/releases/tag/v1.3.1
[1.2.0]: https://github.com/kudasaixc/elycast/releases/tag/v1.2.0
[1.1]: https://github.com/kudasaixc/elycast/releases/tag/v1.1
