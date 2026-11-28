# Options

These are general options and options for one-shot downloads. Daemon, WebUI and remote CLI settings are in [Daemon and remote CLI](daemon-options.md).

<!-- sockseek-help:start(main) -->

## Soulseek Account and Session

```
--user <username>               Soulseek username (required)
--pass <password>               Soulseek password (required unless --password-file is set)
--password-file <path>          Read the Soulseek password from a file
--listen-port <port>            Port for incoming connections (default: 49998)
--no-listen                     Disable the incoming connection listener
--connect-timeout <ms>          Timeout used when logging in to Soulseek (default: 20000ms)
--user-description <desc>       Soulseek profile description
--user-picture <path>           Soulseek profile picture (local image file)
```

## Configuration and Profiles

```
-c, --config <path>             Set config file location. Set to 'none' to ignore config
--no-config                     Ignore any config file
--profile <names>               Configuration profile(s) to use. See `--help config`.
--concurrent-jobs <num>         Max concurrent leaf jobs (default: 20)
--concurrent-searches <num>     Max concurrent Soulseek searches (default: 2)
--concurrent-extractors <num>   Max concurrent input extractors (default: 4)
```

## Input and Output Options

```
<input>                         A URL, search string, Soulseek link, or path to a local
                                CSV/list file. Run `--help input` to view the accepted inputs.
                                Can also be passed with -i, --input <input>
-o, --output-dir <path>         Download directory
--input-type <type>             [csv|youtube|spotify|bandcamp|string|list|soulseek-link|
                                musicbrainz] (default: auto)
-s, --song                      Song mode for string input
--name-format <format>          Name format for downloaded tracks. See `--help name-format`
--invalid-replace-str <str>     Replacement string for invalid path characters (default: space)

-n, --number <maxtracks>        Download the first n tracks of a playlist
--offset <offset>               Skip a specified number of tracks
-r, --reverse                   Download tracks in reverse order
--write-playlist                Create an m3u playlist file in the output directory
--playlist-path <path>          Override default path for m3u playlist file
--write-index                   Create/update the Sockseek index (default when using
                                compatible inputs)
--no-write-index                Do not create/update the Sockseek index
--index-path <path>             Override default path for Sockseek index
--no-incomplete-ext             Write directly to the destination during download.

--no-skip-existing              Download items even when index/file checks find an existing match
--skip-mode-output-dir <mode>   How to match files in the output dir: name|tag|index
                                (default: index)
--skip-check-cond               Check file conditions when skipping existing files. If the
                                local candidate does not exist or does not satisfy the
                                required conditions, the item will not be skipped.
--skip-check-pref-cond          Check preferred conditions when skipping existing files
--skip-music-dir <path>         Also skip downloading tracks found in a music library
--skip-mode-music-dir <mode>    How to match files in --skip-music-dir: name|tag
                                (default: name)
--skip-not-found                Skip searching for tracks that weren't found on Soulseek
                                during the last run.

--on-complete <command>         Run a command when a download completes. See `--help
                                on-complete`
```
## Search Options

```
--fast-search                   Begin downloading as soon as a file satisfying the preferred
                                conditions is found. Only for song downloads.
--fast-search-delay <ms>        Delay before accepting fast-search candidates (default: 300)
--fast-search-min-up-speed <n>  Minimum upload speed for fast-search candidates (default: 1)
--remove-ft                     Remove 'feat.' and everything after before searching
--remove-brackets               Remove square-bracketed text from track titles before search
--extract-artist                Extract artist/title from titles like "Artist - Title"
--parse-title <template>        Parse title fields with placeholders like {artist} - {title}
--regex <regex>                 Remove a regexp from all track titles and artist names.
                                Optionally specify a replacement regex after a semicolon.
                                Add 'T:', 'A:' or 'L:' at the start to only apply this to
                                the track title, artist, or album respectively. Prefix with
                                '+ ' to append a regex rule instead of replacing prior rules.
--artist-maybe-wrong            Performs an additional search without the artist name.
                                Useful for sources like SoundCloud where the "artist"
                                could just be an uploader. Note that when downloading a
                                YouTube playlist via URL, this option is set automatically
                                on a per-track basis, so it is best kept off in that case.
--fallback-level <0-3>          Try simpler searches when no results are found.
                                0: normal searches only
                                1: retry with the first artist, without feat./ft. credits
                                2: also try each remaining artist (default)
                                3: also try broader searches for artist/album/title only and
                                relax length requirements.
--max-fallback-attempts <num>   Maximum fallback searches when no results are found (def: 8)
-d, --desperate                 Equivalent to --fallback-level 3
--no-remove-special-chars       Keep special characters in Soulseek search terms
--max-retries <num>             Max download retries per item (default: 10)
--unknown-error-retries <num>   Extra retries for transient errors (default: 2)
--fails-to-downrank <num>       Failures within a workflow before downranking a peer (default: 2)
--fails-to-ignore <num>         Failures within a workflow before ignoring a peer (default: 3)

--yt-dlp                        Use yt-dlp to download songs that weren't found on
                                Soulseek. yt-dlp must be available from the command line.
                                Works on any input containing songs; albums not supported.
                                Runs a YouTube search and downloads the first result. For
                                YouTube inputs, downloads directly by ID instead.
--yt-dlp-argument <str>         The command line arguments when running yt-dlp. Default:
                                "{id}" -f bestaudio/best -o "{savepath-noext}.%(ext)s" -x
                                Available vars are: {id}, {savedir}, {savepath},
                                {savepath-noext}.
                                Note that -x causes yt-dlp to download webms in case ffmpeg
                                is unavailable.

--search-timeout <ms>           Stop a search attempt after this long without an accepted
                                response (default: 5000 for CLI jobs, 15000 for webui searches)
--max-stale-time <ms>           Max download time without progress in ms (default: 30000).
                                Files you pick in Search or peer Shares can wait longer
                                if the peer confirms they’re still queued.
--response-limit <num>          Max accepted peer responses per search attempt (default: 250)
--searches-per-time <num>       Max searches per time interval. Higher values may cause
                                30-minute bans, see `--help notes`. (default: 34)
--searches-renew-time <sec>     Controls how often available searches are replenished.
                                See `--help notes`. (default: 220)
```

## Spotify Options

```
--spotify-id <id>               Spotify client ID
--spotify-secret <secret>       Spotify client secret
--spotify-secret-file <path>    Read the Spotify client secret from a file
--spotify-token <token>         Spotify access token
--spotify-refresh <token>       Spotify refresh token
--remove-from-source            Remove downloaded tracks from source playlist
```

## YouTube Options

```
--youtube-key <key>             YouTube Data API key
--youtube-key-file <path>       Read the YouTube Data API key from a file
--get-deleted                   Attempt to retrieve titles of deleted videos from wayback
                                machine. Requires yt-dlp.
--deleted-only                  Only retrieve & download deleted music.
```

## Bandcamp Options

```
--from-html <path>              Read Bandcamp page HTML from a local file
```

## SoundCloud Options

```
--soundcloud-token <token>      SoundCloud OAuth token
```

## CSV File Options

```
--artist-col <name>             Artist column name
--title-col <name>              Track title column name
--album-col <name>              Album column name
--length-col <name>             Track length column name
--album-track-count-col <name>  Album track count column name (sets --album-track-count)
--yt-desc-col <name>            YouTube description column (improves --yt-parse)
--yt-id-col <name>              YouTube video id column (improves --yt-parse)

--time-format <format>          Time format in Length column of the CSV file (e.g h:m:s.ms
                                for durations like 1:04:35.123). Default: s
--yt-parse                      Enable if the CSV contains YouTube video titles and channel
                                names; attempt to parse them into title and artist names.
--remove-from-source            Remove downloaded tracks from source CSV file
```

## Filtering & Ranking Options

```
--format <formats>              Required file format(s). Comma-separated, unordered. See
                                also --pref-format for soft preferences. Prefix with '+ ' to append.
--length-tol <sec>              Length tolerance in seconds, -1 to disable (default: -1)
--min-bitrate <rate>            Minimum file bitrate
--max-bitrate <rate>            Maximum file bitrate
--min-samplerate <rate>         Minimum file sample rate
--max-samplerate <rate>         Maximum file sample rate
--min-bitdepth <depth>          Minimum bit depth
--max-bitdepth <depth>          Maximum bit depth
--strict-title                  Require track title in filename
--strict-artist                 Require artist in path. When the input artist contains
                                multiple artists split by ;, requires one only.
--strict-album                  Require album in folder path
--exclude-locked                Exclude locked files from search results (default: false)
--banned-users <list>           Users to ignore, comma-separated.
                                Prefix with '+ ' to append one exact name.
--allowed-users <list>          Users to allow, comma-separated.
                                Prefix with '+ ' to append one exact name.
--cond <conditions>             Semicolon-delimited required conditions

--pref-format <formats>         Preferred format(s) for ranking. Use --format to require
                                formats strictly. Comma-separated, unordered. Prefix with '+ ' to append.
--pref-length-tol <sec>         Preferred length tolerance, -1 to disable (default: 3)
--pref-min-bitrate <rate>       Preferred minimum bitrate
--pref-max-bitrate <rate>       Preferred maximum bitrate
--pref-min-samplerate <rate>    Preferred minimum sample rate
--pref-max-samplerate <rate>    Preferred maximum sample rate
--pref-min-bitdepth <depth>     Preferred minimum bit depth
--pref-max-bitdepth <depth>     Preferred maximum bit depth
--pref-strict-title             Prefer filenames containing the track title
--pref-strict-artist            Prefer file paths containing artist name
--pref-strict-album             Prefer folder paths containing album name
--pref-exclude-locked           Prefer unlocked results (default: true)
--pref-banned-users <list>      Users to downrank, comma-separated.
                                Prefix with '+ ' to append one exact name.
--pref-allowed-users <list>     Users to prefer, comma-separated.
                                Prefix with '+ ' to append one exact name.
--pref <conditions>             Semicolon-delimited preferred conditions

--strict-conditions             Skip files with missing properties instead of accepting by
                                default; if --min-bitrate is set, ignores any files with
                                unknown bitrate. Warning: Available props depend on client
```

## Album Download Options

```
-a, --album                     Album mode for string input and string lines in list files.
--upgrade-to-album              Upgrade song-shaped sources such as CSV song rows or Spotify
                                playlist tracks into album jobs when possible.
-t, --interactive               Interactively select folders. See --help shortcuts.
--album-track-count <num>       Specify the exact number of tracks in the album. Add a + or
                                - for inequalities, e.g '5+' for five or more tracks.
                                Spotify/Bandcamp inputs automatically set album-track-count
                                to n+.
--strict-album-quality          Require every audio file in an album folder to satisfy
                                required quality conditions such as --format, bitrate, sample
                                rate, and bit depth. By default mixed-quality folders are
                                ranked by coverage.
--pref-common-track-count       Prefer album folders holding at least the track count most
                                matching folders share, so incomplete folders rank after
                                complete ones (default, except for manual searches and
                                --interactive)
--no-pref-common-track-count    Do not rank album folders by the most common track count
--min-album-track-count <num>   Minimum number of tracks in an album folder
--max-album-track-count <num>   Maximum number of tracks in an album folder
--no-extract-min-tracks         Do not set the minimum album track count from extracted
                                sources
--no-extract-max-tracks         Do not set the maximum album track count from extracted
                                sources. By default extractors that report an album's
                                track count use it as both minimum and maximum.
--album-track-count-max-retries Max retries when album track count fails (default: 5)
--album-art <option>            Retrieve additional images after downloading the album:
                                'default': No additional images
                                'largest': Download from the folder with the largest image
                                'most': Download from the folder containing the most images
--album-art-only                Only download album art for the provided album; implies
                                album-art=largest when album-art is default
--browse-folder                 Automatically browse user shares to get all files in the
                                selected album folder (default)
--no-browse-folder              Do not automatically browse user shares to get all files in
                                the folder
--incomplete-album-action <a>   What to do with completed album files when the album
                                does not complete. Values: 'move' to move to {configured
                                output dir}/failed (default), 'move:<path>' to move to a
                                custom path, 'delete' to delete them, or 'keep' to leave
                                them where they are.
                                A folder downloaded from search results or a peer's
                                shares defaults to 'keep'; --interactive picks do not.
```

## Group Download Options

```
-g, --group                     Group download mode: Find and download all distinct
                                songs associated with the provided artist, album, or title.
--group-length-tol <tol>        Max length tolerance in seconds to consider two tracks or
                                albums equal. (Default: 3)
--min-group-shares <num>        Minimum number of shares of a track or album for it to be
                                downloaded in group mode. (Default: 2)
--relax-filtering               Slightly relax file filtering in group mode to include
                                more results
```

## Printing & Debug Options

```
-v, --verbose                   Print debug logs
-vv, --trace                    Print trace-level debug info
--debug                         Alias for --verbose
--log-file <path>               Write debug logs to a file
--no-progress                   Disable progress bars/percentages, only simple printing
--progress-json                 Print progress events as JSON lines
--print <option>                Print jobs or search results instead of downloading:
                                'jobs': Print input jobs after extraction/preprocessing
                                'jobs-full': Print extended information about input jobs
                                'results': Print search results satisfying file conditions
                                'results-full': Print search results including full paths.
                                'json': Print first result in json format
                                'json-all': Print json of all results in sorted order
                                'link': Print first result slsk:// link
                                'index': Print Sockseek index as formatted json
                                'index-failed': Print failed downloads from Sockseek index
--print-jobs                    Alias for --print jobs
--print-jobs-full               Alias for --print jobs-full
--print-results                 Alias for --print results
--print-results-full            Alias for --print results-full
--print-link                    Alias for --print link
--print-json                    Alias for --print json
--print-json-full               Alias for --print json-all

--mock-files-dir <path>         Directory containing files to simulate download results
--mock-files-no-read-tags       Only read filenames when simulating (much faster)
--mock-files-slow               Simulate slow mock-file downloads and folder browses
--mock-files-fail-downloads <n> Simulate n failed mock-file downloads
```

<!-- sockseek-help:end -->

<!-- sockseek-help:start(cli-notes) -->
## CLI Notes

- Flags can be explicitly disabled by setting them to false, e.g. `--interactive false`.
- Single-character flags can be combined, e.g. `-at` for `-a -t`.
- Some acronyms of two- and `--three-word-flags` like `--twf` are also accepted. E.g. `--Mbr` for `--max-bitrate`.

<!-- sockseek-help:end -->
