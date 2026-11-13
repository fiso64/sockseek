# Sockseek

Sockseek is a command-line downloader for Soulseek. Point it at a search query, Spotify or YouTube playlist, CSV file, or any other supported input; it searches the network, ranks candidate files using your preferences, and downloads the best match (automatically or interactively). It is scriptable and configurable, and can run either as a one-shot CLI tool or as a persistent daemon.

This project was formerly named `sldl` (and `slsk-batchdl` before that). See [here](https://github.com/fiso64/sockseek/releases/tag/v3.0.4) for why it was renamed to something dumb.

## Quick Start

1. Download a release for your OS from the [releases page](https://github.com/fiso64/sockseek/releases).

2. Create a config file named `sockseek.conf` in one of these locations:

   - Linux/macOS/Windows: `~/.config/sockseek/sockseek.conf`
   - Windows: `%APPDATA%\sockseek\sockseek.conf`

    Minimal config:

   ```ini
   username = your-soulseek-username
   password = your-soulseek-password
   output-dir = path/to/your/download/folder
   # Optional: prefer FLAC while keeping other formats as fallbacks:
   # pref-format = flac
   ```

    If you're running another Soulseek client, use a **separate Soulseek account** to avoid connection problems.

3. Download your first song:

   ```bash
   sockseek "Artist - Song Title" -s
   ```

4. Or download an album interactively (`-t`):

   ```bash
   sockseek "Artist - Album Title" -t
   ```

> [!NOTE]
> Sharing, uploads, private messages, chatrooms, and notifications are available
> through [daemon mode](#daemon--remote-mode). A normal one-shot download command
> does not stay online to serve shares or receive chat messages.

## Common workflows

### Download a song

Use the `--song`/`-s` flag:

```bash
sockseek "Song Title" -s
sockseek "Artist - Song Title" -s
```

The hyphen ` - ` determines what part of the input is the artist and title, which can be important for [ranking and filtering](#file-conditions) (see also [search string syntax](#search-string)).

### Download an album

String inputs are treated as album downloads by default:

```bash
sockseek "Album Title"
sockseek "Artist - Album Title"
```

As in the song case, prefer to separate artist from album title with ` - ` when providing both in the query.

### Download an album interactively

Add the `--interactive`/`-t` flag to pick albums before downloading:

```bash
sockseek "Artist - Album Title" -t
```

### Download a playlist

Download playlists from streaming services or local list files like so:

```bash
sockseek "https://www.youtube.com/playlist?list=blah"
sockseek "https://open.spotify.com/playlist/id"
sockseek "/path/to/tracks.csv"
```

See [input types](#input-types) for all supported sources. It's a good idea to check the metadata of the first few tracks before downloading your 10'000 song megalist:

```bash
sockseek "your-input" -n 20 --print jobs-full
```

### Download all albums by an artist interactively

[Album Groups mode](#album-groups) (`-g`) ca grouped results by album, giving you several folder choices for each one. Albums which are shared the most are sorted first. In interactive mode (`-t`), you can pick which albums to download and which particular version to use. Run it like this:

```bash
sockseek "artist=Artist Name" -gt
```

Note that this may also include compilations the artist appeared in.

### Prefer lossless, but accept other formats

You can make it prefer lossless files while still accepting other formats when no lossless version exists, like this:

```bash
sockseek "Artist - Album Title" --pref-format flac,wav
```

To ban lossy files altogether, use `--format` instead of `--pref-format`. Note that format lists are unordered; `flac,wav` is equivalent to `wav,flac` and does not change the sorting. See [file conditions](#file-conditions) for more information about ranking and filtering downloads.

### Skipping existing items

For multi-item inputs like YouTube or Spotify playlists and CSV or list files, Sockseek automatically writes an index file next to the downloads, which means that **successfully downloaded tracks and albums will be skipped on next runs**. If you want a shared index, use the `--index-path` option or config field.

Sockseek can also skip items found in a music library, without any pre-existing index files: 

```bash
sockseek "playlist.csv" --skip-music-dir "path/to/music"
```

### More examples

More niche examples can be found [here](#other-examples).

## About failed or incorrect downloads

Sockseek searches the Soulseek P2P network. Sources like Spotify and YouTube are only used as metadata sources; no audio is downloaded from them. It usually does a good job downloading items, provided they actually exist on the network. The results returned by Soulseek of course depend on spelling, peers, etc. Read [this section](#why-are-some-of-my-downloads-wrong-or-missing) if you're dealing with missing or incorrect downloads.

## Index

- [Input types](#input-types)
  - [CSV file](#csv-file)
  - [YouTube](#youtube)
  - [Spotify](#spotify)
  - [Bandcamp](#bandcamp)
  - [MusicBrainz](#musicbrainz)
  - [SoundCloud](#soundcloud)
  - [Soulseek Link](#soulseek-link)
  - [Search string](#search-string)
  - [List file](#list-file)
- [Download modes](#download-modes)
  - [Song](#song)
  - [Album](#album)
  - [Album Groups](#album-groups)
  - [Song Groups](#song-groups)
- [Daemon / remote mode](#daemon--remote-mode)
- [Configuration](#configuration)
- [File conditions](#file-conditions)
- [Name format](#name-format)
- [On-Complete Actions](#on-complete-actions)
- [Shortcuts \& interactive mode](#shortcuts--interactive-mode)
- [More Examples](#other-examples)
- [Tips](#tips)
- [Options reference](#options-reference)
- [Docker](docs/docker.md)

<!-- sockseek-help:start(input) -->

## Input types

The input type is usually determined automatically. You can also manually set it with `--input-type`.  

The following input types are supported:

### CSV file

A path to a local CSV file listing songs or albums. 

In the CSV file, rows with a track title are song downloads, and rows without a title are treated as album downloads. Only a title or album column is required in the CSV file (extra info may improve search results). If the columns have common names (like 'Artist', 'Title', 'Album', 'Length', etc) then it's not required to manually specify them, otherwise you must provide at least `--title-col` or `--album-col`.   

See [CSV options](#csv-file-options) for all options.

### YouTube

## Index
 - [Input types](#input-types)
   - [CSV file](#csv-file)
   - [YouTube](#youtube)
   - [Spotify](#spotify)
   - [Bandcamp](#bandcamp)
   - [MusicBrainz](#musicbrainz)
   - [Soulseek Link](#soulseek-link)
   - [Search string](#search-string)
   - [List file](#list-file)
 - [Download modes](#download-modes)
   - [Song](#song)
   - [Album](#album)
   - [Album Groups](#album-groups)
   - [Song Groups](#song-groups)
 - [Daemon / remote mode](#daemon--remote-mode)
 - [Configuration](#configuration)
 - [File conditions](#file-conditions)
 - [Name format](#name-format)
 - [Variables](#variables)
 - [On-Complete Actions](#on-complete-actions)
 - [Shortcuts \& interactive mode](#shortcuts--interactive-mode)
 - [Examples](#examples)
 - [Notes](#notes)
 - [Tips](#tips)
 - [Options reference](#options-reference)
 - [Docker](docs/docker.md)

The default method to retrieve playlists might not reliably return all videos. If you're having issues, you can use the official API by providing a key with `--youtube-key`. A key can be obtained at https://console.cloud.google.com (create a new project, click 'Enable API' and search for 'YouTube Data', then follow the prompts).

See [YouTube options](#youtube-options) for all options.

### Spotify

Spotify inputs support:

- Public and private playlist URLs
- Album URLs
- Liked songs, with `sockseek spotify-likes`
- Liked albums, with `sockseek spotify-albums`

Spotify API access now requires your own Spotify developer application for all Spotify inputs,
including public playlists. Spotify also requires the owner of that application to have an
active Spotify Premium subscription. If you do not have Premium, you can export the Spotify playlist
with a Spotify-to-CSV converter and pass the CSV file to Sockseek instead.

See [Spotify options](#spotify-options) for all options.

#### Using Credentials

<details>
  <summary>Click to expand</summary>

Create a Spotify application at https://developer.spotify.com/dashboard/applications with a redirect URL http://127.0.0.1:48721/callback. The Spotify account that owns the application must have an active Premium subscription. Obtain an application ID and secret from the created application dashboard.

For public playlists and albums, pass the application credentials:

```bash
sockseek "https://open.spotify.com/playlist/id" --spotify-id 123456 --spotify-secret 123456
```

For private playlists, liked songs, liked albums, or `--remove-from-source`, start Sockseek with the obtained credentials and an action that requires auth to trigger the Spotify app login flow:

```bash
sockseek spotify-likes --spotify-id 123456 --spotify-secret 123456 -n 1 --print jobs
```

Sockseek will try to open a browser automatically but will fall back to logging the login flow URL to output. After login flow is complete it will output a token and refresh token and finish running the current command.

To skip requiring login flow every time Sockseek is used the token and refresh token can be provided (hint: store this info in the config file to make commands less verbose):

```bash
sockseek spotify-likes --spotify-id 123456 --spotify-secret 123456 --spotify-refresh 123456 --spotify-token 123456 -n 1 --pt
```

spotify-token access is only valid for 1 hour. spotify-refresh will enable Sockseek to renew access every time it is run (and can be used without including spotify-token)

</details>

### Bandcamp

Bandcamp inputs support:

- Track URLs
- Album URLs
- Artist URLs, to download all albums by the artist.
- Wishlist URLs, to download all albums on the wishlist.

Extraction might fail due to Cloudflare; download the HTML to a local file and point Sockseek to it using `--from-html` in case of issues.

<!-- Uncomment if it gains more than one option: See [Bandcamp options](#bandcamp-options) for all options. -->

### MusicBrainz

MusicBrainz inputs support:

- `/release/...` URLs
- `/release-group/...` URLs, where it picks the most common version of the album.
- `/collection/...` URLs, to download a list of albums.

### SoundCloud

SoundCloud inputs support:

- Track URLs
- Public and private playlist URLs

Requires a SoundCloud OAuth token, passed with `--soundcloud-token`, also for public items.

When a track has no publisher artist, the artist is taken from titles like "Artist - Title", falling back to the uploader's name.

<!-- Uncomment if it gains more than one option: See [SoundCloud options](#soundcloud-options) for all options. -->

#### Getting the OAuth token

<details>
  <summary>Click to expand</summary>

1. Log in to https://soundcloud.com in your browser.
2. Open the developer tools (F12) and go to the Storage tab (Application in Chrome).
3. Under Cookies > https://soundcloud.com, copy the value of `oauth_token`.

Pass it with `--soundcloud-token`, or put it in your config file:

```
soundcloud-token = <your-token>
```

</details>

### Soulseek Link
A direct path starting with `slsk://`. By default, a path ending in `/` is an ordinary
folder download and a path without it is an ordinary exact-file download. Use `--song`
or `--album` to request music-specific handling. `--song` rejects directory links;
`--album` treats the supplied remote path as the selected album directory, whether or
not it has a trailing slash.

### Search string

A string input is a name of the track, album, or artist to search for. They come in these forms:

1. 
   Arbitrary search queries (like what you would type in the Soulseek search bar), e.g. `sockseek "blah blah"`.  

   _This is equivalent to `sockseek "album=blah blah"` by default, and to `sockseek "title=blah blah"` when using `-s`/`--song` mode._

2.   
   An artist-album or artist-song combination, separated by a hyphen with spaces ` - `:

   - `sockseek "Artist - Album"` downloads an album
   - `sockseek "Artist - Song" -s` downloads a song
  
   It's always better to split the artist from song/album like this when you want to provide an artist due to [ranking and filtering](#file-conditions).  

   _This is equivalent to `sockseek "artist=Artist, album=Album"` in the first case, and to `sockseek "artist=Artist, title=Song"` in the second case._

3.   
   A comma-separated list of properties, like `title=Song Name, artist=Artist Name, length=215`.  
   With an `album` field (and no `title`) it's treated as an album download. With a `title` field it's treated as a song download (even if it includes an `album` field).

Accepted fields are:
- Song title `title`
- Artist name `artist`
- Album name `album`
- Song `length` (in seconds)
<!-- Technically supports two others, but album-track-count is better passed as a condition, and artist-maybe-wrong is quite niche, need to think how to explain it. -->

Notes:
- You can combine the second and third form, e.g. `Artist - Song, length=123` is the same as `artist=Artist, title=Song, length=123` in `--song` mode.
- You can use `"artist=Artist, title=Song" --album` if you want `title=` to act as an album search hint, i.e. you want to search for an album by the name of one of its tracks.

### List file

A path to a local text file listing items of any supported input type. List inputs **must be manually activated** with `--input-type=list`.

Each line in the list file must be of this form:

```
Any input type                conditions (optional)           pref. conditions (optional)
```

Here is an example list file:

```yaml
"Artist - Album"                "strict-album=true; album-track-count>=13"   "br >= 320"

# Treat string input as song download with the s: prefix
s:"Artist - Song"               strict-title=true

# Any other input type is also accepted:
path/to/tracks.csv
https://www.youtube.com/playlist?list=blah
```

The conditions are added on top of the configured conditions and can be omitted.
For string lines, unprefixed entries use the configured download mode: album by default, or song
mode when `--song` / `song = true` is set. Lines starting with `#` are treated as comments and ignored.

<!-- sockseek-help:end -->

<!-- sockseek-help:start(download-modes) -->

## Download modes

Sources usually decide for themselves whether they contain songs or albums. String inputs are treated as albums by default. There are four main download modes:

### Song

Song mode is the default for playlists from streaming platforms and CSV song lists, and downloads one file per item. Use `-s/--song` for string/list input that should be treated as a song search.

Example:

```bash
sockseek "Artist - Song Name" -s
```

### Album

In Album mode, Sockseek downloads whole folders. It is the default for string inputs and album sources such as Spotify/Bandcamp album links and CSV album rows. Use `-t` to pick interactively.

Example:

```bash
sockseek "Artist - Album Name" -t
```

See [Album options](#album-download-options) for album-specific options.

### Album Groups

Album Groups mode is activated when `-g/--group` is enabled for album inputs. In this mode, Sockseek will attempt to group folders returned by Soulseek and download one of each distinct album, starting with the one shared by the most peers.

Note that `--min-group-shares` is set to 2 by default, meaning that albums shared by only one user will be ignored. Album Groups mode can be used to determine the most common version of a particular album, or download the most popular (or all) albums by an artist. It's recommended to pair it with `-t`/`--interactive`.

Example:

```bash
sockseek "artist=Artist Name" -gt
```

See [Grouping options](#group-download-options) for group options.

### Song Groups

Song Groups mode is enabled with `--group --song` (or `-gs` for short). Sockseek will attempt to group files returned by Soulseek and download one of each distinct song, starting with the one shared by the most peers.

As in Album Groups mode, `--min-group-shares` is set to 2 by default, meaning that songs shared by only one user will be ignored. This mode is useful for downloading all songs by an artist available on Soulseek.

Example:

```bash
sockseek "artist=Artist Name" -gs
```

See [Grouping options](#group-download-options) for group options.

<!-- sockseek-help:end -->

<!-- sockseek-help:start(daemon) -->

## Daemon / remote mode
Daemon mode runs Sockseek as a long-lived service with an HTTP/SignalR API,
remote CLI access, sharing, chat, notifications, and durable history. Configure a WebUI
password and HTTPS access, then run `sockseek daemon` to start
it; the default endpoint is `http://127.0.0.1:5030`.

Set `web-password-file` (or `web-password-hash`) for browser login. HTTPS is required;
`--allow-http-auth` explicitly permits plain HTTP for trusted local deployments.
For TLS termination, configure `trusted-proxies` with the proxy addresses.
`--no-auth` disables authentication entirely.

Create named API keys in Settings or with `sockseek auth keys create <name>`.
Remote clients use `--remote <url> --remote-api-key-file <path>`; add `--monitor`
to observe live workflows. Keys can be listed and revoked through Settings or
`sockseek auth keys`. Browser sessions expire after seven days by default and
are invalidated by logout or daemon restart.

For application integration, see [the API guide](docs/api.md).
<!-- sockseek-help:end -->

<!-- sockseek-help:start(config) -->

## Configuration

### Config Location

Sockseek will look for a file named sockseek.conf in the following locations:

- `~/.config/sockseek/sockseek.conf` (Any platform)
- `%APPDATA%\sockseek\sockseek.conf` (Windows only)
- `$XDG_CONFIG_HOME/sockseek/sockseek.conf`
- `{sockseek executable dir}/sockseek.conf`

Use `--config <path>` to choose a config file, `--config none` or `--no-config` to skip config loading.

The daemon automatically reloads config changes; share changes require a rescan, and some settings still require a restart.

### Syntax

Example config file:

```ini
username = your-username
password = your-password
pref-format = flac
fast-search = true
```

Lines starting with `#` will be treated as comments. Tildes in paths are expanded as the user
directory (even on Windows). Path settings also support `{bindir}` for the Sockseek binary directory
and `{configdir}` for the directory containing the active config file.

### Configuration profiles

Sockseek supports configuration profiles:

```ini
[lossless]
pref-format = flac,wav
```

To activate the above profile, use `--profile lossless`. This will apply all of its options. Your manual CLI options still take precedence.

To list all configured profiles, run `--profile help`.

### Auto-profiles

Profiles can be activated automatically based on a few simple conditions. For example:

```ini
# never automatically cancel album downloads in interactive mode
[no-stale]
profile-cond = interactive && download-mode == "album"
max-stale-time = 9999999

# download to another location for YouTube
[youtube]
profile-cond = input-type == "youtube"
output-dir = ~/downloads/sockseek-youtube

# use structural naming for non-music downloads
[generic-layout]
profile-cond = download-mode == "generic"
name-format = {peer-username}/{foldername}/{filename}
```

The following operators are supported for use in `profile-cond`: `&&`, `||`, `==`, `!=`, `!{bool}`.
The following variables are available:

```
input-type        "youtube"|"csv"|"string"|"bandcamp"|"spotify"|"list"|
                  "musicbrainz"|"soulseek-link"|"manual-search"|
                  "peer-shares"|"none" 
download-mode     "song"|"song-groups"|"album"|"album-groups"|"generic"
album             bool
group             bool
interactive       bool
progress-json     bool
no-progress       bool
```

<!-- sockseek-help:end -->

<!-- sockseek-help:start(file-conditions) -->

## File conditions

`pref-*` options change how results are **ranked**; they never filter anything out. `--pref-format flac` means Sockseek will prefer flac when available, but will still download mp3 if no flac is found. By contrast, conditions without the `pref` prefix are mandatory. For example, `--format flac` will reject non-flac files entirely.

_Note: Format conditions are UNORDERED! `--pref-format mp3,flac` is the same as `flac,mp3`._

The default required conditions accept common formats and enforce the source length (when both source and file length are known):

```ini
format = mp3,flac,ogg,m4a,opus,wav,aac,alac
length-tol = 3
```

The default preferred conditions are:

```ini
# Prefer files whose paths contain the supplied title and album.
# Changing these is not recommended.
pref-strict-title = true
pref-strict-album = true

# Prefer results that aren't locked. This is only relevant for
# interactive searches in the webui. Automated downloads always
# ignore locked items.
pref-exclude-locked = true
```

For album downloads, conditions like format, bitrate and sample rate operate based on coverage: A folder with 9 FLAC files and 1 MP3 is preferred over a mostly-MP3 folder, and the selected folder is still downloaded as a whole. Use `--strict-album-quality` to require every audio file in the folder to satisfy the conditions.

See [the options reference](#filtering--ranking-options) for all filtering and ranking options.

Conditions can also be supplied as a semicolon-delimited string with `--cond` and `--pref`, e.g.:

```bash
--cond "br>=320; format=mp3,ogg; sr<96000; album-track-count>=8"
```

For debugging, you can run a song search with `--print results-full` to reveal the sorting logic.

### Note on availability of metadata

Some info may be unavailable depending on the client used by the peer (like the bitrate). This is why if you set a `--min-bitrate`, Sockseek will still accept any file with **unknown** bitrate as a precaution.

To reject all files where one or more of the checked properties is null (unknown), use `--strict-conditions`.

This flag should be used with care: It's easy to accidentally exclude all files from users with
certain clients. For example, because the standard Soulseek client does not broadcast the bitrate,
enabling `--strict-conditions` and setting a `--min-bitrate` will effectively exclude all files shared by users with the standard client(!). Even if you don't add a required min-bitrate, this option will still result in all those shares getting ranked at the bottom due to the default pref- bitrate checks above.

<!-- sockseek-help:end -->

<!-- sockseek-help:start(name-format) -->

## Name format
Variables enclosed in {} will be replaced by the corresponding available value.
Name format supports subdirectories as well as conditional expressions like {tag1|tag2} - If
tag1 is null, use tag2. This can be chained arbitrarily many times. String literals enclosed
in parentheses are ignored in the null check. See [Variables](#variables) for the complete
reference, also available with `sockseek --help variables`.

Sockseek can format the downloaded filenames using `--name-format <yourformat>`. Variables enclosed in `{}` will be replaced by the corresponding file tag value.
Name format supports subdirectories as well as conditional expressions like `{tag1|tag2}` - If
tag1 is null, use tag2 (can be chained). Examples:

- `{artist} - {title}`
    Always name it 'Artist - Title'. Because some files on Soulseek are untagged, the
    following is generally preferred:
- `{artist( - )title|filename}`
    Everything inside round parentheses `()` inside braces `{}` is treated as a raw string and ignored in the null check. Here, if artist and title are both not null, name it 'Artist - Title', otherwise use the original filename.
- `{albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}`
    Sort files into artist/album folders if all tags are present, otherwise put them in
    the 'missing-tags' folder.
<!-- sockseek-help:end -->

<!-- sockseek-help:start(variables) -->
## Variables

Variables are shared by name formats and on-complete commands.

### Shared placement variables

These variables are available for all download jobs.

```
peer-username / username        Username of the peer serving the file
filename / slsk-filename        Remote filename without its extension
ext                             Final output file extension, including the dot
relative-path                   Relative directory plus filename, without the extension
foldername / slsk-foldername    Selected root plus relative directory; for a direct
                                file, its immediate remote parent folder
item-name                       Current song, album, file, or folder name
default-folder                  Enclosing list/playlist folders for the current job
                                (e.g. Spotify playlist name)
output-dir / outputdir          Output parent directory (--output)
type                            Job type (Song, Album, RemoteFile, RemoteDirectory, ..)
extractor                       Name of the input extractor (Spotify, CSV, ..)
input                           Original input string
bindir                          Base application directory
configdir                       Active config file directory
```

### Music variables

These are read from the downloaded file's tags:
```
artist                         First artist
artists                        Artists, joined with '&'
albumartist                    First album artist
albumartists                   Album artists, joined with '&'
title                          Track title
album                          Album name
year                           Track year
track                          Track number
disc                           Disc number
length                         Track length (in seconds)
```

These are taken from the input source (CSV file data, Spotify, etc):
```
sartist / sartists             Source artist
stitle                         Source track title
salbum                         Source album name
slength                        Source track length
uri / url                      Track URI
snum                           Source item number (1-indexed, including offset)
row / line                     Line number (1-indexed, only for CSV or list input)
artist-maybe-wrong             If the source artist might be incorrect (true/false)
```

<!-- sockseek-help:end -->

<!-- sockseek-help:start(on-complete) -->

## On-Complete Actions

The `--on-complete` option allows executing commands after a track or album is downloaded. Multiple actions can be chained using the `+ ` prefix (note the space after +).

**Syntax:** `--on-complete [options] -- command`

Hint: You can use `--mock-files-dir` to test your commands (see [Testing Options](#testing-options)).

Every on-complete command must include the `--` delimiter. Sockseek options go before it; everything after it is the command passed to the OS.

When passing an on-complete action on the command line, quote the whole value so the delimiter is part of the `--on-complete` argument:

```bash
--on-complete "when=success scope=album -- notify-send \"Downloaded\" \"{path}\""
```

### Options

- `when=` omitted - Same as `when=completed`
- `when=completed` - Execute on success or fail, if the item wasn't skipped
- `when=success` - Execute only for successful downloads
- `when=failure` - Execute for failed or partially successful downloads
- `when=skipped` - Execute for skipped jobs
- `when=already-exists` - Execute only for already-existing skipped jobs
- `when=not-found-last-time` - Execute only for not-found-last-time skipped jobs
- `when=cancelled` - Execute only for cancelled jobs
- `when=any` - Execute for every terminal outcome
- `scope=track` - Execute only for track-level completions
- `scope=album` - Execute only for album-level completions
- `hidden` - Hide the command window
- `shell` - Use shell execute
- `lock` - Serialize this action across jobs
- `update-index` - Read stdout as `success;new_path`, `failed`, or `ignored;new_path` to update the track/album entry in the index and playlist. `failed` clears the stored path; `ignored;new_path` leaves the state unchanged and updates only the path.

If `when=` is omitted, it behaves like `when=completed`. This preserves the usual "run when work completed" behavior while avoiding commands for already-existing or not-found-last-time skips.

Chained actions evaluate `when=` and outcome/path variables against the result of the preceding action. For example, if an `update-index` action changes a failure to success, the next `when=success` action runs and sees the updated path.

### Command-output variables

See the shared [Variables](#variables) reference, also available with
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

For album-only (`scope=album`) actions, tag-based variables such as `{title}`, `{artist}`, and `{album}` are read from the first audio file in the album.

### Examples

Send a Linux desktop notification for album downloads:

```ini
on-complete = when=success scope=album -- notify-send "Downloaded: {album}" "{path}"
```

Search album art with [Cover Fetcher](https://github.com/fiso64/cover-fetcher):

```ini
on-complete = when=success scope=album hidden -- cmd /c start "" "path\to\CoverFetcher.exe" --from-dir "{path}"
```

Queue downloaded audio files in foobar2000:

```ini
on-complete = when=success hidden -- cmd /c if {is-audio}==true start "" "path\to\foobar2000.exe" /immediate /add "{path}"
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

<!-- sockseek-help:start(shortcuts) -->

## Shortcuts & interactive mode

### CLI Shortcuts

You can use these at any time while things are searching or downloading:

```
c               cancel specific job by id or all jobs
t               try next candidate for a job id
i               get detailed info about job by id
```

### CLI Interactive Prompt Shortcuts

Interactive mode for albums can be enabled with `-t`/`--interactive`. It allows you to pick the desired folder or download only specific files from it, rather than automatically downloading the best match.

Key bindings:

```
Up/p            previous folder
Down/n          next folder
Enter/d         download selected folder
y               download folder and disable interactive mode
r               retrieve all files in the folder
s/q/Esc         skip current album
Q/S             skip current and all remaining new album prompts
h               print this help text

d:1,2,3         download specific files
d:start-end     download a range of files
f               filter folders containing files matching query
cd ..           load parent folder
cd subdir       go to subfolder
```

<!-- sockseek-help:end -->

<a id="other-examples"></a>
## More Examples

### Given a playlist of songs, download the albums

When album metadata is available and you have a playlist of songs, you can use the `--upgrade-to-album` option to download albums instead of songs:

```bash
sockseek "https://open.spotify.com/playlist/id" --upgrade-to-album
```

Album downloads are of course deduplicated; you won't get many copies of an album just because you have 20 tracks from the same album in the playlist.

### Use yt-dlp as fallback

Sockseek can fall back to downloading song playlists from YouTube using yt-dlp (must be installed separately) when no items are found on Soulseek. It runs a YouTube search first, unless your input is a YouTube playlist, in which case it just downloads directly by ID. Use `--yt-dlp` (and `--yt-dlp-argument` if needed):

```bash
sockseek "your-song-playlist" --yt-dlp
```

You can also use any custom downloader as fallback with [on-complete actions](#on-complete-actions).

### Retrieving deleted YouTube item names

YouTube inputs support the experimental `--get-deleted` option, which attempts to browse the [web archive](https://web.archive.org/) to find deleted video names in a playlist:

```bash
bash "https://youtube.com/playlist/id" --get-deleted
```

### Show artist songs which you don't have

[Song Groups](#song-groups) mode can be used in combination with `--skip-music-dir` to show all songs by an artist on Soulseek which are not in your music library:

```bash
sockseek "artist=MC MENTAL" -s -g --skip-music-dir "path/to/music" --print results
```

Remove the `--print results` part to download those songs.

### Create an automatic wishlist downloader

You can easily build a wishlist downloader. Create a file named `wishlist.txt`, and add some items with [list file](#list-file) syntax:

```
"Artist - Some Album"
"Artist - Album With Conditions"        strict-album=true;album-track-count>=5
"Another Album"                         banned-users=user-sharing-bad-files
"Album With Pref. Conditions"           format=mp3,flac                         format=flac

s:"MC MENTAL - MC MENTAL @ HIS BEST, length=242"
```

Add a profile to your `sockseek.conf`:

```ini
[wishlist]
# Will look for wishlist.txt in the same folder as your sockseek.conf
input = {configdir}/wishlist.txt
input-type = list
log-file = {configdir}/wishlist.log

# The index will keep track of successfully downloaded items to skip
# them in the next runs:
index-path = {configdir}/wishlist-index.csv

# Uncomment this line to remove successfully downloaded items from
# wishlist.txt directly:

# remove-from-source = true

# Add some global conditions for your wishlist, if you want:
format = flac,mp3
min-bitrate = 200

# To keep searching for a flac version of "Album With Pref. Conditions"
# in the above list even after an mp3 version has been downloaded, make
# it check the local version:

# skip-check-pref-cond = true

# Note that this requires the downloaded files to remain in the same
# spot or for the index to be updated after any moves, e.g. via
# on-complete update-index.
```

Now set up a cron job (on Linux) scheduled task (on Windows) to periodically run Sockseek with the following option:

```bash
sockseek --profile wishlist
```

You can also just manually run it, for example in interactive mode (`-t`).

<!-- sockseek-help:start(notes-and-tips) -->

## Tips

### Rate limits

The Soulseek server bans users for 30 minutes if too many searches are performed within a short timespan. Sockseek has a search limiter which can be adjusted with `--searches-per-time` and `--searches-renew-time` (when the limit is reached, the status of the downloads will be 'Waiting'). By default it is configured to allow up to 34 searches every 220 seconds.

### Why are some of my downloads wrong or missing?

The default settings favor recall over precision: when the correct file is available in results, it will almost always be ranked first. The tradeoff is that if it's absent and something else loosely passes the filters, that something else gets downloaded. The options below let you control where you fall on that spectrum.

**It downloaded a wrong song or album.**
To tighten matching for song downloads, add one or more strict filters:

```bash
sockseek "https://spotify.com/playlist/blah" --strict-title --strict-artist
```

These require that the file path contains the song title and artist name (case-insensitive).

For album downloads, the cleanest filter is usually the expected track count:

```bash
sockseek "Artist - Album" --album-track-count 10
```

Use inequalities like `10+` or `12-` when expanded or incomplete editions are acceptable. You can also add `--strict-album` to require the album name in the folder path.

**It didn't find a song or album.**
This usually means one of two things:

1. Your searches are non-standard, like an uncommon artist spelling. You can try cleaning them up with options like `--regex` and `--remove-ft`. See also the next section.
2. Whatever you're looking for is just not available on Soulseek.

For **songs**, there is also the possibility of a length mismatch: When using Spotify or YouTube as input, the reported length can differ from the actual file on Soulseek (like from a CD rip) by more than the default 3-second tolerance. Try `--length-tol 10`, or `--length-tol -1` to disable length filtering entirely.

Use `--print results-full` to inspect what Soulseek returned without downloading anything.

### Searching

- It's always best to provide the least input necessary to uniquely identify an album or song.
  - Sometimes including the artist can be undesirable (e.g. "Various Artists"). For spotify or bandcamp inputs, you can remove the artist name with `--regex A:.*`.
  - Use `--remove-ft` to remove "feat." or "ft." artists
- You can download an entire album based on the name of one of its songs by searching for that name in album mode: `"artist=ARTIST, title=SONG TITLE" --album`.
- When searching for a single song with a string input, you can provide the album name in addition. The album name will not be included in the query, but search results containing it will be preferred (due to pref-strict-album).
- When dealing with YouTube playlists you may want to remove any text in parentheses (like (Video)), as well as "Official" and "Lyrics" with `--regex "[\[\(].*?[\]\)]|(?i:lyrics)|(?i:official)"`

### Speeding things up

The following options will make it go faster, but may decrease search result quality or cause instability:

- `--fast-search` skips waiting until the search completes and downloads as soon as a file matching the preferred conditions is found (songs only)
- `--search-timeout` decrease to make searches end faster at the possible cost of fewer results
- `--concurrent-jobs` controls how many leaf jobs can run at once (default: 20)
- `--concurrent-searches` controls how many Soulseek searches can run at once (default: 2)
- `--concurrent-extractors` controls how many inputs can be extracted at once (default: 4)
- `--max-stale-time` is set to 30 seconds by default, Sockseek will wait a long time before giving up on a file once it's chosen.

### Testing Options

You can test almost any aspect of the search and downloading logic with `--mock-files-dir` and pointing it to a local directory containing audio files. This directory will then be used instead of searching and downloading from Soulseek. Example:

```bash
sockseek "Artist - Album" -t --mock-files-dir /path/to/dir
```

If you plan to use a large music library, you might want to add `--mock-files-no-read-tags` to improve the initial loading performance. But note that reading tags is required when filtering by metadata such as length or bitrate.

<!-- sockseek-help:end -->

## Options reference

Most used flags at a glance:

```text
-s, --song                      Treat string input as song search
-t, --interactive               Pick from album results before downloading
-g, --group                     Download distinct songs/albums from grouped results
-o, --output-dir <path>         Download directory
--pref-format <formats>         Preferred formats for ranking, e.g. flac,wav. Unordered.
--format <formats>              Required accepted formats. Unordered.
--album-track-count <count>     Required number of audio files when downloading albums
--skip-music-dir <path>         Skip tracks already in a music library
--profile <names>               Apply configuration profile(s)
--name-format <format>          Organize files using a path template
--strict-title/artist/album     Require title in filename, artist in path, album in folder path
--upgrade-to-album              Upgrade song-shaped source results to album jobs
```

<!-- sockseek-help:start(main) -->

#### Required Arguments

```
<input>                         A URL, search string, Soulseek link, or path to a local
                                CSV/list file. Run `--help input` to view the accepted inputs.
                                Can also be passed with -i, --input <input>
--user <username>               Soulseek username
--pass <password>               Soulseek password
```

#### General Options

```
-o, --output-dir <path>         Download directory
--input-type <type>             [csv|youtube|spotify|bandcamp|string|list|soulseek-link|
                                musicbrainz] (default: auto)
-s, --song                      Song mode for string input
--name-format <format>          Name format for downloaded tracks. See `--help name-format`
--invalid-replace-str <str>     Replacement string for invalid path characters (default: space)

-n, --number <maxtracks>        Download the first n tracks of a playlist
--offset <offset>               Skip a specified number of tracks
-r, --reverse                   Download tracks in reverse order
-c, --config <path>             Set config file location. Set to 'none' to ignore config
--no-config                     Ignore any config file
--profile <names>               Configuration profile(s) to use. See `--help config`.
--concurrent-jobs <num>         Max concurrent leaf jobs (default: 20)
--concurrent-searches <num>     Max concurrent Soulseek searches (default: 2)
--concurrent-extractors <num>   Max concurrent input extractors (default: 4)
--write-playlist                Create an m3u playlist file in the output directory
--playlist-path <path>          Override default path for m3u playlist file
--write-index                   Create/update the Sockseek index (default when using
                                compatible inputs)
--no-write-index                Do not create/update the Sockseek index
--index-path <path>             Override default path for Sockseek index
--no-incomplete-ext             Save files with their final name instead of a temporary
                                `.incomplete` extension.

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

--listen-port <port>            Port for incoming connections (default: 49998)
--no-listen                     Disable the incoming connection listener
--connect-timeout <ms>          Timeout used when logging in to Soulseek (default: 20000ms)
--user-description <desc>       Soulseek profile description
--user-picture <path>           Daemon profile picture

--on-complete <command>         Run a command when a download completes. See `--help
                                on-complete`
```
#### Search Options

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
--unknown-error-retries <num>   Extra retries for unknown/transient errors (default: 2)
--fails-to-downrank <num>       Failures within a workflow before downranking a peer (default: 2)
--fails-to-ignore <num>         Failures within a workflow before ignoring a peer (default: 3)

--yt-dlp                        Use yt-dlp to download songs that weren't found on
                                Soulseek. yt-dlp must be available from the command line.
                                Works on any input containing songs; albums not supported.
                                Runs a YouTube search and downloads the first result. For
                                YouTube inputs, downloads directly by ID instead.
--yt-dlp-argument <str>         The command line arguments when running yt-dlp. Default:
                                "{id}" -f bestaudio/best -ci -o "{savepath-noext}.%(ext)s" -x
                                Available vars are: {id}, {savedir}, {savepath},
                                {savepath-noext}.
                                Note that -x causes yt-dlp to download webms in case ffmpeg
                                is unavailable.

--search-timeout <ms>           Max search time in ms (default: 5000)
--max-stale-time <ms>           Max download time without progress in ms (default: 30000)
--searches-per-time <num>       Max searches per time interval. Higher values may cause
                                30-minute bans, see `--help notes`. (default: 34)
--searches-renew-time <sec>     Controls how often available searches are replenished.
                                See `--help notes`. (default: 220)
```

#### Spotify Options

```
--spotify-id <id>               Spotify client ID
--spotify-secret <secret>       Spotify client secret
--spotify-token <token>         Spotify access token
--spotify-refresh <token>       Spotify refresh token
--remove-from-source            Remove downloaded tracks from source playlist
```

#### YouTube Options

```
--youtube-key <key>             YouTube Data API key
--get-deleted                   Attempt to retrieve titles of deleted videos from wayback
                                machine. Requires yt-dlp.
--deleted-only                  Only retrieve & download deleted music.
```

#### Bandcamp Options

```
--from-html <path>              Read Bandcamp page HTML from a local file
```

#### SoundCloud Options

```
--soundcloud-token <token>      SoundCloud OAuth token
```

#### CSV File Options

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

#### Filtering & Ranking Options

```
--format <formats>              Required file format(s). Comma-separated, unordered. See
                                also --pref-format for soft preferences.
--length-tol <sec>              Length tolerance in seconds, -1 to disable (default: 3)
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
--banned-users <list>           Comma-separated list of users to ignore
--allowed-users <list>          Comma-separated list of users to allow
--cond <conditions>             Semicolon-delimited required conditions

--pref-format <formats>         Preferred format(s) for ranking. Use --format to require
                                formats strictly. Comma-separated, unordered.
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
--pref-banned-users <list>      Comma-separated list of users to downrank
--pref-allowed-users <list>     Comma-separated list of users to prefer
--pref <conditions>             Semicolon-delimited preferred conditions

--strict-conditions             Skip files with missing properties instead of accepting by
                                default; if --min-bitrate is set, ignores any files with
                                unknown bitrate. Warning: Available props depend on client
```

#### Album Download Options

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
--min-album-track-count <num>   Minimum number of tracks in an album folder
--max-album-track-count <num>   Maximum number of tracks in an album folder
--extract-max-track-count       Set maximum album track count from extracted sources
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
```

#### Group Download Options

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
#### Daemon / Remote Options
```
sockseek daemon                 Start the HTTP/SignalR daemon instead of running a download
--server-ip <ip>                IP/interface for the daemon HTTP API (default: 127.0.0.1)
--server-port <port>            Port for the daemon HTTP API (default: 5030)
--web-username <name>          WebUI login name (default: admin)
--web-password-file <path>      WebUI password file; required unless a hash or --no-auth is set
--web-password-hash <hash>      Alternative to the password file; use auth hash-password
--web-session-lifetime <time>   Fixed session duration (default: 7.00:00:00)
--web-theme <theme>            WebUI theme: dark (default), light, auto
--web-default-search-mode <mode>  generic, track, album (default), song-groups, album-groups
--trusted-proxies <ips>         Forwarding proxy addresses (default: 127.0.0.1,::1)
--allow-http-auth              Permit plaintext HTTP credentials/cookies (default: false)
--no-auth                      Grant anonymous full access (default: false)
--remote <url>                  Use an existing daemon instead of running locally
--remote-api-key-file <path>    Remote client key file; separate from the WebUI password
--remote-api-key <key>          Alternative inline API key (prefer a private file)
sockseek auth hash-password    Generate a WebUI password hash locally
sockseek auth keys list        List API keys; add --remote for a running daemon
sockseek auth keys create <name>  Create a named key and print its secret once
sockseek auth keys revoke <id>  Revoke a key; offline commands require the daemon stopped
--monitor                       Monitor all active daemon workflows. Input is optional;
                                an input submitted with this option appears alongside
                                all other daemon work until the monitor is interrupted
--share <[alias]path|path>      Replace configured public share roots. Prefix a later value
                                with '+ ' to append instead.
--share-exclude <path>          Replace excluded share subtrees. Prefix with '+ ' to append.
--share-filter <regex>          Replace case-insensitive share filters. Prefix with '+ ' to
                                append. Matches on the remote relative path.
--share-scan-on-start <bool>    Scan configured shares when the daemon starts (default: true)
--share-rescan-interval <time>  Periodic rescan interval (for example 30m, 12h, or off;
                                default: off)
--upload-slots <num>            Maximum concurrent uploads (default: 10)
--upload-speed-limit-kib <n>    Aggregate upload limit in KiB/s (default: unlimited)
--upload-blocked-user <name>    Replace usernames denied access to your shares. Prefix with '+ ' to append.
--upload-blocked-ip <address>   Replace IPs denied access to your shares. Prefix with '+ ' to append.
--private-message-blocked-user <name>
                                Replace usernames whose incoming DMs are discarded. Prefix with '+ ' to append.
--chat-room <name>              Replace rooms joined by the daemon. Prefix with '+ ' to append.
--data-dir <path>               Directory for daemon data, including sockseek.db
--no-config-watch               Disable automatic daemon config-file reload
--no-retention                  Disable scheduled history retention
--successful-job-retention-days <days|forever>
                                Successful job retention (default: forever)
--unsuccessful-job-retention-days <days|forever>
                                Failed, cancelled, and interrupted job retention (default: forever)
--transfer-retention-days <days|forever>
                                Transfer history, attempts and byte accounting retention (default: forever)
--search-result-retention-days <days|forever>
                                Raw search-result retention (default: 30)
--private-message-retention-days <days|forever>
                                Private-message retention (default: forever)
--room-message-retention-days <days|forever>
                                Room-message retention (default: 30)
--exclude-locked                Exclude locked files from search results (default: false)
--pref-exclude-locked           Prefer unlocked results (default: true)
```
<!-- sockseek-help-topic:start(user) -->
#### Remote User Commands
```text
sockseek user profile <username>  Show a peer's profile and sharing statistics
  --refresh                       Fetch new profile data
  --picture <mode>                Picture mode: auto|sixel|pixels|none; auto probes
                                  terminal support (default: auto)

sockseek user shares <username>   Browse/select files; remote transfer/output/profile options apply
  --refresh                       Fetch new shares instead of using a recent browse

sockseek user shares-page <browse-id>
                                  List one page of browse results
  --parent <directory-id>         List child directories (omit for roots)
  --files <directory-id>          List files instead; mutually exclusive with --parent
  --query <text>                  Filter the requested page
  --cursor <cursor>               Continue from an earlier page
  --limit <1..500>                Maximum results (default: 100)

sockseek user shares-download <browse-id>
                                  Download selections; remote transfer/output/profile options apply
  --folder <directory-id>         Select a complete public subtree; repeatable
  --file <file-id>                Select one file; repeatable
  --request-id <uuid>             Retry key for idempotent submission

sockseek user shares-cancel <browse-id>
                                  Cancel an active browse for all waiting clients

--remote <url>                    Daemon URL; required unless configured
--json                            Emit JSON instead of pictures or interactive browsing
```
<!-- sockseek-help-topic:end -->
<!-- TODO [V4]: missing CLI documentation for sockseek chat, sockseek room, and others -->
<!-- sockseek-help-topic:start(database) -->
#### Sockseek Database
```
sockseek database migrate      Update the offline database to the current schema
sockseek database integrity    Check an offline database for corruption
sockseek database backup       Create and verify an offline backup
sockseek database restore      Verify and restore an offline backup
--data-dir <path>               Override the configured/default data directory
--backup <path>                 Backup destination for `backup`, source for `restore`
```
<!-- sockseek-help-topic:end -->
#### Printing & Debug Options

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

### CLI Notes

- Flags can be explicitly disabled by setting them to false, e.g. `--interactive false`.
- Single-character flags can be combined, e.g. `-at` for `-a -t`.
- Acronyms of two- and `--three-word-flags` like `--twf` are also accepted. E.g. `--Mbr` for `--max-bitrate`.

<!-- sockseek-help:end -->

## Docker

Docker documentation has moved to [docs/docker.md](docs/docker.md).
