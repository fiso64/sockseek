# Install with Docker Compose

Download and extract the source archive for your [Sockseek release](https://github.com/fiso64/sockseek/releases), or use a v4 checkout. Run the commands below alongside `docker-compose.yml`. Compose builds the image from that version and runs the daemon.

## First run

1. Create the host directories. On Linux, set the container user to the UID and GID that own your downloads. For a fresh checkout:

   ```sh
   mkdir -p instance/config instance/data instance/downloads instance/shares
   chmod 700 instance/config instance/data
   echo "SOCKSEEK_USER=$(id -u):$(id -g)" >> .env
   ```

   The image defaults to UID and GID 1654. If you keep an existing music library under another account, give the container user read access to that library.

2. Build the image and choose a WebUI password. Copy the hash printed by the second command.

   ```sh
   docker compose build
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

The example permits HTTP login on port **5030** and publishes that port only on the host's loopback interface. For access from other machines, see [WebUI and access options](../reference/daemon-options.md#webui-and-access). Port **49998/TCP** is the Soulseek peer listener. To accept incoming peer connections behind a router, forward that port to the Docker host. If you change it, use the same port in `listen-port`, the Compose mapping, and the router.

## Updates and CLI use

After checking out a new release, run `docker compose up -d --build` to replace the container. The mounted directories carry your configuration, jobs and downloads across the update.

For CLI help, run `docker compose run --rm sockseek --help`. For server maintenance, stop the daemon before running an offline database command. To schedule one-shot jobs, invoke the CLI from the host's scheduler. Use [remote CLI](../using-sockseek/daemon.md#remote-cli) to submit work to an already connected daemon.
