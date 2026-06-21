# Sockseek 3.0-dev14 Manual Test Fix Tracking

## In Progress

- None.

## Fixed In Working Tree

- SS3-004: final file placement failures now fail the candidate/job instead of reporting success; focused song and album regression tests added.
- SS3-018: daemon listen IP is validated before Kestrel host construction; invalid IPs are rejected and IPv6 URLs are bracketed correctly.
- SS3-001: Linux/macOS tar.gz packaging now writes explicit Unix file modes; `sockseek` is archived as executable (`0755`).
- SS3-003: name-formatted downloads now use per-job staging paths and publish duplicate-cache entries only after final organization; concurrent duplicate rows with unique `{snum}` outputs are covered by regression test.
- SS3-005 exit-code contract: CLI now returns explicit process exit codes: `0` success, `1` valid invocation with failed work, `2` usage/config/startup validation error, `130` cancellation. Added CLI-level regression tests and shell-checked invalid flag exit code.
- SS3-005 diagnostics stream cleanup: startup/parser errors now route to stderr instead of stdout, `--progress-json` remains JSONL-only on stdout, and unhandled CLI/startup/remote/diagnostic failures log concise summaries instead of default stack traces.
- SS3-006: completely empty or blank-only CSV files now terminate as empty job lists instead of spinning while looking for a header row; focused extractor regression test added.
- SS3-007: album-art-only runs now derive the album terminal outcome from image jobs; largest-image selection no longer treats an empty current image set as already satisfied for small covers. Added regressions for successful image-only album downloads and for normal album downloads where optional album art fails without failing the album.
- SS3-008: `--progress-json` now routes human console logs to stderr from startup, keeping stdout as JSONL-only; added CLI-level stdout parsing regression.
- SS3-011: top-level `SongJob` now participates in `Preprocessor.PreprocessJob`; direct song searches get the same `remove-ft`, bracket removal, and regex preprocessing as songs from containers. Added regression test.
- SS3-012: `when=already-exists` on-complete actions now run from the skip-existing engine path before terminal commit; added engine regression for a skipped existing track.
- SS3-014: malformed Soulseek URIs with missing usernames or empty/root paths are rejected by the extractor before jobs are created; valid file/folder links remain covered.
- SS3-015: invalid numeric/time values from the report are rejected: `--number` must be >= 1, `--offset` must be >= 0, and CSV time formats must use supported units (`h`, `m`, `s`, `ms`) at both CLI and extractor boundaries.
- SS3-013: routine validation and job diagnostic failures no longer expose full exception details at normal local CLI verbosity. Full diagnostic event details are still available with debug/trace logging.
- SS3-016: explicit interactive album skips now use a dedicated manual-skip path instead of completing as `NoSuitableFileFound`; Shift+S skipped album prompts terminalize as `Skipped` with manual skip reason. Added API/backend skip command and strengthened the interactive regression.
- SS3-019: try-next candidate now finds active descendant downloads when called on parent jobs such as job lists, extract results, albums, aggregates, and album aggregates. Added local/remote backend parity coverage for parent job-id and existing display-id try-next paths.
- SS3-017: main generated help now documents the public option groups called out by the report (`--write-index`, `--browse-folder`, print aliases, and preferred strict title/album switches).
- SS3-009: preferred format ranking was rechecked and covered without changing the intended priority: title/album/artist matching remains more important than preferred format, while preferred format still outranks lower-priority tie breakers such as upload speed.
- SS3-020: daemon startup now warns when binding the unauthenticated API to all interfaces (`0.0.0.0` or `::`); loopback bindings remain quiet.
- SS3-021: oversized search submissions are rejected before job/workflow state is created; raw `queryText` and structured search text fields now have bounded lengths.
- SS3-002: redirected/headless daemon startup has been rechecked on the current branch with stdout/stderr redirected; the daemon opened `/api/server/info` successfully and daemon mode does not start the console-input loop.

## Still Pending

- SS3-010: reverted the browse-time file filtering fix because automatic album downloads should not partially filter retrieved folders. Proper handling should treat required album audio-quality conditions (`format`, `bitrate`, `samplerate`, `bitdepth`) as folder-level coverage/ranking/rejection, then download the selected folder as a whole.

## Checked But Not Yet Closed

- None.
