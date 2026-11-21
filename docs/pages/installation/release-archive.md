# Install from a release archive

The same executable runs both the local CLI and the daemon.

1. Download and extract the [release for your OS](https://github.com/fiso64/sockseek/releases). The examples use `sockseek` on your PATH. To run it from the extracted folder, use `./sockseek` on Linux or macOS, or `.\sockseek.exe` in Windows PowerShell.
2. Create `~/.config/sockseek/sockseek.conf` with your Soulseek credentials and the directory where you want downloads saved. On Windows you can also use `%APPDATA%\sockseek\sockseek.conf`.

   ```ini
   username = your-soulseek-username
   password = your-soulseek-password
   output-dir = path/to/your/download/folder
   ```

   If another Soulseek client is signed in, use a separate Soulseek account to keep both connected.

For **local CLI** use, you're ready to [download a song or album](../using-sockseek/one-shot.md#download-a-song). For the **daemon and WebUI**, [set a WebUI password and start the daemon](../using-sockseek/daemon.md#start-the-daemon).
