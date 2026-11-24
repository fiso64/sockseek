# Sockseek

Sockseek is a self-hosted Soulseek client. Run the daemon to search, download, share files and chat from the WebUI. Give it an artist and album, a playlist, or a CSV file and it can also rank the available files against your preferences and download the best matches automatically.

The [getting started guide](docs/pages/index.md) covers both the local CLI and the daemon. The [release archive](docs/pages/installation/release-archive.md) includes both. You can also run the daemon on [Debian](docs/pages/installation/debian.md), on [NixOS](docs/pages/installation/nixos.md) or with [Docker Compose](docs/pages/installation/docker.md). The manual for your installed version is also available at `/docs/` on your daemon, with built-in search.

Want a desktop client, or a graphical interface for the download tool? Run `sockseek daemon --no-auth` in a terminal on your own computer and open the WebUI in your browser. The [desktop UI section](docs/pages/index.md#desktop-ui) has the steps. Sockseek will soon be packaged as a proper desktop app.

For a one-shot download, configure your Soulseek credentials and run:

```sh
sockseek "Artist - Album Title" -t
```

See [one-shot downloads](docs/pages/using-sockseek/one-shot.md), [configuration](docs/pages/using-sockseek/configuration.md), or the [CLI options](docs/pages/reference/options.md). Run `sockseek --help` for terminal help.

This project was formerly named `sldl` (and `slsk-batchdl` before that). See [the release notes](https://github.com/fiso64/sockseek/releases/tag/v3.0.4) for why it was renamed to something dumb.

## Making a release

Push a version tag such as `v4.0.0` or `v4.0.0-rc.1`. GitHub builds the six portable archives, checks both Debian packages, and attaches all eight files to a draft release. Review the notes and publish the draft to start the versioned Docker image build.

For a preview from a branch, run `gh workflow run release.yml --ref v4`, substituting your branch name. Download the packages from the Actions run.
