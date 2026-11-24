# Coming from 3.x

Sockseek 3.x was a command-line downloader. 4.0 keeps that tool and adds a daemon that is a full Soulseek client, with a WebUI, file sharing and chat.

## One-shot downloads still work

You do not need the daemon. `sockseek "Artist - Album Title" -t` and every other [one-shot download](./one-shot.md) runs as it did in 3.x: it connects to Soulseek, downloads, and exits.

Your config file is read from the same place, and inputs, file conditions, name formats, profiles and on-complete actions keep their meaning. At most you will need the small config changes below.

## Update your config and scripts

"Aggregate" mode is now called "Groups". The short flag `-g` is unchanged.

| 3.x | 4.0 |
|---|---|
| `--aggregate` | `--group` |
| `--aggregate-length-tol` | `--group-length-tol` |
| `--min-shares-aggregate` | `--min-group-shares` |
| `--input-type soulseek` | `--input-type soulseek-link` |

These options no longer exist:

- **`--extract-max-track-count`**: this is now the default; see below.
- **`--shared-files` and `--shared-folders`**: they only set the numbers reported to the Soulseek server. The daemon now [shares real folders](./daemon.md) and reports their actual counts.

## Changed defaults

- **Album track counts from the source are enforced.** When a source such as Spotify or MusicBrainz says how many tracks an album has, a folder must have exactly that many audio files to match. In 3.x only the minimum was applied unless you opted in. Turn either limit off with `--no-extract-min-tracks` or `--no-extract-max-tracks`.
- **No format or quality is preferred by default.** 3.x preferred MP3 files between 200 and 2500 kbps, up to 48 kHz. 4.0 has none of these preferences, so the same search can pick a different file. To get the old ranking back, add this to your config:

  ```ini
  pref-format = mp3
  pref-min-bitrate = 200
  pref-max-bitrate = 2500
  pref-max-samplerate = 48000
  ```
- **Peers get one more chance before being skipped.** A peer's files are downranked after 2 failed downloads and ignored after 3 (`--fails-to-downrank`, `--fails-to-ignore`). In 3.x it was 1 and 2.

## The daemon

3.x had a daemon that only served the remote CLI. In 4.0 it is the Soulseek client, and it is no longer open by default.

- **It requires sign-in.** Set a WebUI password before starting it, or run it with `--no-auth` on a computer only you can reach. See [daemon and remote access](./daemon.md).
- **The remote CLI needs an API key**, passed with `--remote-api-key` or `--remote-api-key-file`, unless the daemon runs with `--no-auth`.
- **Everything the download tool does is in the WebUI** under **New job**, so a playlist or CSV download no longer needs a terminal.

## Found something else?

4.0 is a beta. If a 3.x command or config no longer does what it did and this page does not say why, please [open an issue](https://github.com/fiso64/sockseek/issues).
