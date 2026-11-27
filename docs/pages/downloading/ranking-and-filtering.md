<!-- sockseek-help:start(ranking-and-filtering) -->

# Ranking and filtering

Preferred (`pref-*`) options change how results are **ranked**; they never filter anything out. `--pref-format flac` means Sockseek will prefer flac when available, but will still download mp3 if no flac is found. By contrast, conditions without the `pref` prefix are mandatory. For example, `--format flac` will reject non-flac files entirely.

## Filtering

The only default required conditions are for the files to be audio:

```ini
format = mp3,flac,ogg,m4a,opus,wav,aac,alac
```

This only applies for music-oriented search modes (track mode, album mode, etc); generic file searches in the webui start with no filters at all.

## Ranking

The preferred conditions determine ranking. The defaults are:

```ini
# Prefer songs whose duration differs from the supplied one
# by at most 3 seconds.
pref-length-tol = 3
# Prefer files whose paths contain the exact title and album name.
# Changing these is not recommended.
pref-strict-title = true
pref-strict-album = true

# Prefer results that aren't locked. This is only relevant for
# interactive searches in the WebUI. Automated downloads always
# ignore locked items.
pref-exclude-locked = true
```

When results differ in several preferences, they're ranked in this order:

1. Not from a downranked user (`pref-banned-users`)
2. Exact title, then exact album name, then exact artist name
3. Length within tolerance (for songs)
4. From a preferred user (`pref-allowed-users`)
5. Format, then bitrate, sample rate and bit depth
6. Not locked

For example, an MP3 in a folder named after the album ranks above a FLAC in a folder that isn't.

> [!WARNING]
> The order of formats in a list has no effect. Both `--pref-format mp3,flac` and `--pref-format flac,mp3` give MP3 and FLAC equal preference.

For debugging, you can run a song search with `--print results-full` to reveal the sorting logic.

## Albums

For album downloads, conditions like format, bitrate and sample rate operate based on coverage: A folder with 9 FLAC files and 1 MP3 is preferred over a mostly-MP3 folder, and the selected folder is still downloaded as a whole. Use `--strict-album-quality` to require every audio file in the folder to satisfy the conditions.

## Setting conditions

See [the options reference](../reference/options.md#filtering-ranking-options) for all filtering and ranking options.

Conditions can also be supplied as a semicolon-delimited string with `--cond` and `--pref`, e.g.:

```bash
--cond "br>=320; format=mp3,ogg; sr<96000; album-track-count>=8"
```

File Search in the UI starts with no file conditions. To set defaults for a particular search mode, use an auto-profile with an `input-type` or a `download-mode` condition. See [search defaults](../using-sockseek/configuration.md#webui-search-defaults).

## Unknown metadata

Some info may be unavailable depending on the client used by the peer (like the bitrate). This is why if you set a `--min-bitrate`, Sockseek will still accept any file with **unknown** bitrate as a precaution.

To reject all files where one or more of the checked properties is null (unknown), use `--strict-conditions`.

This flag should be used with care: It's easy to accidentally exclude all files from users with
certain clients. For example, because the standard Soulseek client does not broadcast the bitrate,
enabling `--strict-conditions` and setting a `--min-bitrate` will effectively exclude all files shared by users with the standard client(!).

<!-- sockseek-help:end -->
