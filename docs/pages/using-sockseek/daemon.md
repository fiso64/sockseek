<!-- sockseek-help:start(daemon) -->
# Daemon and remote access

The daemon serves the WebUI, HTTP API, and bundled manual on the same port. It listens on `127.0.0.1:5030` by default. To listen on another interface, set `server-ip` to its address. The Soulseek peer listener uses a separate `listen-port`.

## Start the daemon

If you installed the [release archive](../installation/release-archive.md), run `sockseek auth hash-password` and enter the password you want to use for the WebUI. Add the printed hash to your `sockseek.conf`:

```ini
web-password-hash = paste-the-hash-here
```

Start the daemon on your computer:

```sh
sockseek daemon --allow-http-auth
```

Open [http://127.0.0.1:5030](http://127.0.0.1:5030) and sign in as `admin` with your WebUI password.

## Your first WebUI search or download

Use the main search bar to inspect results and choose the files or folder you want. For an album, try `Artist - Album Title`. Group modes collect related songs or albums so you can compare the versions offered by different peers. Filtering a results page works on the responses already received. You can change the filter after the search finishes.

For an automatic download, create a download job. Sockseek can take a search string, a playlist, a CSV, or a list and pick matches using your [file conditions](../search-and-download/file-conditions.md). Required conditions decide which results qualify. Preferred conditions help choose among the qualifying results. For CSV and list inputs, upload the file from your browser or enter a path on the daemon machine. Downloads are saved on that machine in either case. See [download modes](../automated-and-bulk-downloads/download-modes.md) and [supported inputs](../automated-and-bulk-downloads/inputs.md) for examples.

## Share a folder

Add a directory on the daemon machine to your config, for example `share = /path/to/Music`. Use **System → Rescan shares** to update the catalog, then check **System → Shares** to see what peers can browse. [Sharing and upload options](../reference/daemon-options.md#sharing-uploads-and-chat)

## Remote CLI

Create a named API key in **System → API**. Copy the secret when it is shown and save it in a file on the client machine. You can now submit downloads from the CLI:

```sh
sockseek "Artist - Album Title" --remote https://your-daemon --remote-api-key-file /path/to/key
```

Use `--detach` to print the workflow ID and exit, or `--monitor` to watch active workflows. Files are downloaded to the daemon's output directory. The same connection options work with commands such as `sockseek chat conversations` and `sockseek transfers`. Put `remote` and `remote-api-key-file` in a [configuration profile](./configuration.md#configuration-profiles) if you also use the CLI for local downloads.

For key management, offline maintenance, and sharing options, see the [daemon reference](../reference/daemon-options.md).
<!-- sockseek-help:end -->
