# More Examples

## Given a playlist of songs, download the albums

When album metadata is available and you have a playlist of songs, you can use the `--upgrade-to-album` option to download albums instead of songs:

```bash
sockseek "https://open.spotify.com/playlist/id" --upgrade-to-album
```

Album downloads are of course deduplicated; you won't get many copies of an album just because you have 20 tracks from the same album in the playlist.

## Use yt-dlp as fallback

Sockseek can fall back to downloading song playlists from YouTube using yt-dlp (must be installed separately) when no items are found on Soulseek. It runs a YouTube search first, unless your input is a YouTube playlist, in which case it just downloads directly by ID. Use `--yt-dlp` (and `--yt-dlp-argument` if needed):

```bash
sockseek "your-song-playlist" --yt-dlp
```

You can also use any custom downloader as fallback with [on-complete actions](./on-complete.md).

## Retrieving deleted YouTube item names

YouTube inputs support the experimental `--get-deleted` option, which attempts to browse the [web archive](https://web.archive.org/) to find deleted video names in a playlist:

```bash
bash "https://youtube.com/playlist/id" --get-deleted
```

## Show artist songs which you don't have

[Song Groups](./download-modes.md#song-groups) mode can be used in combination with `--skip-music-dir` to show all songs by an artist on Soulseek which are not in your music library:

```bash
sockseek "artist=MC MENTAL" -s -g --skip-music-dir "path/to/music" --print results
```

Remove the `--print results` part to download those songs.

## Create an automatic wishlist downloader

You can easily build a wishlist downloader. Create a file named `wishlist.txt`, and add some items with [list file](./inputs.md#list-file) syntax:

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
