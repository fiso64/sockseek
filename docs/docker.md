# Docker

> [!WARNING]
> This Docker documentation may be outdated. It was written before Sockseek's daemon / remote mode was added and needs a full review.

A docker container for running Sockseek can be built from this repository. The image supports linux x86/ARM. 

To build and start container:

```shell
clone https://github.com/fiso64/sockseek
cd Sockseek
docker compose up -d
```

`exec` into the container to start using Sockseek:

```shell
docker compose exec sockseek sh
sockseek --help
```

The compose stack mounts two directories relative to where `docker-compose.yml` is located which can be used for file management:

* `/config` (at `./config` on host) - put your `sockseek.conf` configuration in this directory and then use `sockseek -c /config ...` to use your configuration in the container
* `/data` (at `./data` on host) - use as the download directory IE `sockseek -p /data ...`

## File Permissions

If you are running Docker on a **Linux Host** you should specify `user:group` permissions of the user who owns the **configuration and data directory** on the host to avoid [docker file permission problems.](https://ikriv.com/blog/?p=4698) These can be specified using the [environmental variables **PUID** and **PGID**.](https://docs.linuxserver.io/general/understanding-puid-and-pgid)

To get the UID and GID for the current user run these commands from a terminal:

* `id -u` -- prints UID
* `id -g` -- prints GID

Replace these with the corresponding variable (`PUID` `PGID`) in `docker-compose.yml`.


## Cron

One or more Sockseek commands can be run on a schedule using [cron](https://en.wikipedia.org/wiki/Cron) built into the container.

To create a schedule make a new file on the host `./config/crontabs/abc` and use it with the standard [crontab](https://en.wikipedia.org/wiki/Cron#Overview) syntax.

Make sure to restart the container after any changes to the cron file are made.

Example => Run Sockseek every Sunday at 1am, search for missing tracks from the specified Spotify playlist

```
# min   hour    day     month   weekday command
0 1 * * 0 sockseek https://open.spotify.com/playlist/6sf1WR5grXGJ6dET -c /config -p /data --index-path /data/index.sockseek --spotify-id 123456 --spotify-secret 123456
```

[crontab.guru](https://crontab.guru/) could be used to help with the scheduling expression.
