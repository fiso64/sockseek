# One-shot downloads

The local CLI connects to Soulseek for the duration of each command. If this is your first run, [install the release archive and configure your Soulseek credentials](../installation/release-archive.md).

## Common workflows

### Download a song

Use the `--song`/`-s` flag:

```bash
sockseek "Song Title" -s
sockseek "Artist - Song Title" -s
```

The hyphen ` - ` determines what part of the input is the artist and title, which can be important for [ranking and filtering](../search-and-download/file-conditions.md) (see also [search string syntax](../automated-and-bulk-downloads/inputs.md#search-string)).

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

See [input types](../automated-and-bulk-downloads/inputs.md) for all supported sources. It's a good idea to check the metadata of the first few tracks before downloading your 10'000 song megalist:

```bash
sockseek "your-input" -n 20 --print jobs-full
```

### Download all albums by an artist interactively

[Album Groups mode](../automated-and-bulk-downloads/download-modes.md#album-groups) (`-g`) groups results by album, giving you several folder choices for each one. Albums which are shared the most are sorted first. In interactive mode (`-t`), you can pick which albums to download and which particular version to use. Run it like this:

```bash
sockseek "artist=Artist Name" -gt
```

Note that this may also include compilations the artist appeared in.

### Prefer lossless, but accept other formats

You can make it prefer lossless files while still accepting other formats when no lossless version exists, like this:

```bash
sockseek "Artist - Album Title" --pref-format flac,wav
```

To ban lossy files altogether, use `--format` instead of `--pref-format`. Note that format lists are unordered; `flac,wav` is equivalent to `wav,flac` and does not change the sorting. See [file conditions](../search-and-download/file-conditions.md) for more information about ranking and filtering downloads.

### Skipping existing items

For multi-item inputs like YouTube or Spotify playlists and CSV or list files, Sockseek automatically writes an index file next to the downloads, which means that **successfully downloaded tracks and albums will be skipped on next runs**. If you want a shared index, use the `--index-path` option or config field.

Sockseek can also skip items found in a music library, without any pre-existing index files:

```bash
sockseek "playlist.csv" --skip-music-dir "path/to/music"
```

### More examples

More niche examples can be found [here](../automated-and-bulk-downloads/examples.md).

## About failed or incorrect downloads

Sockseek searches the Soulseek P2P network. Sources like Spotify and YouTube are only used as metadata sources; no audio is downloaded from them. It usually does a good job downloading items, provided they actually exist on the network. The results returned by Soulseek of course depend on spelling, peers, etc. Read [this section](../automated-and-bulk-downloads/tips.md#why-are-some-of-my-downloads-wrong-or-missing) if you're dealing with missing or incorrect downloads.
