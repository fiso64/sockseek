# Install with Docker Compose

Save [`docker-compose.yml`](https://github.com/fiso64/sockseek/blob/v4/docker-compose.yml) in an empty directory and run the commands below from there. It uses the image `ghcr.io/fiso64/sockseek:4`, which always has the latest Sockseek 4 release. To choose when you update, change the tag to a version such as `:4.0.0`.

## First run

1. Create the host directories. On Linux, set the container user to the UID and GID that own your downloads. For example:

   ```sh
   mkdir -p instance/config instance/data instance/downloads instance/shares
   chmod 700 instance/config instance/data
   echo "SOCKSEEK_USER=$(id -u):$(id -g)" >> .env
   ```

   The image defaults to UID and GID 1654. If you keep an existing music library under another account, give the container user read access to that library.

2. Pull the image and choose a WebUI password. Copy the hash printed by the second command.

   ```sh
   docker compose pull
   docker compose run --rm sockseek auth hash-password
   ```

3. Create `instance/config/sockseek.conf`:

   ```ini
   username = your-soulseek-username
   password = your-soulseek-password
   web-password-hash = paste-the-hash-here
   allow-http-auth = true
   ```

   Use a separate Soulseek account if another client is already signed in with yours. The WebUI password is independent of the Soulseek password.

4. Start Sockseek and open [http://127.0.0.1:5030](http://127.0.0.1:5030) on the Docker host. Sign in as `admin` with the WebUI password. The [first search and download guide](../using-sockseek/daemon.md#your-first-webui-search-or-download) picks up here.

   ```sh
   docker compose up -d
   docker compose logs -f sockseek
   ```

## Storage and ports

| Host directory | Container path | Contents |
| --- | --- | --- |
| `./instance/config` | `/etc/sockseek` | Configuration and optional password file, mounted read-only |
| `./instance/data` | `/var/lib/sockseek` | Daemon database, history, and other persistent state |
| `./instance/downloads` | `/downloads` | Default download destination |
| `./instance/shares` | `/shares` | Optional read-only music library |

Paths in `sockseek.conf` refer to the container. To share an existing library, change the host side of the `/shares` mount to its location, add `share = /shares` to the config, and rescan from the WebUI.

The example permits HTTP login on port **5030** and publishes that port only on the host's loopback interface. For access from other machines, see [WebUI and access options](../reference/daemon-options.md#webui-and-access). Port **49998/TCP** is the Soulseek peer listener. To accept incoming peer connections behind a router, forward that port to the Docker host (see [ports and connectivity](../using-sockseek/daemon.md#ports-and-connectivity)). If you change it, use the same port in `listen-port`, the Compose mapping, and the router.

## Updates and CLI use

To update, run `docker compose pull`, then `docker compose up -d`. Your configuration, jobs and downloads stay in the mounted directories.

For CLI help, run `docker compose run --rm sockseek --help`. For server maintenance, stop the daemon before running an offline database command. To schedule one-shot jobs, invoke the CLI from the host's scheduler. Use [remote CLI](../using-sockseek/daemon.md#remote-cli) to submit work to an already connected daemon.
