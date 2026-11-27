# Install on Debian

Add Sockseek's apt repository, then install the package. It includes the CLI, daemon, WebUI and .NET runtime, for Debian 13 and Ubuntu 24.04 or newer, on `amd64` and `arm64`.

```sh
sudo install -d -m 0755 /etc/apt/keyrings
sudo curl -fsSLo /etc/apt/keyrings/sockseek.asc https://fiso64.github.io/sockseek/apt/sockseek.asc
echo "deb [signed-by=/etc/apt/keyrings/sockseek.asc] https://fiso64.github.io/sockseek/apt stable main" \
  | sudo tee /etc/apt/sources.list.d/sockseek.list
sudo apt update
sudo apt install sockseek
```

To install without the repository, download the `.deb` for your architecture from [GitHub Releases](https://github.com/fiso64/sockseek/releases) (`dpkg --print-architecture` shows which) and run `sudo apt install ./sockseek_VERSION_amd64.deb`.

The CLI is ready to use. To start the server:

1. Run `sockseek auth hash-password` to generate a WebUI password hash.
2. Run `sudoedit /etc/sockseek/sockseek.conf`. Set `username` and `password` for Soulseek and paste the hash into `web-password-hash`.
3. Run `sudo systemctl enable --now sockseek`. Open <http://127.0.0.1:5030> on the server and sign in as `admin` with your WebUI password.


## Logs, storage, other

- **Logs** — Run `journalctl -u sockseek` to read the service log.
- **Storage** — Sockseek keeps its database and history in the private `/var/lib/sockseek/data`. Downloads go in `/var/lib/sockseek/downloads` by default and are readable by other local users, for example a media server.
- **Other folders** — To download into an existing media library, set `output-dir` to its path and give the `sockseek` service user write access.
- **Shared write access** — To let a group such as `media` write downloads too, add `sockseek` to that group and set the directory's setgid bit so downloads inherit the group. Then run `sudo systemctl edit sockseek`, add `UMask=0002` under `[Service]`, and restart the service.
- **Incoming connections** — See [ports and connectivity](../using-sockseek/daemon.md#ports-and-connectivity) to let Soulseek peers reach you.
- **Upgrades** — Run `sudo apt update && sudo apt upgrade`. Without the repository, install the newer `.deb` the same way as the first. An active service restarts, and its configuration, history and downloads are preserved.
