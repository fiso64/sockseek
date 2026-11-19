# Getting started

Sockseek can run for a single download on your computer or stay connected as a daemon. Both can download albums, tracks, and playlists.

## Soulseek Client

The background daemon is a full Soulseek client and includes a webui. You can search in the browser, queue downloads, share a library, and receive messages. Downloads continue on the daemon machine when you close the browser. You can also submit jobs from the [remote CLI](./using-sockseek/daemon.md#remote-cli).

- **Release archive:** [Install the executable](./installation/release-archive.md), then [start the daemon](./using-sockseek/daemon.md#start-the-daemon).
- **Docker Compose:** [Set up the container](./installation/docker.md). 

Once signed in, try [your first WebUI search or download](./using-sockseek/daemon.md#your-first-webui-search-or-download).


## Local CLI (one-shot downloading)

Use the local CLI to download from a terminal or script. It connects to Soulseek, saves the files on your computer, and exits when the job finishes.

[Install from a release archive](./installation/release-archive.md) and set your Soulseek credentials. Then try an interactive album download:

```sh
sockseek "Artist - Album Title" -t
```

See [one-shot downloads](./using-sockseek/one-shot.md) for songs, playlists, CSV files, and unattended downloads.
