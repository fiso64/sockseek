# Install on Debian

Download the `.deb` for your architecture from [GitHub Releases](https://github.com/fiso64/sockseek/releases). Debian 13 packages include the CLI, daemon, WebUI and .NET runtime. Run `dpkg --print-architecture` to choose between `amd64` and `arm64`.

Install the downloaded file, substituting its filename:

```sh
sudo apt install ./sockseek_VERSION_amd64.deb
```

The CLI is ready to use. To start the server:

1. Run `sockseek auth hash-password` to generate a WebUI password hash.
2. Run `sudoedit /etc/sockseek/sockseek.conf`. Set `username` and `password` for Soulseek and paste the hash into `web-password-hash`.
3. Run `sudo systemctl enable --now sockseek`. Open <http://127.0.0.1:5030> on the server and sign in as `admin` with your WebUI password.

- **Logs** — Run `journalctl -u sockseek` to read the service log.
- **Storage** — Sockseek keeps its database and history in the private `/var/lib/sockseek/data`. Downloads go in `/var/lib/sockseek/downloads` by default and are readable by other local users, for example a media server.
- **Other folders** — To download into an existing media library, set `output-dir` to its path and give the `sockseek` service user write access.
- **Shared write access** — To let a group such as `media` write downloads too, add `sockseek` to that group and set the directory's setgid bit so downloads inherit the group. Then run `sudo systemctl edit sockseek`, add `UMask=0002` under `[Service]`, and restart the service.
- **Incoming connections** — See [ports and connectivity](../using-sockseek/daemon.md#ports-and-connectivity) to let Soulseek peers reach you.
- **Upgrades** — Install the newer file with the same `apt install ./…deb` command. An active service restarts, and its configuration, history and downloads are preserved.
