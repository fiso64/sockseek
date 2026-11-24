<!-- sockseek-help:start(on-complete) -->

# On-Complete Actions

The `--on-complete` option allows executing commands when a file or folder download finishes. Multiple actions can be chained using the `+ ` prefix (note the space after +).

**Syntax:** `--on-complete [options] -- command`

Hint: You can use `--mock-files-dir` to test your commands (see [Testing Options](./tips.md#testing-options)).

Every on-complete command must include the `--` delimiter. Sockseek options go before it; everything after it is the command passed to the OS.

When passing an on-complete action on the command line, quote the whole value so the delimiter is part of the `--on-complete` argument:

```bash
--on-complete "when=success scope=folder -- notify-send \"Downloaded\" \"{path}\""
```

## Options

- `when=completed` - Execute on success or fail, if the item wasn't skipped
- `when=success` - Execute only for successful downloads
- `when=failure` - Execute for failed or partially successful downloads
- `when=skipped` - Execute for skipped jobs
- `when=already-exists` - Execute only for already-existing skipped jobs
- `when=not-found-last-time` - Execute only for not-found-last-time skipped jobs
- `when=cancelled` - Execute only for cancelled jobs
- `when=any` - Execute for every terminal outcome
- `scope=file` - Run for each file, including files in a folder or album download.
- `scope=folder` - Run once for the whole folder or album.
- Omit `scope` to run for both. A successful three-file folder triggers a `when=success` command four times.
- `hidden` - Hide the command window
- `shell` - Use shell execute
- `lock` - Serialize this action across jobs
- `update-index` - Read stdout only if the command exits with code 0. Use `success;new_path` to record success and the new path, `failed` to record failure and clear the path, or `ignored;new_path` to update only the path.

If `when=` is omitted, it behaves like `when=completed`. This preserves the usual "run when work completed" behavior while avoiding commands for already-existing or not-found-last-time skips.

Chained actions evaluate `when=` and outcome/path variables against the result of the preceding action. For example, if an `update-index` action changes a failure to success, the next `when=success` action runs and sees the updated path.

## Command-output variables

See the shared [Variables](../reference/variables.md) reference, also available with
`sockseek --help variables`. On-complete commands additionally provide:

```
is-audio            true for an audio file; false otherwise
path                Absolute downloaded file or directory path
path-noext          Absolute downloaded path without its final file extension

terminal-outcome    Succeeded, Failed, Skipped, Cancelled, or PartialSuccess
skip-reason         AlreadyExists, NotFoundLastTime, Manual, or Filtered;
                    None when the outcome is not a skip or has no specific reason
failure-reason      None, InvalidSearchString, OutOfDownloadRetries,
                    AllDownloadsFailed, Other, ExtractionFailed, Cancelled,
                    ChildJobsFailed, NoSearchResults, or NoMatchingResults

exitcode            Previous command's exit code
stdout              Previous command's stdout
stderr              Previous command's stderr
first-exitcode      First command's exit code
first-stdout        First command's stdout
first-stderr        First command's stderr
```

Sockseek captures stdout/stderr for ordinary on-complete commands, so chained commands can use output variables from the previous ones. Commands launched with `shell` cannot use stdout/stderr.

For folder actions on album jobs, tag-based variables such as `{title}`, `{artist}`, and `{album}` are read from the first audio file in the album.

## Examples

This auto-profile applies to album downloads. The folder scope sends one Linux desktop notification when the whole album succeeds.

```ini
[album-notification]
profile-cond = album
on-complete = when=success scope=folder -- notify-send "Downloaded: {album}" "{path}"
```

Search album art with [Cover Fetcher](https://github.com/fiso64/cover-fetcher):

```ini
on-complete = when=success scope=folder hidden -- cmd /c start "" "path\to\CoverFetcher.exe" --from-dir "{path}"
```

Queue downloaded audio files in foobar2000:

```ini
on-complete = when=success scope=file hidden -- cmd /c if {is-audio}==true start "" "path\to\foobar2000.exe" /immediate /add "{path}"
```

Convert downloaded audio files to MP3 on Windows (requires ffmpeg):

```ini
# Check if file is audio and not already MP3
on-complete =   when=success hidden -- cmd /c if "{is-audio}"=="true" if /i not "{ext}"==".mp3" if not exist "{path-noext}.mp3" echo true

# Convert to MP3 if check passed
on-complete = + when=success hidden -- cmd /c if /i "{stdout}"=="true" (ffmpeg -i "{path}" -q:a 0 "{path-noext}.mp3" && echo success)

# Delete original and update index if conversion succeeded
on-complete = + when=success hidden update-index -- cmd /c if /i "{stdout}"=="success" (del "{path}" & echo "ignored;{path-noext}.mp3")
```

<!-- sockseek-help:end -->
