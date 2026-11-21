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

The database and job history live in the private `/var/lib/sockseek/data` directory. Downloads go to `/var/lib/sockseek/downloads`. For an existing media library, set `output-dir` to its path and give the `sockseek` service user write access. For example, if a library uses a shared `media` group, add `sockseek` to that group and set the directory's setgid bit so downloads inherit the media group. Restart the service after changing group membership.

Use `journalctl -u sockseek` for logs. To upgrade, install the newer file with the same `apt install ./…deb` command. An active service restarts, and its configuration, history and downloads are preserved.
