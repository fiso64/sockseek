# One-shot downloads

The local CLI connects to Soulseek for the duration of each command. If this is your first run, [install the release archive and configure your Soulseek credentials](../installation/release-archive.md).

## Common workflows

### Download a song

Use the `--song`/`-s` flag:

```bash
sockseek "Song Title" -s
sockseek "Artist - Song Title" -s
```

The hyphen ` - ` determines what part of the input is the artist and title, which can be important for [ranking and filtering](../downloading/ranking-and-filtering.md) (see also [search string syntax](../downloading/auto-downloads.md#search-string)).

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

See [input types](../downloading/auto-downloads.md) for all supported sources. It's a good idea to check the metadata of the first few tracks before downloading your 10'000 song megalist:

```bash
sockseek "your-input" -n 20 --print jobs-full
```

### Download all albums by an artist interactively

[Album Groups mode](../downloading/download-modes.md#album-groups) (`-g`) groups results by album, giving you several folder choices for each one. Albums which are shared the most are sorted first. In interactive mode (`-t`), you can pick which albums to download and which particular version to use. Run it like this:

```bash
sockseek "artist=Artist Name" -gt
```

Note that this may also include compilations the artist appeared in.

### Prefer lossless, but accept other formats

You can make it prefer lossless files while still accepting other formats when no lossless version exists, like this:

```bash
sockseek "Artist - Album Title" --pref-format flac,wav
```

To ban lossy files altogether, use `--format` instead of `--pref-format`. Note that format lists are unordered; `flac,wav` is equivalent to `wav,flac` and does not change the sorting. See [ranking and filtering](../downloading/ranking-and-filtering.md) for more information.

### Skipping existing items

For multi-item inputs like YouTube or Spotify playlists and CSV or list files, Sockseek automatically writes an index file next to the downloads, which means that **successfully downloaded tracks and albums will be skipped on next runs**. If you want a shared index, use the `--index-path` option or config field.

Sockseek can also skip items found in a music library, without any pre-existing index files:

```bash
sockseek "playlist.csv" --skip-music-dir "path/to/music"
```

## More examples

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

You can also use any custom downloader as fallback with [on-complete actions](../downloading/on-complete.md).

### Retrieving deleted YouTube item names

YouTube inputs support the experimental `--get-deleted` option, which attempts to browse the [web archive](https://web.archive.org/) to find deleted video names in a playlist:

```bash
sockseek "https://youtube.com/playlist/id" --get-deleted
```

### Show artist songs which you don't have

[Song Groups](../downloading/download-modes.md#song-groups) mode can be used in combination with `--skip-music-dir` to show all songs by an artist on Soulseek which are not in your music library:

```bash
sockseek "artist=MC MENTAL" -s -g --skip-music-dir "path/to/music" --print results
```

Remove the `--print results` part to download those songs.

### Create an automatic wishlist downloader

You can easily build a wishlist downloader. Create a file named `wishlist.txt`, and add some items with [list file](../downloading/auto-downloads.md#list-file) syntax:

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

Now set up a cron job (on Linux) or a scheduled task (on Windows) to periodically run Sockseek with the following option:

```bash
sockseek --profile wishlist
```

You can also just manually run it, for example in interactive mode (`-t`).

## About failed or incorrect downloads

Sockseek searches the Soulseek P2P network. Sources like Spotify and YouTube are only used as metadata sources; no audio is downloaded from them. It usually does a good job downloading items, provided they actually exist on the network. The results returned by Soulseek of course depend on spelling, peers, etc. If downloads are missing or wrong, read [why some downloads are wrong or missing](../downloading/troubleshoot.md#why-are-some-of-my-downloads-wrong-or-missing).
