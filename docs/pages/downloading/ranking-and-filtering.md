<!-- sockseek-help:start(ranking-and-filtering) -->

# Ranking and filtering

Sockseek supports filter as well as ranking configuration. 

A required condition filters files from search results. For example, `--format flac` will reject non-flac files entirely.

Ranking (or "preferred") conditions start with the `pref-` prefix and change how results are **ranked**; they never filter anything out. Ranking affects how results are sorted when using "Relevance" sorting in the GUI, and how results are chosen in [automated downloads](./auto-downloads.md). For example, in an automated download,`--pref-format flac` means Sockseek will prefer flac when available, but will still download mp3 if no flacs are found.

## Filtering

The only default required conditions are for the files to be audio:

```ini
format = mp3,flac,ogg,m4a,opus,wav,aac,alac
```

This only applies for music-oriented search modes (song mode, album mode, etc); generic file searches in the GUI start with no filters at all.

## Ranking

The preferred conditions determine ranking. The defaults are:

```ini
# Prefer songs whose duration differs from the supplied one
# by at most 3 seconds.
pref-length-tol = 3

# Prefer files whose paths contain the exact title and album name,
# surrounded by boundary characters like punctuation (see below). 
# Disabling these is usually not recommended.
pref-strict-title = true
pref-strict-album = true

# Prefer results that aren't locked. This is only relevant for
# interactive searches in the WebUI. Automated downloads always
# ignore locked items.
pref-exclude-locked = true
```

When results differ in several preferences, they're ranked in this order:

1. Not from a downranked user (`pref-banned-users`)
2. Strict title, then strict album name, then strict artist name
3. For songs, Length within tolerance
4. From a preferred user (`pref-allowed-users`)
5. Format, then bitrate, sample rate and bit depth
6. Not locked

For example, with `pref-strict-title` enabled and `pref-format=flac`, an MP3 file that contains the song name still ranks above a FLAC file that doesn't.

> [!WARNING]
> The order of formats in a list has no effect. Both `--pref-format mp3,flac` and `--pref-format flac,mp3` give MP3 and FLAC equal preference.

For debugging, you can run a song search with `--print results-full` to reveal the sorting logic.

## Albums

For album downloads, conditions like format, bitrate and sample rate operate based on coverage: A folder with 9 FLAC files and 1 MP3 is preferred over a mostly-MP3 folder, and the selected folder is still downloaded as a whole. Use `--strict-album-quality` to require every audio file in the folder to satisfy the conditions.

## Strict-artist, strict-album, strict-title

These check that the result filepaths contain what you searched for, as a whole name rather than part of a longer one:

- **Artist** must appear anywhere in the file's path.
- **Album** must appear in the path. The file name doesn't count.
- For songs, **Title** must appear in the file name (without its extension).

Each comes as a requirement (`strict-title`, `strict-artist`, `strict-album`) and as a preference (`pref-strict-title`, `pref-strict-artist`, `pref-strict-album`). Casing and accents are ignored, and `_` counts as a space, as do characters that can't appear in file names on some systems, like `:` and `?`.

**Artist** must be whole words, with a space or punctuation on each side (or the start/end of the text). `Joe Yamanaka` matches `Joe Yamanaka - Best`, but not `Joe Yamanakas`.

**Title and album** are stricter: only punctuation (such as `-`, `(`, `)`, `[`, `]`, `.`, `,`), a folder separator, or the start or end of the text may surround them. A space alone is not enough (except after a number such as a track number or year). For `Song name` and `Ashita no Joe`:

- `01 Song name.flac`, `Artist - Song name (Live).flac`, `Artist - Ashita no Joe [FLAC]` and `1980 Ashita no Joe` match.
- `Song name II.flac` and `Ashita no Joe 2 Soundtrack` do not, because `II` and `2 Soundtrack` continue the name.

Some cases are handled specially:

- **Several artists** separated by `;` match if any one of them appears. Featured artists (`feat.`) are optional.
- **Self-titled songs and albums**, where the album has the artist's name, would match every folder of that artist. For these, the album folder must contain nothing but the name, apart from bracketed text, years and punctuation. `Led Zeppelin (Remaster)` matches, but `Led Zeppelin II` doesn't. Note that for Sockseek to be able to recognize that your input is self-titled in the first place, you must fill in both the artist and album/song inputs when searching.

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
certain clients. For example, because the SoulseekQt client does not broadcast the bitrate,
enabling `--strict-conditions` and setting a `--min-bitrate` will effectively exclude all files shared by users with the standard client(!). 

<!-- sockseek-help:end -->
