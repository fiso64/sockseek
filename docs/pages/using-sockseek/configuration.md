<!-- sockseek-help:start(config) -->

# Configuration

## Config Location

Sockseek will look for a file named sockseek.conf in the following locations:

- `~/.config/sockseek/sockseek.conf` (Any platform)
- `%APPDATA%\sockseek\sockseek.conf` (Windows only)
- `$XDG_CONFIG_HOME/sockseek/sockseek.conf`
- `{sockseek executable dir}/sockseek.conf`

Use `--config <path>` to choose a config file, `--config none` or `--no-config` to skip config loading.

The daemon automatically reloads config changes; share changes require a rescan, and some settings still require a restart.

## Syntax

Example config file:

```ini
username = your-username
password = your-password
pref-format = flac
fast-search = true
```

Lines starting with `#` will be treated as comments. Tildes in paths are expanded as the user
directory (even on Windows). Path settings also support `{bindir}` for the Sockseek binary directory
and `{configdir}` for the directory containing the active config file.

## Configuration profiles

Sockseek supports configuration profiles:

```ini
[lossless]
pref-format = flac,wav
```

To activate the above profile, use `--profile lossless`. This will apply all of its options. Your manual CLI options still take precedence.

To list all configured profiles, run `--profile help`.

## Auto-profiles

Profiles can be activated automatically based on a few simple conditions. For example:

```ini
# never automatically cancel album downloads in interactive mode
[no-stale]
profile-cond = interactive && download-mode == "album"
max-stale-time = 9999999

# download to another location for YouTube
[youtube]
profile-cond = input-type == "youtube"
output-dir = ~/downloads/sockseek-youtube

# use structural naming for non-music downloads
[generic-layout]
profile-cond = download-mode == "generic"
name-format = {peer-username}/{foldername}/{filename}
```

The following operators are supported for use in `profile-cond`: `&&`, `||`, `==`, `!=`, `!{bool}`.
The following variables are available:

```
input-type        "youtube"|"csv"|"string"|"bandcamp"|"spotify"|"list"|
                  "musicbrainz"|"soulseek-link"|"manual-search"|
                  "peer-shares"|"none"
download-mode     "song"|"song-groups"|"album"|"album-groups"|"generic"
album             bool
group             bool
interactive       bool
progress-json     bool
no-progress       bool
```

## Lists

Lists can be either be written as comma-separated single lines:

```ini
banned-users = Alice,Bob
```

Or multiline:
```ini
banned-users =
  - Alice
  - Bob
```

Use `= +` to append to the current list, including values inherited from another profile. Commas and leading plus signs inside an item are literal.

```ini
[weekend]
allowed-users = +
    - Bob
    - + Friends
```

## WebUI search defaults

Use `input-type == "manual-search"` in an auto-profile to set defaults for the WebUI search bar. Add a `download-mode` condition to tailor the defaults for one search mode.

```ini
[webui-search]
profile-cond = input-type == "manual-search"
search-timeout = 20000
pref-format = flac

[webui-album-search]
profile-cond = input-type == "manual-search" && download-mode == "album"
name-format = {albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}
```

<!-- sockseek-help:end -->
