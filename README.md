# Sockseek

Sockseek is a command-line downloader for Soulseek. Point it at a search string, Spotify playlist, YouTube playlist, CSV file, Bandcamp page, MusicBrainz release, or Soulseek link; it searches the network, ranks candidate files using your preferences, and downloads the best match (automatically or interactively). It is scriptable, configurable, and can run either as a one-shot CLI tool or as a persistent daemon.

This project was formerly named `sldl` (and `slsk-batchdl` before that). See [here](https://github.com/fiso64/sockseek/releases/) for why it was renamed to something dumb.

## Quick Start

1. Download a release for your OS from the [releases page](https://github.com/fiso64/sockseek/releases).

2. Create a config file named `sockseek.conf` in one of these locations:
    - Linux/macOS/Windows: `~/.config/sockseek/sockseek.conf`
    - Windows: `%APPDATA%\sockseek\sockseek.conf`

    Minimal config:
    ```ini
    username = your-soulseek-username
    password = your-soulseek-password
    # Sockseek prefers mp3 by default. To prefer lossless (will still
    # fall back to mp3 if unavailable):
    # pref-format = flac,wav
    ```

    If you're running a persistent Soulseek client, use Sockseek with a **separate Soulseek account** to avoid connection problems.


3. Download your first song:
    ```bash
    sockseek "Artist - Song Title" -s
    ```

4. Or download an album interactively (`-t`):
    ```bash
    sockseek "Artist - Album Title" -t
    ```

If a download is wrong or missing, see [When downloads are wrong or missing](#when-downloads-are-wrong-or-missing).

> [!NOTE]
> Sockseek does not share your music folders yet. To keep the Soulseek network healthy, please also share your collection with a regular client like [Nicotine+](https://github.com/nicotine-plus/nicotine-plus) or [slskd](https://github.com/slskd/slskd).
>
> [Daemon mode](#daemon--remote-mode) is the path toward longer-running client features, but sharing is not implemented yet.

## Common workflows

#### Download a song
```bash
sockseek "Song Title" --song
sockseek "Artist - Song Title" --song
```
The hyphen ` - ` determines what part of the input is the artist and title, which can be important for ranking and filtering. See [Search string](#search-string).

#### Download an album automatically
```bash
sockseek "Album Title"
sockseek "Artist - Album Title"
```
Again, prefer to separate artist from album title with ` - ` when providing both.

#### Download an album interactively
```bash
sockseek "Artist - Album Title" -t
```

#### Download a playlist
```bash
sockseek "https://www.youtube.com/playlist?list=blah"
```

Check the tracks before downloading a 5000-item long megalist:
```bash
sockseek "input" -n 10 --print-tracks-full
```

#### Download all albums by an artist interactively
```bash
sockseek "artist=Artist Name" -agt
```

Groups the albums and sorts by popularity; may also include compilations.

#### Prefer FLAC or WAV, but still accept other formats
```bash
sockseek "Artist - Album Title" --pref-format flac,wav
```

#### Skip tracks already in your music library
```bash
sockseek "playlist.csv" --skip-music-dir "path/to/music"
```

For more examples, see [Examples](#examples).


## When downloads are wrong or missing

Sockseek searches the Soulseek peer-to-peer network -- Spotify, YouTube, and similar inputs are used only as metadata sources to drive the search, not as audio sources. Most of the time, if the file you want exists on Soulseek, Sockseek will find and download it correctly.

The default settings favor recall over precision: when the correct file is available in results, it will almost always be ranked first. The tradeoff is that if it's absent and something else loosely passes the filters, that something else gets downloaded. The options below let you control where you fall on that spectrum.

**A wrong song or album gets downloaded.**
To tighten song matching, add one or more strict filters:

```bash
sockseek "https://open.spotify.com/playlist/blah" --strict-title --strict-artist
```

These require that the file path contains the song title and artist name (case-insensitive).

For album downloads, the cleanest guard is usually the expected track count:

```bash
sockseek "Artist - Album" --album-track-count 10
```

Use inequalities like `10+` or `12-` when expanded or incomplete editions are acceptable. `--strict-album` requires the album name in the folder path, but track count tends to be cleaner.

**A song or album isn't found at all.**  
Two common causes:

- **Length mismatch.** When using Spotify or YouTube as input, the reported length can differ from the actual file on Soulseek (like from a CD rip) by more than the default 3-second tolerance. Try `--length-tol 10`, or `--length-tol -1` to disable length filtering entirely.
- **Naming differences.** The Soulseek network returned no results for the query. Options like `--remove-ft` or `--regex` can help clean it up.

Use `--print results-full` to inspect what Soulseek returned without downloading anything.

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
   - [Aggregate](#aggregate)
   - [Album Aggregate](#album-aggregate)
 - [Daemon / remote mode](#daemon--remote-mode)
 - [Configuration](#configuration)
 - [File conditions](#file-conditions)
 - [Name format](#name-format)
 - [On-Complete Actions](#on-complete-actions)
 - [Shortcuts \& interactive mode](#shortcuts--interactive-mode)
 - [Examples](#examples)
 - [Notes](#notes)
 - [Tips](#tips)
 - [Options reference](#options-reference)
 - [Docker](docs/docker.md)


<!-- sockseek-help:start(input) -->
## Input types
The input type is usually determined automatically. You can also manually set it with `--input-type`.  
The following input types are accepted:

###  CSV file
Path to a local CSV file. Use a CSV file containing track information to download a list of
songs or albums. Only the title or album column is required, but extra info may improve search
result ranking. If the columns have common names ('Artist', 'Title', 'Album', 'Length', etc)
then it's not required to manually specify them, otherwise you must provide at least `--title-col` or `--album-col`.   
CSV rows determine their own shape: rows with a track title are song downloads, and rows
without a title are album downloads.

###  YouTube
A YouTube playlist URL. Download songs from a YouTube playlist.  
**Note:** The default method to retrieve playlists might not reliably return all videos. To get all
videos, you can use the official API by providing a key with `--youtube-key`. A key can
be obtained at https://console.cloud.google.com. Create a new project, click 'Enable API' and
search for 'YouTube Data', then follow the prompts.

### Spotify
Any playlist or album URL, or `spotify-likes` for your liked songs, or `spotify-albums` for liked albums.  
Spotify API access now requires your own Spotify developer application for all Spotify inputs,
including public playlists. Spotify also requires the owner of that application to have an
active Spotify Premium subscription. If you do not have Premium, export the Spotify playlist
with a Spotify-to-CSV converter and pass the CSV file to Sockseek instead.

#### Using Credentials

<details>
  <summary>Click to expand</summary>

Create a Spotify application at https://developer.spotify.com/dashboard/applications with a redirect URL http://127.0.0.1:48721/callback. The Spotify account that owns the application must have an active Premium subscription. Obtain an application ID and secret from the created application dashboard.

For public playlists and albums, pass the application credentials:

```bash
sockseek "https://open.spotify.com/playlist/id" --spotify-id 123456 --spotify-secret 123456
```

For private playlists, liked songs, liked albums, or `--remove-from-source`, start Sockseek with the obtained credentials and an authorized action to trigger the Spotify app login flow:

```bash
sockseek spotify-likes --spotify-id 123456 --spotify-secret 123456 -n 1 --print-tracks
```
Sockseek will try to open a browser automatically but will fall back to logging the login flow URL to output. After login flow is complete Sockseek will output a token and refresh token and finish running the current command.

To skip requiring login flow every time Sockseek is used the token and refresh token can be provided to Sockseek (hint: store this info in the config file to make commands less verbose):

```bash
sockseek spotify-likes --spotify-id 123456 --spotify-secret 123456 --spotify-refresh 123456 --spotify-token 123456 -n 1 --pt
```

spotify-token access is only valid for 1 hour. spotify-refresh will enable Sockseek to renew access every time it is run (and can be used without including spotify-token)
</details>

### Bandcamp
A Bandcamp track, album, or artist URL. Download a single track, an album, or an artist's
entire discography. Also accepts wishlist URLs. Extraction might fail due to Cloudflare; download the HTML to a local file and point Sockseek to it using `--from-html` in case of issues.

### MusicBrainz
A MusicBrainz.org URL for a release, release group, or collection.
- A `/release/...` URL is treated as a single album download with a strict track count.
- A `/release-group/...` URL is also treated as a single album download. It tries to pick the most common version of the album. Sets the minimum album track count to the chosen release track count, and no maximum track count unless `--extract-max-track-count` is set.
- A `/collection/...` URL is treated as a list of albums, downloading each release contained within the collection.

### Soulseek Link
A direct path starting with `slsk://`. Paths ending in `/` are album/folder downloads;
file paths are direct single-file downloads unless `--album` is explicitly requested.

### Search string
Name of the track, album, or artist to search for. The input can either be an arbitrary
search string (like what you would type in the Soulseek search bar), or a comma-separated
list of properties of the form `title=Song Name, artist=Artist Name, length=215`.

The following properties are accepted: title, artist, album, length (in seconds), 
artist-maybe-wrong, album-track-count.

String input accepts a shorthand for track and album downloads: The input `ARTIST - TITLE`
is parsed as `artist=ARTIST, album=TITLE` by default, and as
`artist=ARTIST, title=TITLE` when run with `--song`.
Keyed string input is more explicit: `artist=ARTIST, title=TITLE` is treated as a song
download by default. Use `--album` if you want `title=` to act as an album search hint,
i.e. you want to search for an album by the name of one of its tracks.

### List file
List input must be manually activated with `--input-type=list`. The input must be a path to a text
file containing lines of the following form:
```text
# Any input type                conditions (optional)           pref. conditions (optional)
"Artist - Album"                "format=mp3; br>128"            "br >= 320"

# String album input:
"Artist - Album"                strict-album=true;album-track-count=13

# String song input:
s:"Artist - Song"               strict-title=true

# Album search using a song-title hint:
a:"artist=Artist, title=Song"

# Any other input type is also accepted:
path/to/tracks.csv
https://www.youtube.com/playlist?list=blah
```
The conditions are added on top of the configured conditions and can be omitted.
For string lines, unprefixed entries use the configured download mode: album by default, or song
mode when `--song` / `song = true` is set.
<!-- sockseek-help:end -->

<!-- sockseek-help:start(download-modes) -->
## Download modes
Structured sources such as CSV rows, Spotify, YouTube, Bandcamp, MusicBrainz, and Soulseek links
usually decide for themselves whether they contain songs or albums. String inputs are treated as
albums by default.

Use `--upgrade-to-album` when a structured source gives you song entries but you want album jobs instead,
such as downloading the albums represented by a Spotify song playlist or a CSV of tracks.

### Song
Downloads a single file for string input and string lines inside list files. Song mode is the default
for playlists from streaming platforms and CSV song lists. Use `-s/--song` for string/list input that
should be treated as a song search. To restore the pre-3.0 default behavior globally, add 
`song = true` to your config file.

### Album
Sockseek will search for the album and download an entire folder including non-audio
files. Album mode is the default for string input and string lines inside list files. It is
also used by album-shaped sources such as Spotify/Bandcamp album links and CSV rows without
a track title. Use `-t` to pick
interactively. See [Shortcuts & interactive mode](#shortcuts--interactive-mode).

### Song Aggregate
With `--song -g/--aggregate`, Sockseek performs an ordinary search for the input, then attempts to
group the results. Note that `--min-shares-aggregate` is 2 by default, meaning that
items shared by only one user will be ignored. Aggregate song mode can be used to download
all songs by an artist. See [Print all songs by an artist which are not in your library](#print-all-songs-by-an-artist-which-are-not-in-your-library).

### Album Aggregate
Activated when `--aggregate` is enabled for album-shaped input. Sockseek will group shares and
download one of each distinct album, starting with the one shared by the most users. Note
that `--min-shares-aggregate` is 2 by default, meaning that albums shared by only one user
will be ignored. Album-aggregate mode can be used to download the most popular (or all) albums
by an artist. It is recommended to pair it with `--interactive`. See [Example](#download-all-albums-by-an-artist-interactively) for more details.
<!-- sockseek-help:end -->

<!-- sockseek-help:start(daemon) -->
## Daemon / remote mode
Daemon mode is the first step toward running Sockseek as a persistent Soulseek client rather than a one-shot downloader. 
Right now it exposes the download engine for remote CLI use; future releases may expand it with long-running client features such as sharing.

Run `sockseek daemon` to start the HTTP/SignalR daemon. It uses the same config/profile system as the
CLI and listens on `127.0.0.1:5030` by default.

Once the daemon is running, use `--remote <url>` to run the CLI as a thin client against it:

```bash
sockseek daemon --server-ip 0.0.0.0 --server-port 5030
sockseek "Artist - Title" --remote http://127.0.0.1:5030
```

For HTTP API, SignalR, and client integration notes, see [docs/api.md](docs/api.md).
<!-- sockseek-help:end -->

<!-- sockseek-help:start(config) -->
## Configuration
### Config Location
Sockseek will look for a file named sockseek.conf in the following locations:

- `~/.config/sockseek/sockseek.conf`
- `%APPDATA%\sockseek\sockseek.conf` (Windows)
- `$XDG_CONFIG_HOME/sockseek/sockseek.conf`
- `{sockseek executable dir}/sockseek.conf`

Use `--config <path>` to choose a config file, `--config none` or `--no-config` to skip config loading.

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
Profiles are supported:
```ini
[lossless]
pref-format = flac,wav
```
To activate the above profile, run `--profile lossless`. To list all available profiles,
run `--profile help`.  
Profiles can be activated automatically based on a few simple conditions:
```ini
# never automatically cancel album downloads in interactive mode
[no-stale]
profile-cond = interactive && download-mode == "album"
max-stale-time = 9999999

# download to another location for YouTube
[youtube]
profile-cond = input-type == "youtube"
path = ~/downloads/sockseek-youtube
```
The following operators are supported for use in profile-cond: &&, ||, ==, !=, !{bool}.  
The following variables are available:
```
input-type        ("youtube"|"csv"|"string"|"bandcamp"|"spotify"|"list"|"soulseek"|"musicbrainz"|"none")
download-mode     ("normal"|"song"|"aggregate"|"album"|"album-aggregate")
album             (bool)
aggregate         (bool)
interactive       (bool)
progress-json     (bool)
no-progress       (bool)
```
<!-- sockseek-help:end -->

<!-- sockseek-help:start(file-conditions) -->
## File conditions
`pref-*` options change how results are **ranked**; they never filter anything out. `--pref-format flac`
means Sockseek will prefer flac when available, but will still download mp3 if no flac is found.
To reject non-flac files entirely, use `--format flac` instead.  
Format lists are unordered: `pref-format = flac,mp3` does not prioritize flac over mp3; both are
treated as equally preferred.

The default required conditions accept common audio formats and enforce the source length when
both source and file length are known:
```ini
format = mp3,flac,ogg,m4a,opus,wav,aac,alac
length-tol = 3
```

The default preferred conditions are:
```ini
pref-format = mp3
pref-length-tol = 3
pref-min-bitrate = 200
pref-max-bitrate = 2500
pref-max-samplerate = 48000
pref-strict-title = true
pref-strict-album = true
```

In other words, by default, Sockseek will
- accept common audio files with no length metadata, or whose length differs from the supplied length by no more than 3 seconds
- prefer mp3 files with bitrate between 200 and 2500 kbps.

Moreover, it will prefer files whose paths contain the supplied title and album.
Changing the last two preferred conditions is not recommended.  

In album mode, required audio-quality conditions (format, bitrate, sample rate, bit depth)
rank or reject whole folders instead of removing individual tracks. A folder with 9 FLAC files
and 1 MP3 is preferred over a mostly-MP3 folder, and the selected folder is still downloaded as
a whole. Use `--strict-album-quality` to require every audio file in the folder to satisfy those
quality conditions. In default mixed-quality mode, coverage is based on the folder contents
Sockseek has seen so far; if a later folder browse reveals hidden files, the coverage can change,
but the folder is still treated as a whole. In strict mode, Sockseek retrieves the full folder when
needed and rejects the candidate before download if hidden files break the required quality
conditions.

Run a song search with `--print results-full` to reveal the sorting logic.

Conditions can also be supplied as a semicolon-delimited string with `--cond` and `--pref`, e.g
`--cond "br>=320; format=mp3,ogg; sr<96000"`. Folder conditions can be included too, such as
`album-track-count>=8` or `required-track-title=Intro`.

### Note on availability of metadata
Some info may be unavailable depending on the client used by the peer. If (e.g) `--min-bitrate`
is set, then Sockseek will still accept any file with unknown bitrate. To reject all files where one
or more of the checked properties is null (unknown), enable `--strict-conditions`.  

This flag should be used with care: It's easy to accidentally exclude all files from users with
certain clients. For example, because the standard Soulseek client does not broadcast the bitrate,
enabling `--strict-conditions` and setting a `--min-bitrate` will make Sockseek ignore all files
shared by users with the standard client. Even without a required min-bitrate, all those shares
will be ranked at the bottom due to the default pref- bitrate checks.
<!-- sockseek-help:end -->

<!-- sockseek-help:start(name-format) -->
## Name format
Variables enclosed in {} will be replaced by the corresponding file tag value.
Name format supports subdirectories as well as conditional expressions like {tag1|tag2} - If
tag1 is null, use tag2. This can be chained arbitrarily many times. String literals enclosed
in parentheses are ignored in the null check.

### Examples
- `{artist} - {title}`  
    Always name it 'Artist - Title'. Because some files on Soulseek are untagged, the
    following is generally preferred:
- `{artist( - )title|filename}`  
    If artist and title are not null, name it 'Artist - Title', otherwise use the original
    filename.
- `{albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}`  
    Sort files into artist/album folders if all tags are present, otherwise put them in
    the 'missing-tags' folder.   

### Available variables

The following values are read from the downloaded file's tags:
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

The following values are taken from the input source (CSV file data, Spotify, etc):
```
sartist                        Source artist
stitle                         Source track title
salbum                         Source album name
slength                        Source track length
uri                            Track URI
snum                           Source item number (1-indexed, including offset)
row/line                       Line number (1-indexed, only for CSV or list input)
```

Other variables:
```
type                           Track type
state                          Track state
failure-reason                 Reason for failure if any
is-audio                       If track is audio (true/false)
artist-maybe-wrong             If artist might be incorrect (true/false)
slsk-filename                  Soulseek filename without extension
slsk-foldername                Soulseek folder name
extractor                      Name of the extractor used
input                          Input string
item-name                      Name of the playlist/source
default-folder                 Default Sockseek folder name
bindir                         Base application directory
outputdir                      Output directory (--path)
configdir                      Active config file directory
path                           Download file path (or folder if album)
path-noext                     Download file path without extension
ext                            File extension
```
<!-- sockseek-help:end -->

<!-- sockseek-help:start(on-complete) -->
## On-Complete Actions
The `--on-complete` parameter allows executing commands after a track or album is downloaded. Multiple actions can be chained using the `+ ` prefix (note the space after +).

**Syntax:** `--on-complete [options] -- command`

Hint: You can use `--mock-files-dir` to test your commands (see [Testing Options](#testing-options)).

Every on-complete command must include the `--` delimiter. Sockseek options go before it; everything after it is the command passed to the operating system.

When passing an on-complete action on the command line, quote the whole value so the delimiter is part of the `--on-complete` argument: `--on-complete "when=success scope=album -- notify-send \"Downloaded\" \"{path}\""`.

### Options
- `when=success` - Execute only for successful downloads
- `when=failure` - Execute for failed or partially successful downloads
- `when=skipped` - Execute for skipped jobs
- `when=already-exists` - Execute only for already-existing skipped jobs
- `when=not-found-last-time` - Execute only for not-found-last-time skipped jobs
- `when=cancelled` - Execute only for cancelled jobs
- `when=partial` - Execute only for partially successful container jobs
- `when=completed` - Execute for all non-skipped terminal outcomes
- `when=any` - Execute for every terminal outcome
- `scope=track` - Execute only for track-level completions
- `scope=album` - Execute only for album-level completions
- `hidden` - Hide the command window
- `shell` - Use shell execute
- `lock` - Serialize this action across jobs
- `update-index` - Use stdout to update the index and playlist path

If `when=` is omitted, it behaves like `when=completed`. This preserves the usual "run when work completed" behavior while avoiding commands for already-existing or not-found-last-time skips.

Sockseek captures bounded stdout/stderr for ordinary on-complete commands, so chained commands can use output variables without an extra option. Commands launched with `shell` use shell execute and cannot expose stdout/stderr. When using `update-index`, stdout should be `ignored;new_path` to update the track path in the index and playlist.

### Variables

The available variables are the same as in [name-format](#available-variables), with the following additions:
- `{exitcode}` - Previous command's exit code
- `{stdout}` - Previous command's stdout
- `{stderr}` - Previous command's stderr
- `{first-exitcode}` - First command's exit code
- `{first-stdout}` - First command's stdout
- `{first-stderr}` - First command's stderr

For album-only (`scope=album`) actions, tag variables such as `{title}`, `{artist}`, and `{album}` are read from the first audio file in the album. Job/source/path variables such as `{sartist}`, `{salbum}`, and `{path}` describe the album-level completion itself.

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
```
c               cancel a job by id or all jobs
t               try next candidate for a job id
i               get detailed info about job by id
```

### CLI Interactive Prompt Shortcuts
Interactive mode for albums can be enabled with `-t`/`--interactive`. It enables you to choose the desired folder or download specific files from it, rather than automatically downloading the best match.

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
`S` only suppresses future prompts for new albums. If an album you already accepted fails and
Sockseek can retry with another candidate, that retry prompt is still shown.
<!-- sockseek-help:end -->

## Examples

##### Download tracks from a CSV file
```bash
sockseek "tracks.csv"
```

##### Download a Spotify playlist or your liked songs
```bash
sockseek "https://open.spotify.com/playlist/id" --spotify-id 123456 --spotify-secret 123456
sockseek "spotify-likes" --spotify-id 123456 --spotify-secret 123456 --spotify-refresh 123456
```

##### Download the albums of a spotify playlist
```bash
sockseek "https://open.spotify.com/playlist/id" --upgrade-to-album --spotify-id 123456 --spotify-secret 123456
```

##### Download a YouTube playlist with yt-dlp fallback & retrieving deleted video names
```bash
sockseek "https://youtube.com/playlist/id" --get-deleted --yt-dlp
```

##### Interactive album download, only include albums with 13 or more tracks
```bash
sockseek "Album Name" -at --atc 13+
```

##### Download a specific song by name and length, preferring lossless
```bash
sockseek "MC MENTAL @ HIS BEST, length=242" --song --pref-format "flac,wav"
``` 

##### Download all albums by an artist interactively
```bash
sockseek "artist=MC MENTAL" -agt
```
This command will show an interactive UI listing all albums with appearances by the specified artist, starting with the most popular (based on the number of shares). You can download or skip albums as needed. Sockseek will do its best to group shares of the same album into a single entry (but due to differences in filenames this will not be 100% reliable). For some artists, it can be useful to add `--strict-artist` to avoid listing incorrect results. There is currently no way to only include albums *by that artist*, rather than every album/compilation where that artist appeared (feel free to request it if needed).

##### Print all songs by an artist which are not in your library
```bash
sockseek "artist=MC MENTAL" --song -g --skip-music-dir "path/to/music" --print results
```

### Advanced example: Automatic wishlist downloader
Create a file named `wishlist.txt`, and add some items as detailed in [Input types: List](#list-file):
```
"Artist - Some Album"                   strict-album=true;album-track-count=5
s:"Artist - My Favorite Song"           strict-title=true;format=flac
```
Add a profile to your `sockseek.conf`:
```ini
[wishlist]
input = {configdir}/wishlist.txt 
input-type = list
index-path = {configdir}/wishlist-index.csv
log-file = {configdir}/wishlist.log
```
This will create a global index file `wishlist-index.csv` which will be scanned every time Sockseek is run to skip wishlist items that have already been downloaded. If you want to continue searching until a version satisfying the preferred conditions is found, also add `skip-check-pref-cond = true` (note that this requires the files to remain in the same spot after being downloaded).  

Now you can manually run, or set up a cron job / scheduled task to periodically run Sockseek with the following option:
```bash
sockseek --profile wishlist
```

<!-- sockseek-help:start(notes-and-tips) -->
## Notes
- **Soulseek's rate limits**: The server bans users for 30 minutes if too many searches are performed within a short timespan. Sockseek has a search limiter which can be adjusted with `--searches-per-time` and `--searches-renew-time` (when the limit is reached, the status of the downloads will be 'Waiting'). By default it is configured to allow up to 34 searches every 220 seconds.

## Tips

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
You can test almost any aspect of the search and downloading logic by using `--mock-files-dir` and pointing it to a local directory containing audio files. This directory will then be used instead of searching Soulseek. Example:
```
sockseek "Artist - Album" -t --mock-files-dir /path/to/dir
```
If you plan to use a large music library, you may want to add `--mock-files-no-read-tags` to improve the initial loading performance. But note that reading tags is required when filtering by metadata such as length or bitrate.

<!-- sockseek-help:end -->

## Options reference

Most used flags at a glance:

```text
-s, --song                      Treat string input as song search
-t, --interactive               Pick from album results before downloading
-g, --aggregate                 Download distinct songs/albums from grouped results
-p, --path <path>               Download directory
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
-p, --path <path>               Download directory
--input-type <type>             [csv|youtube|spotify|bandcamp|string|list|soulseek|
                                musicbrainz] (default: auto)
-s, --song                      Song mode for string input
--name-format <format>          Name format for downloaded tracks. See `--help name-format`
--invalid-replace-str <str>     Replacement string for invalid path characters (default: space)

-n, --number <maxtracks>        Download the first n tracks of a playlist
-o, --offset <offset>           Skip a specified number of tracks
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

--no-skip-existing              Do not skip downloaded tracks
--skip-mode-output-dir <mode>   How to match files in the output dir: name|tag|index
                                (default: index)
--skip-check-cond               Check file conditions when skipping existing files
--skip-check-pref-cond          Check preferred conditions when skipping existing files
--skip-music-dir <path>         Also skip downloading tracks found in a music library
--skip-mode-music-dir <mode>    How to match files in --skip-music-dir: name|tag
                                (default: name)
--skip-not-found                Skip searching for tracks that weren't found on Soulseek
                                during the last run.

--listen-port <port>            Port for incoming connections (default: 49998)
--no-listen                     Disable the incoming connection listener
--connect-timeout <ms>          Timeout used when logging in to Soulseek (default: 20000ms)
--user-description <desc>       Optional description text for your Soulseek account
--shared-files <int>            Number of files you share on Soulseek (default: 0)
--shared-folders <int>          Number of folders you share on Soulseek (default: 0)

--on-complete <command>         Run a command when a download completes. See `--help
                                on-complete`
```
#### Daemon / Remote Options
```
sockseek daemon                 Start the HTTP/SignalR daemon instead of running a download
--server-ip <ip>                IP/interface for the daemon HTTP API (default: 127.0.0.1)
--server-port <port>            Port for the daemon HTTP API (default: 5030)
--remote <url>                  Use an existing daemon instead of running locally
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
-d, --desperate                 Tries harder to find the desired track by searching for the
                                artist/album/title only, then filtering. (slower search)
--no-remove-special-chars       Keep special characters in Soulseek search terms
--max-retries <num>             Max download retries per item (default: 10)
--unknown-error-retries <num>   Extra retries for unknown/transient errors (default: 2)
--fails-to-downrank <num>       Number of fails to downrank a user's shares (default: 1)
--fails-to-ignore <num>         Number of fails to ban/ignore a user's shares (default: 2)

--yt-dlp                        Use yt-dlp to download tracks that weren't found on
                                Soulseek. yt-dlp must be available from the command line.
--yt-dlp-argument <str>         The command line arguments when running yt-dlp. Default:
                                "{id}" -f bestaudio/best -ci -o "{savepath-noext}.%(ext)s" -x
                                Available vars are: {id}, {savedir}, {savepath},
                                {savepath-noext}.
                                Warning: If you change the -o parameter, Sockseek won't be able
                                to index the downloaded files.
                                Note that -x causes yt-dlp to download webms in case ffmpeg
                                is unavailable.

--search-timeout <ms>           Max search time in ms (default: 6000)
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
--strict-artist                 Require artist in path
--strict-album                  Require album in folder path
--banned-users <list>           Comma-separated list of users to ignore
--allowed-users <list>          Comma-separated list of users to allow
--cond <conditions>             Semicolon-delimited required conditions

--pref-format <formats>         Preferred format(s) for ranking. Use --format to require
                                formats strictly. Comma-separated, unordered. (def.: mp3)
--pref-length-tol <sec>         Preferred length tolerance, -1 to disable (default: 3)
--pref-min-bitrate <rate>       Preferred minimum bitrate (default: 200)
--pref-max-bitrate <rate>       Preferred maximum bitrate (default: 2500)
--pref-min-samplerate <rate>    Preferred minimum sample rate
--pref-max-samplerate <rate>    Preferred maximum sample rate (default: 48000)
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
--strict-album-quality          Require every audio file in an album folder to satisfy required
                                quality conditions such as --format, bitrate, sample rate, and
                                bit depth. By default mixed-quality folders are ranked by coverage.
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
                                output dir}/failed, 'move:<path>' to move to a custom path,
                                'delete' to delete them, or 'keep' to leave them where
                                they are.
```
#### Aggregate Download Options
```
-g, --aggregate                 Aggregate download mode: Find and download all distinct
                                songs associated with the provided artist, album, or title.
--aggregate-length-tol <tol>    Max length tolerance in seconds to consider two tracks or
                                albums equal. (Default: 3)
--min-shares-aggregate <num>    Minimum number of shares of a track or album for it to be
                                downloaded in aggregate mode. (Default: 2)
--relax-filtering               Slightly relax file filtering in aggregate mode to include
                                more results
```
#### Printing & Debug Options
```
-v, --verbose                   Print extra debug info
-vv, --trace                    Print trace-level debug info
--debug                         Alias for --verbose
--log-file <path>               Write debug info to a specified file
--no-progress                   Disable progress bars/percentages, only simple printing
--progress-json                 Print progress events as JSON lines
--print <option>                Print tracks or search results instead of downloading:
                                'tracks': Print all tracks to be downloaded
                                'tracks-full': Print extended information about all tracks
                                'results': Print search results satisfying file conditions
                                'results-full': Print search results including full paths.
                                'json': Print first result in json format
                                'json-all': Print json of all results in sorted order
                                'link': Print first result slsk:// link
                                'index': Print Sockseek index as formatted json
                                'index-failed': Print failed downloads from Sockseek index
--print-tracks                  Alias for --print tracks
--print-tracks-full             Alias for --print tracks-full
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
### Notes
- Flags can be explicitly disabled by setting them to false, e.g. `--interactive false`.
- Single-character flags can be combined, e.g. `-at` for `-a -t`.
- Acronyms of two- and `--three-word-flags` like `--twf` are also accepted. E.g. `--Mbr` for `--max-bitrate`.
<!-- sockseek-help:end -->



## Docker

Docker documentation has moved to [docs/docker.md](docs/docker.md).
