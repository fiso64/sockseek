# Spectre Rendering Plan

## Goal

Replace cursor-addressed progress bars with a bounded Spectre.Console live section plus normal log lines.

The terminal should behave like `dotnet build` / `dotnet test`:

- completed facts are printed as static log lines and remain in scrollback
- currently running work is shown in a dynamic live section
- the live section is bounded so it cannot overflow the terminal and flicker wildly
- queued/pending work does not create one live row per job

This is intended to fix rendering issues such as bars disappearing, corrupting output, or flickering when the terminal scrolls.

## Rendering Model

The terminal has two conceptual sections:

```text
# Static logs
[id] SongJob: downloaded: Artist - Title -> C:\Music\file.flac
[id] AlbumJob: downloaded: 03 - Track.flac
[id] AlbumJob: completed: Album Name -> C:\Music\Album

# Running jobs, dynamic
20 active, 978 queued, 2 completed, 0 failed

[id] Playlist: downloading: 20 active, 978 queued, 2 completed, 0 failed
[id] SongJob: searching: Artist - Title
[id] SongJob: downloading: Artist - Title 48%
[id] AlbumJob: downloading: Album Name 50% [1/2]
    (5%) InProgress: filename2.flac
```

Static logs are normal console writes. The running section is the only Spectre live renderable.

## Presentation Model

The new renderer should not be built around progress bars.

The main primitives are:

- `JobView`: one currently visible unit of work in the live section
- `JobChildView`: one child row under a parent job, usually an album track or active playlist child
- `TerminalLogLine`: one durable fact that should be printed to scrollback

Example `JobView` shape:

```csharp
sealed record JobView(
    string Id,
    int DisplayId,
    string Kind,
    string Name,
    string State,
    int? Percent,
    int? DoneChildren,
    int? TotalChildren,
    IReadOnlyList<JobChildView> Children,
    string? ParentId,
    bool IsParentSummary);

sealed record JobChildView(
    string Id,
    string State,
    string Name,
    int? Percent);
```

`JobView` is presentation state, not engine state. It is allowed to be simpler and more opinionated than the underlying job graph.

The renderer receives snapshots of `JobView`s and decides which rows fit in the bounded live region.

Example `TerminalLogLine` shape:

```csharp
enum TerminalLogKind
{
    JobSucceeded,
    JobFailed,
    JobCancelled,
    SongDownloaded,
    SongAlreadyExists,
    SongSkipped,
    SongFailed,
    AlbumTrackDownloaded,
    AlbumTrackSkipped,
    AlbumTrackFailed,
    ExtractedJobs,
    PlaylistCompleted,
    AggregateCompleted,
}

sealed record TerminalLogLine(
    TerminalLogKind Kind,
    string JobId,
    int DisplayId,
    string JobType,
    string Message);
```

One formatter should own the text form of `TerminalLogLine`.

Event handlers should update `JobView` state and emit structured `TerminalLogLine`s rather than writing progress strings directly.

## Core Rules

- Do not render `Pending` jobs as individual live rows.
- A job becomes a live row only when it has meaningful active state, such as extracting, searching, downloading, retrieving, moving, deleting, or on-complete.
- When a job reaches a terminal state, remove it from the live section and emit a static log line.
- Completed child rows become static log lines and leave the live section.
- The live section should show current work, not historical work.
- `--no-progress` should bypass live rendering and keep plain output behavior.
- Redirected output should bypass live rendering.
- `--progress-json` remains separate and untouched.

## Spectre Guidelines

- Use `Spectre.Console.Live`.
- Do not nest Spectre `Status`, `Progress`, or `Live`.
- One renderer task should own all console writes in live mode.
- Workers/reporters emit events or update presentation state; they do not write directly to the console.
- Use `Rows`, `Grid`, or no-border tables for the dynamic section.
- Use a bounded live renderable:
  - `VerticalOverflow.Crop`
  - a computed max height, roughly `terminal height - 8`
  - custom summarization before relying on raw crop
- Use `AutoClear(true)` or equivalent final cleanup so no stale dynamic section remains at exit.

## Concurrency Implications

Current engine behavior:

- `ConcurrentJobs` defaults to `int.MaxValue`.
- `ConcurrentSearches` defaults to `2`.
- A large `JobList`, such as a 1000-song playlist, fans out child `ProcessJob` tasks.
- `SearchSong` updates a song to `Searching` before waiting for the search concurrency semaphore.
- Therefore, with unlimited job concurrency, a 1000-song playlist can produce 1000 apparent searching jobs even though only 2 searches are actively running.

Decision:

- Lower default `ConcurrentJobs`, probably to `20`.
- Keep `ConcurrentSearches` separate.
- Treat the default mental model as something like: 20 active jobs, 2 active network searches.
- The renderer should show queued counts, not queued rows.

Example:

```text
20 active, 978 queued, 2 completed, 0 failed

[005] SongJob: searching: Artist - Title
[006] SongJob: downloading: Artist - Title 48%
...
```

Albums are a special case:

- An `AlbumJob` consumes one job slot.
- Song children inside an album do not consume job slots.
- One album can still produce many child rows.
- Album rendering must therefore be bounded and summarized independently.

## Large Playlist Behavior

For a playlist with many songs:

- show a parent playlist/job-list row when there is a meaningful parent name
- show global and/or parent queue counts
- do not show each queued song
- show active child jobs under the parent when useful
- preserve stable ordering rather than recently-updated jumping

Preferred ordering:

- group by parent
- show parent summary rows first
- order children by display id
- keep active downloads more visible than search-only rows when overflow forces prioritization

If multiple large parents are running:

- reserve rows for parent summaries
- fill remaining rows with active children
- show omitted counts when children cannot fit

## Overflow Behavior

The live section must never become unbounded.

When rows exceed available height:

- keep the running summary line
- keep parent rows
- prefer active downloads over search-only rows
- include an explicit omitted count

Example:

```text
20 active, 978 queued, 2 completed, 0 failed

[010] Playlist: downloading: 20 active, 978 queued, 2 completed, 0 failed
... 14 active rows hidden ...
[032] SongJob: downloading: Artist - Title 72%
[033] AlbumJob: downloading: Album Name 50% [1/2]
    (5%) InProgress: filename2.flac
```

## Static Log Policy

Static logs are deliberately smaller than `--no-progress` output.

Print static log lines when:

- a job leaves the running section
- a child item completes, fails, or is skipped while its parent remains running
- important detail cannot fit naturally in a live row

Do not log ordinary progress transitions:

- queued
- searching
- resolving
- downloading
- percent changes
- transient state changes that are visible in the live row

### SongJob Logs

```text
[id] SongJob: downloaded: Artist - Title -> C:\Music\file.flac
[id] SongJob: already exists: Artist - Title
[id] SongJob: skipped: Artist - Title: reason
[id] SongJob: failed: Artist - Title: no suitable file found
[id] SongJob: cancelled: Artist - Title
```

For standalone successful songs, include the final destination path.

### AlbumJob Logs

```text
[id] AlbumJob: downloaded: 03 - Track.flac
[id] AlbumJob: skipped: 04 - Track.flac: already exists
[id] AlbumJob: failed track: 05 - Track.flac: reason
[id] AlbumJob: completed: Album Name -> C:\Music\Album
[id] AlbumJob: failed: Album Name: 8/10 tracks downloaded
[id] AlbumJob: cancelled: Album Name: 3/10 tracks downloaded
```

For album children, prefer filename-only logs rather than full paths.

### ExtractJob Logs

```text
[id] ExtractJob: extracted 14 jobs: playlist-name
[id] ExtractJob: failed: reason
[id] ExtractJob: cancelled: playlist-name
```

The `ExtractJob` should only be visible while extracting. Once it produces the result job, it disappears and the result job or playlist summary takes over.

### JobList / Playlist Logs

```text
[id] Playlist: completed: playlist-name: 982 succeeded, 12 already existed, 6 failed
[id] Playlist: failed: playlist-name: reason
[id] Playlist: cancelled: playlist-name: 300/1000 completed
```

For huge playlists where many tracks already exist, prefer a collapsed summary over one line per skipped track, unless verbose output is requested later.

### Aggregate / Folder Logs

```text
[id] AggregateJob: completed: query: 12 succeeded, 2 failed
[id] AggregateJob: failed: query: reason
[id] RetrieveFolderJob: completed: folder name -> C:\Music\...
[id] RetrieveFolderJob: failed: folder name: reason
```

## Album Display

Compact album display:

```text
[id] AlbumJob: downloading: Album Name 50% [1/2]
    (5%) InProgress: filename2.flac
```

- show the album header
- show active/current child rows only
- completed children become static log lines

Non-compact album display:

```text
[id] AlbumJob: downloading: Album Name 50% [1/2]
    (100%) Done: filename1.flac
    (5%) InProgress: filename2.flac
```

Decision:

- completed child rows should still disappear after logging
- non-compact means show more active/relevant children, not preserve completed history

## Already Existing / Skipped Items

For individual standalone jobs:

- log final skipped/already-existing states

For huge playlists:

- collapse large batches of already-existing tracks into a parent summary
- avoid printing hundreds or thousands of low-value static lines by default

## Rate Limiting

Search rate limiting should be shown as a live/global status, not repeated static logs.

Repeated rate-limit log lines would be noisy and would not help understand completed work.

## Interactive Prompts

If an interactive prompt appears while live rendering is active:

- suspend or clear the live section
- show the prompt normally
- resume the live section afterward

The prompt should never compete with a live repaint.

## Final Exit

On normal exit:

- clear the live section
- leave static logs in scrollback
- print a final summary

Example:

```text
Completed: 982 succeeded, 12 already existed, 6 failed
```

Do not leave a stale running section after completion.

## Non-Goals

- Do not make live mode as verbose as `--no-progress`.
- Do not preserve every transient progress state in scrollback.
- Do not render thousands of queued jobs.
- Do not rely on cursor-addressed per-row progress bars.
- Do not preserve the old `IProgressBar` abstraction as the long-term rendering model.
- Do not keep completed child rows in the live section as history.
