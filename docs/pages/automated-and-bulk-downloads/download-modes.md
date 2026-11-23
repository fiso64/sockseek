<!-- sockseek-help:start(download-modes) -->

# Download modes

Sources usually decide for themselves whether they contain songs or albums. String inputs are treated as albums by default. There are four main download modes:

## Song

Song mode is the default for playlists from streaming platforms and CSV song lists, and downloads one file per item. Use `-s/--song` for string/list input that should be treated as a song search.

Example:

```bash
sockseek "Artist - Song Name" -s
```

## Album

In Album mode, Sockseek downloads whole folders. It is the default for string inputs and album sources such as Spotify/Bandcamp album links and CSV album rows. Use `-t` to pick interactively.

Example:

```bash
sockseek "Artist - Album Name" -t
```

See [Album options](../reference/options.md#album-download-options) for album-specific options.

## Album Groups

Album Groups mode is activated when `-g/--group` is enabled for album inputs. In this mode, Sockseek will attempt to group folders returned by Soulseek and download one of each distinct album, starting with the one shared by the most peers.

Note that `--min-group-shares` is set to 2 by default, meaning that albums shared by only one user will be ignored. Album Groups mode can be used to determine the most common version of a particular album, or download the most popular (or all) albums by an artist. It's recommended to pair it with `-t`/`--interactive`.

Example:

```bash
sockseek "artist=Artist Name" -gt
```

See [Grouping options](../reference/options.md#group-download-options) for group options.

## Song Groups

Song Groups mode is enabled with `--group --song` (or `-gs` for short). Sockseek will attempt to group files returned by Soulseek and download one of each distinct song, starting with the one shared by the most peers.

As in Album Groups mode, `--min-group-shares` is set to 2 by default, meaning that songs shared by only one user will be ignored. This mode is useful for downloading all songs by an artist available on Soulseek.

Example:

```bash
sockseek "artist=Artist Name" -gs
```

See [Grouping options](../reference/options.md#group-download-options) for group options.

<!-- sockseek-help:end -->
