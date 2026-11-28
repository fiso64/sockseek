<!-- sockseek-help:start(download-modes) -->

# Download modes

There are five download modes: Song, Album, Song Groups, Album Groups, and Generic. In the GUI, you can select which mode to use for the next search using the button in the search bar, and set the default using the `web-default-search-mode` option in the config. For [automated downloads](./auto-downloads.md), sources usually decide for themselves whether they contain songs or albums. In the CLI, string inputs are treated as albums by default.

## Song

Song mode is the default for playlists from streaming platforms and CSV song lists, and downloads one file per item. In the CLI, use `-s/--song` for string/list input that should be treated as a song search. A list of songs can be upgraded to album downloads using `--upgrade-to-album` (albums will be deduplicated).

CLI Example (string input):

```bash
sockseek "Artist - Song Name" -s
```

## Album

In Album mode, Sockseek downloads whole folders. It is the default for CLI string inputs and album sources such as Spotify/Bandcamp album links and CSV album rows. In the CLI, `-t` to pick interactively.

CLI Example (string input):

```bash
sockseek "Artist - Album Name" -t
```

See [Album options](../reference/options.md#album-download-options) for album-specific options.

## Album Groups

Album Groups mode is activated when `-g/--group` is enabled for album inputs. In this mode, Sockseek will attempt to group folders returned by Soulseek and download one of each distinct album, starting with the one shared by the most peers.

Note that `--min-group-shares` is set to 2 by default, meaning that albums shared by only one user will be ignored. Album Groups mode can be used to determine the most common version of a particular album, or download the most popular (or all) albums by an artist. It's recommended to pair it with `-t`/`--interactive` in the CLI, which allows you to pick the particular version of the album in each group and skip groups you don't want.

CLI Example:

```bash
sockseek "artist=Artist Name" -gt
```

See [Grouping options](../reference/options.md#group-download-options) for group options.

## Song Groups

Song Groups mode is enabled with `--group --song` (or `-gs` for short). Sockseek will attempt to group files returned by Soulseek and download one of each distinct song, starting with the one shared by the most peers.

As in Album Groups mode, `--min-group-shares` is set to 2 by default, meaning that songs shared by only one user will be ignored. This mode is useful for downloading all songs by an artist available on Soulseek.

CLI Example:

```bash
sockseek "artist=Artist Name" -gs
```

See [Grouping options](../reference/options.md#group-download-options) for group options.

## Generic Files

This mode is only available in the GUI and behaves much like the usual search in clients like SoulseekQt and Nicotine+. Unlike album and song modes, it does not set any default filters for music, and does no special [ranking](./ranking-and-filtering.md) of search results beyond the usual sorting mode (like by speed or by file size) that you chose. This mode is useful when you're looking for non-music downloads, like PDFs or videos. 

<!-- sockseek-help:end -->
