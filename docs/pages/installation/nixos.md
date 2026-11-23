# Install on NixOS

Add Sockseek to your system flake.

```nix
inputs.sockseek.url = "github:fiso64/sockseek/v4";
```

Include `sockseek` in the arguments to `outputs` and add its module to your host alongside your existing modules.

```nix
modules = [ ./configuration.nix sockseek.nixosModules.default ];
```

Without flakes, import `nix/module.nix` from a pinned Sockseek checkout and use nixpkgs with the .NET 11 SDK. Set `services.sockseek.package` to override the package.

In `configuration.nix`, enable the service and supply your credentials. Add the two password files at runtime with read access for the `sockseek` user.

```nix
services.sockseek = {
  enable = true;
  settings = {
    username = "your-soulseek-username";
    password-file = "/run/secrets/soulseek-password";
    web-password-file = "/run/secrets/sockseek-web-password";
    allow-http-auth = true; # Allow web login over loopback HTTP.
  };
};
```

Apply the configuration with `sudo nixos-rebuild switch`. On the NixOS host, open <http://127.0.0.1:5030> and sign in as `admin` with your WebUI password.

- **Logs** — Run `journalctl -u sockseek` to read the service log.
- **Storage** — Sockseek keeps its database and history in the private `/var/lib/sockseek/data`. Downloads go in `/var/lib/sockseek/downloads` by default and are readable by other local users, for example a media server.
- **Shared write access** — To let a group such as `media` write downloads too, add `sockseek` to that group, set the download folder's group and setgid bit, and set `systemd.services.sockseek.serviceConfig.UMask = "0002";`.
- **Other folders** — Set `output-dir` to download elsewhere or `share` to share an existing library. Give the `sockseek` user write access to the download folder and read access to shared folders.
- **Incoming connections** — To accept connections from Soulseek peers, add your listener port (default `49998`) to `networking.firewall.allowedTCPPorts`.

## Settings and profiles

Use the configuration names from the [options](../reference/options.md) and [daemon](../reference/daemon-options.md) references. A list replaces the inherited items, `[]` clears them, and `{ append = [ ... ]; }` adds items. Each list element is literal, so a username can contain a comma.

```nix
services.sockseek = {
  settings.pref-format = [ "flac" ];
  settings.user-description = ''
    Welcome to my library.
    Blah.
  '';
  profiles = [
    {
      name = "lossless";
      settings.format = [ "flac" "wav" ];
    }
    {
      name = "albums";
      condition = ''download-mode == "album"'';
      settings.pref-format.append = [ "wav" ];
    }
  ];
};
```

Select `lossless` when submitting a job. Sockseek applies matching automatic profiles in declaration order.

## CLI-only installation

Enabling the service also installs the `sockseek` CLI. To install just the CLI, use the flake's default package for x86-64 or ARM64 Linux.

You can also try the CLI before installing it.

```sh
nix run github:fiso64/sockseek/v4 -- --help
```
