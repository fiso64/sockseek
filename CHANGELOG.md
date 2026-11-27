# Changelog

Notable changes in each Sockseek release. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/) (sometimes).

## [Unreleased]

Sockseek 4.0 turns the daemon into a full Soulseek client with a WebUI. One-shot downloads from the CLI work as before. For config and script changes, see [Coming from 3.x](https://fiso64.github.io/sockseek/using-sockseek/coming-from-v3). The docs now live at https://fiso64.github.io/sockseek/.

### Added

- **WebUI.** Search, download and follow transfers in the browser. Every download-tool job, including albums, playlists, CSV and list files, is available under **New job**. The WebUI works okay on phones.
- **Search modes.** File, Track, Album, Song Groups and Album Groups searches, with live filtering, sorting, and ranking conditions you can adjust for each search.
- **Browse users.** Browse a peer's shared folders, filter them, and download folders or files from the tree.
- **Sharing and uploads.** Share folders with `--share`. Limit uploads with `--upload-slots` and `--upload-speed-limit-kib`, and block users or IP addresses with `--upload-blocked-user` and `--upload-blocked-ip`.
- **Chat.** Private messages and chat rooms, with message history.
- **Dashboard** with transfer history and statistics.
- **Sign-in.** WebUI login with `--web-password-hash` or `--web-password-file`, and API keys for the remote CLI with `--remote-api-key`.
- **Install options.** An apt repository for Debian and Ubuntu, a NixOS module with a binary cache, and a Docker image.
- WebUI themes with `--web-theme` and `--web-color-scheme`.

### Changed

- Album and song ranking is significantly better at picking the right folder or file.
- "Aggregate" mode is now "Groups": `--aggregate` is `--group`, `--aggregate-length-tol` is `--group-length-tol`, and `--min-shares-aggregate` is `--min-group-shares`. The short flag `-g` is unchanged.
- `--input-type soulseek` is now `--input-type soulseek-link`.
- **Album track counts from the source are enforced.** A folder must have exactly as many audio files as the source lists. Turn either limit off with `--no-extract-min-tracks` or `--no-extract-max-tracks`.
- **No format or quality is preferred by default.** 3.x preferred MP3 files between 200 and 2500 kbps, up to 48 kHz.
- Peers are downranked after 2 failed downloads and ignored after 3, instead of 1 and 2.
- **The daemon requires sign-in.** The remote CLI needs an API key, unless the daemon runs with `--no-auth`.
- I honestly forgot.

### Removed

- `--extract-max-track-count`, which is now the default.
- `--shared-files` and `--shared-folders`. The daemon reports the counts of the folders it actually shares.

## [3.1.0] - 2026-11-19

### Added

- Add SoundCloud track and public or private playlist URLs as inputs. Pass a SoundCloud OAuth token with `--soundcloud-token`. By @fain182 in #219.
- Now retries multi-artist searches (i.e. artist parts containing `;`) with each individual artist when no matches are found. Strict artist matching now accepts any listed artist. #220
- Add `--detach` for remote CLI to submit a remote job, print its workflow ID, and exit without waiting. By @hunortakacs in #218.

### Changed

- **Disable required track-length matching by default**. Length still affects ranking. Use `--length-tol 3` to restore the previous three-second limit.
- List files now use two or more spaces or a tab between fields, so double quotes are no longer strictly necessary for inputs containing spaces (as long as they're not consecutive).
- Rework `--desperate` mode into `--fallback-level` (and `--max-fallback-attempts`). `--desperate` is equivalent to fallback level 3.

### Fixed

- Fix remote profiles with a preconfigured input failing when called without an explicit input.
- Fix `--album-art-only` so it ignores audio filters regardless of option order.
- Fix the daemon exhausting Linux file watches when started from a directory with many files. By @hunortakacs in #217.

[Unreleased]: https://github.com/fiso64/sockseek/compare/v3.1.0...HEAD
[3.1.0]: https://github.com/fiso64/sockseek/releases/tag/v3.1.0
