# Daemon mode

> [!WARNING]
> This document is currently fully AI-authored. It will be rewritten in the future.

Daemon mode keeps Sockseek running in the background so it can accept work from
other terminals or applications. It also keeps job, search, and transfer history
across restarts.

Use daemon mode when you want to:

- submit downloads from another terminal or machine;
- keep one long-running connection to Soulseek;
- view previous jobs and search results;
- use Sockseek through its HTTP API or .NET client.

## Start the daemon

The daemon listens on `127.0.0.1:5030` by default:

```bash
sockseek daemon
```

Open `http://127.0.0.1:5030/` in a browser to use the WebUI. Published
Sockseek builds bundle it with the daemon; an ordinary source `dotnet build`
does not build the frontend (see [`webui/README.md`](../webui/README.md)).
The WebUI uses the same API and has the same network access implications
described under [Network security](#network-security).

Leave this terminal open, or run the process through your preferred service
manager.

To use another port:

```bash
sockseek daemon --server-port 8080
```

To listen on every network interface:

```bash
sockseek daemon --server-ip 0.0.0.0
```

The daemon uses the same Soulseek credentials, download settings, config file,
and profiles as the regular CLI.

Run `sockseek daemon --help` for a short overview. The complete option reference,
including database and retention settings, is available through:

```bash
sockseek --help
```

## Submit work remotely

Set the daemon URL once in the default configuration:

```ini
remote = http://127.0.0.1:5030
```

Normal downloads and the dedicated sharing, transfer, chat, room, and
notification commands then use that daemon automatically. `--remote` overrides
the configured URL for one invocation:

```bash
sockseek "Artist - Title" --remote http://127.0.0.1:5030
```

Most download options can be used in the same command:

```bash
sockseek "Artist - Album" \
  --remote http://127.0.0.1:5030 \
  --album \
  --format flac
```

The daemon performs the work. Closing the remote CLI does not stop the daemon.

### Planning and `--print jobs`

Direct Start consumes the shared Core planner without creating a preview.
Local `--print jobs` and `--print jobs-full` run that planner in-process and do
not require a daemon, database, or configured daemon data directory. In remote
mode those two print forms are Review operations: the CLI creates an expiring
Job Preview, exhausts its cursor-paged nodes, prints them, and does not create a
runtime workflow.

When a remote command names a client-local CSV or explicit List file, the CLI
streams it to the daemon as an immutable input artifact before either Review or
direct Start. The daemon therefore never assumes that a client filesystem path
exists on the server, and CSV/List source-removal behavior never mutates the
client-owned file.

### Search results in local and remote CLI mode

The local and remote CLI use the same Core search-definition, admission,
ranking, grouping, condition-fact, and counter logic. Local mode runs that
kernel directly and needs no daemon or database. Remote mode reads the daemon's
disk-backed Search View pages, so it can display the same projection while
results are arriving and after the workflow leaves memory.

Print-only Song, Album and aggregate jobs retain locked observations for display,
including condition explanations in full results. CLI text marks locked files;
JSON emits `Visibility: "Locked"` on those files. Automatic/manual download
candidates and printed download links use public files only. Printing reuses the
projection's evaluated facts rather than evaluating conditions again.

Legacy user-success downranking remains scoped to the active workflow. Local
and daemon projection receive the same workflow-local counts, and a published
Search View retains the resulting sort facts rather than creating a durable or
shared reputation system. Its longer-lived daemon semantics remain a V4 design
question.

A Search View advances through immutable revisions. Each revision contains
consistent rows, ordering, groups, explanations, and summary counters for all
observations consumed so far. The UI and remote CLI observe the View through the
`ObserveSearchView` SignalR stream. While observed, it catches up in fair batches
independently of display refreshes. The stream delivers the first available
summary immediately, then coalesces updates to at most once per second; completion
is immediate. Clients
refetch visible pages and expanded groups at the received revision.
Closing or hiding the results cancels observation; unobserved arrivals remain
raw. Reopening resumes from the published prefix, including after restart.
Creation and ordinary summary/update reads still request one bounded batch.
Idle working kernels can be released without deleting published revisions.
Jobs listings must not request projections merely to obtain aggregate group counts.

Displayed Jobs lists use `ObserveJobList` with a query and a fetched extent (100
roots by default). The first frame replaces that scope; subsequent frames contain
changed fields and actual membership removals. Increasing the extent loads more
rows. Reconnect starts a fresh authoritative scope, even when retained revisions
are lower than the previous runtime revisions. Persistence retirement does not
remove a displayed job. `/api/jobs/snapshot` supplies the same initial frame over
HTTP; connected clients need only the observation. Ordinary `/api/jobs` cursor
paging remains available for traversal without a live display.

Retrieving a directory uses the exact view-issued peer-directory ref and the
same Core folder-retrieval job for generic and album views. Completion adds a
new immutable revision with browse-authoritative totals and pageable children;
the user can select the directory as one unit or select specific public child
files without server-owned checkbox state.

## Monitor daemon work

Use `--monitor` to follow every active workflow in the normal live renderer.
Input is optional; when supplied, its workflow appears alongside other daemon
work:

```bash
sockseek --remote http://127.0.0.1:5030 --monitor
sockseek "Artist - Title" --remote http://127.0.0.1:5030 --monitor
```

The normal `c`, `t`, and `i` shortcuts work daemon-wide by display ID. Choosing
all from the cancel prompt cancels active jobs without stopping the daemon.

## Sharing and transfer commands

Sharing, scans, and uploads belong to the running daemon. These commands require
a remote URL from configuration or `--remote`; they never start a temporary
local daemon:

```bash
sockseek share status --remote http://127.0.0.1:5030
sockseek share scan --remote http://127.0.0.1:5030
sockseek share scan --cancel --remote http://127.0.0.1:5030
sockseek transfers --direction upload --remote http://127.0.0.1:5030
sockseek transfer cancel <id> --remote http://127.0.0.1:5030
```

`share status` reports the sharing health, published generation, public aliases,
aggregate catalog counts, recent scan state, and blocked-peer counts
without exposing local roots or blacklist contents. `transfers` pages the same
newest-first combined timeline used by the WebUI: active downloads/uploads,
queued uploads, and retained history with live state overlaid by transfer ID.
Its JSON envelope reports retained coverage explicitly when persistence is
disabled or degraded. Use `--limit`, `--cursor`, `--state`, `--username`, and
`--direction` to narrow it. New transfers may appear above an existing cursor;
status changes do not reorder the traversal. Add `--json` for typed output.
Scan and individual transfer cancellation use the same advertised actions and
HTTP contracts as other API clients. The API also provides direction/state
scoped bulk cancellation and reversible terminal-history archive as distinct
operator mutations with bounded outcome receipts.

The Dashboard analytics API uses the same transfer persistence lifecycle. It
checkpoints cumulative bytes per attempt and stores compact five-minute base
buckets, so retries, resumes, and transfers crossing a selected range are not
reconstructed from file size or creation time. Dashboard ranges are bounded and
report a contiguous `completeFromUtc` coverage boundary. A new installation,
retention, an unclean restart, or unhealthy persistence may therefore make an
older range explicitly partial or unavailable; transfers continue independently.
Content rankings use the public shared-directory identity and display path
captured when an upload is admitted, never the configured local root.

Remote-user share acquisitions are also immutable, expiring SQLite artifacts
under the daemon data directory. The same artifact owns ordinary directory/file
navigation, exact download selection, and global share filtering. Global
filtering uses an artifact-local trigram index with an exact case-insensitive
substring post-filter; one- and two-character queries use the same semantics via
a direct artifact scan. It returns bounded flat pages with display-path ancestor
context and exact public/locked totals rather than recursively embedding a tree
or issuing one file query per directory. Broad-query temporary work spills to
disk. A cursor is valid only for its browse generation, revision, and query.
Older retained artifacts without the index remain ordinarily browsable and can
be refreshed to enable global filtering.

### Configure sharing and uploads

Sharing is off until at least one root is configured. A root may use its final
directory name as the public alias, or specify an alias explicitly:

```ini
share = /srv/music
share = + [Archive]/mnt/archive/audio
share-exclude = + /srv/music/private
share-filter = + \.(part|tmp)$

share-scan-on-start = true
# share-rescan-interval = 6h

upload-slots = 10
# upload-speed-limit-kib = 2048

upload-blocked-user = + unwanted-user
upload-blocked-ip = + 192.0.2.10
private-message-blocked-user = + noisy-user
```

The leading `+` appends another value; an unprefixed list value replaces values
from an earlier configuration layer. Upload blocks deny future inbound search,
browse, directory, and upload requests. Private-message blocks discard future
incoming DMs only; room messages and all outbound profile, browse, download, and
chat actions remain available. Public aliases and relative remote paths are
visible to peers. Local roots and the contents of restriction lists are not
returned by ordinary status APIs.

Scans skip Windows `Hidden` or `System` entries, Unix dotfiles and
dot-directories, and every symbolic link or reparse point. A hidden directory's
entire subtree is skipped. Attribute or entry-read failures skip that entry;
they do not invalidate the filesystem. Zero-byte files are indexed and served.
This hidden-file policy is fixed in v4 rather than configurable. A failed or
cancelled rescan leaves the previous complete generation active.

The initial scan runs in the background: daemon HTTP and ordinary download work
start without waiting for a large library. Until the first generation publishes,
sharing status reports the catalog unavailable and Sockseek advertises no
shares. When a prior valid generation exists, it remains searchable, browsable,
and upload-resolvable while the replacement is built.

The catalog and browse artifact live under the daemon data directory, separately
from durable history. A disk-full or write failure removes the incomplete
staging generation and leaves the previously published generation active.
Sockseek retains the current and rollback generations; an older generation is
deleted only after its outstanding request streams release it.
The dedicated sharing directory and its manifest, SQLite generations, and browse
artifacts are restricted to the daemon account (protected owner-only ACLs on
Windows and `0700`/`0600` modes on Unix).

### Sharing filesystem behavior

Sockseek does not maintain a filesystem allowlist. A configured root is eligible
when the daemon account can enumerate and open it through .NET, including common
NAS mounts, ZFS/Btrfs, Docker bind mounts, and FUSE-backed storage. A volume root
such as `/` or `C:\` requires an explicit public alias. Local roots may overlap
when their aliases are distinct.

Peer paths are resolved only through the published catalog, checked for canonical
containment, and never concatenated into an unchecked filesystem probe. Symbolic
links and reparse points are excluded. Immediately before an upload, Sockseek
opens the file read-only and validates its current size and modification time;
native stable-file identity is optional hardening rather than a platform gate.
Individual inaccessible or changed files fail without disabling the whole root.

The catalog database itself should still live on reliable local storage under
the daemon data directory. That operational recommendation is independent of
where shared media is mounted.

For application integration, see [API and client integration](api.md) and the
generated [OpenAPI document](openapi.json).

## Chatrooms, private messages, and notifications

Chat is a daemon feature because it needs one long-running Soulseek connection
and durable SQLite storage. It is available whenever daemon persistence is
enabled and started. A temporary foreground download process does not receive
messages or join rooms.

Configure rooms to join after login or reconnect with the normal list syntax:

```ini
chat-room = indie
chat-room = + electronic
private-message-retention-days = forever
room-message-retention-days = 30
```

An unprefixed `chat-room` replaces earlier configured values; `+ ` appends.
Rooms joined through the API are remembered separately unless `--no-remember`
is used. Leaving a configured room lasts for the current connection; the daemon
will request it again after its next login. Private messages are retained
forever by default; room messages default to 30 days. Each policy can be set to
a positive day count or `forever` independently.

The remote CLI is intentionally scriptable rather than a second interactive
chat UI. The examples show explicit `--remote`, but it may be omitted when
`remote` is configured:

```bash
sockseek chat status --remote http://127.0.0.1:5030
sockseek chat conversations --unread --remote http://127.0.0.1:5030
sockseek chat messages alice --remote http://127.0.0.1:5030
sockseek chat send alice "hello" --remote http://127.0.0.1:5030
sockseek chat read alice --remote http://127.0.0.1:5030
sockseek chat archive alice --remote http://127.0.0.1:5030

sockseek room available --remote http://127.0.0.1:5030
sockseek room joined --remote http://127.0.0.1:5030
sockseek room join indie --remote http://127.0.0.1:5030
sockseek room messages indie --remote http://127.0.0.1:5030
sockseek room send indie "hello room" --remote http://127.0.0.1:5030
sockseek room members indie --remote http://127.0.0.1:5030
sockseek room member add secret-room friend --remote http://127.0.0.1:5030
sockseek room leave indie --remote http://127.0.0.1:5030

sockseek notifications --unread --remote http://127.0.0.1:5030
sockseek notification read all --remote http://127.0.0.1:5030
```

Add `--json` for typed JSON and `--limit 1..200` for bounded list commands.
Private-room membership and moderation reported by Soulseek are reflected in
room metadata, including `Owned` and `Moderated` on joined-room summaries.
Sockseek accepts private-room invitations, can join accessible private rooms,
and exposes `room member add`; it does not expose private-room
creation, member removal, moderator changes, ownership transfer, or membership
drop.

Available-room responses include `Truncated` when the bounded server directory
could not retain every reported room. Joined-room summaries expose
`RosterComplete`, and member pages expose `Complete`; a GUI should show an
incomplete-state hint instead of treating a deliberately bounded or interrupted
roster as authoritative.

Incoming private messages are committed before Sockseek acknowledges them to
the Soulseek server. A replay is deduplicated. Protocol acknowledgement is not
the same as the local read watermark: clients explicitly mark visible messages
or notifications read. Room mentions match the current username as a whole
token. Private-message-blocked usernames have incoming DMs acknowledged and
discarded; they still appear in room chat and may receive outgoing DMs. Upload-
access username/IP restrictions are unrelated to chat, and server-delivered chat
events do not contain a peer IP address.

Messages are plain text and are never interpreted as HTML or Markdown by the
daemon. Message bodies, conversation lists, and room rosters are absent from
the compact daemon snapshot. New notification records and unread summaries are
published on the daemon live stream, while open conversations and rooms use
their own recoverable live scopes. This lets a GUI show a notification bubble
immediately without broadcasting every busy-room message to every client. A
notification carries actor/target metadata and a bounded one-line plain-text
preview, not a second copy of the full message body. History deletion and
retention replace an open target's bounded live tail so a GUI does not retain
messages that are no longer durable.

Live publication is bounded independently per daemon, conversation, and room
scope. If a busy room outpaces SignalR delivery, Sockseek keeps recent state and
creates a sequence gap; supported clients automatically recover that room from
an HTTP snapshot. Other rooms and the notification badge are not held behind
the stalled scope.

## Network security

The daemon API does not currently provide authentication.

The default `127.0.0.1` address accepts connections only from the same computer.
Binding to `0.0.0.0`, `::`, or another network address makes the API reachable
from that network. Only do this on a trusted network or when access is protected
by a VPN, firewall, or authenticated reverse proxy.

Anyone who can reach the API can use its current pass-through operator trust
domain: they may view history, submit work, start/cancel share scans, and cancel
eligible jobs or uploads using the daemon's configured credentials. The
`Sockseek.Operator` endpoint marker is an integration seam for the planned v4
authentication work; it is not access control today.

## History and restarts

Daemon mode retains jobs, workflows, search results, downloads, and download
attempts. This makes it possible to inspect earlier activity or start a new
download from a retained search result after restarting Sockseek.

Historical jobs are not resumed automatically. Starting a download from an old
search result creates a new job. Commands such as cancel or manual selection
only apply to work that is currently running.

Stop the daemon with `Ctrl+C` when possible. Sockseek then finishes saving
pending history before exiting. After a crash or forced shutdown, the most
recent history may be incomplete and unfinished work is shown as interrupted.

Search history may be labelled:

- `Complete` when all results were saved;
- `Incomplete` when some results could not be retained;
- `Pruned` when old raw results were removed by retention;
- `Interrupted` when the daemon stopped before the search finished.

## Database and privacy

Sockseek stores daemon history in `sockseek.db` inside its data directory. The
default data directory depends on the operating system:

| System | Default data directory |
| --- | --- |
| Linux | `${XDG_DATA_HOME:-$HOME/.local/share}/sockseek` |
| Windows | `%LOCALAPPDATA%\sockseek` |
| macOS | `~/Library/Application Support/sockseek` |

Set a different directory with:

```bash
sockseek daemon --data-dir /path/to/sockseek-data
```

The setting can also be kept in `sockseek.conf`:

```ini
data-dir = /path/to/sockseek-data
```

Sockseek chooses the database filename inside that directory. You do not need to
configure a database file separately. For a container, mount a persistent
directory and set `data-dir` to that mount, such as `/data`.

Durable submission history, Search View revisions, transfer accounting,
peer-restriction overrides, and uploaded-input metadata share this database,
including its migrations, integrity checks, backups, and retention runs.
Uploaded input bodies remain immutable files beside it, and Job Preview uses a
temporary per-daemon spool that is deliberately not restored after restart.
Share catalogs and completed peer-browse databases are immutable generation
artifacts rather than parallel history databases.

Keep the database on a local disk. Network shares and cloud-synchronized folders
are not supported database locations.

The database and its backups may contain:

- peer usernames;
- search queries;
- remote and local file paths;
- job and transfer outcomes;
- error messages.
- private and room messages, read state, room subscriptions, and notifications.

Treat these files as private download history. On Windows, custom paths inherit
their folder permissions. On Unix, Sockseek restricts newly created database and
backup files to the current user.

Only one daemon or database command can use a database at a time.

The unreleased v4 branch uses a fresh schema baseline. Databases from earlier v4
development builds are rejected without being replaced. Keep the old database
with a compatible build, or explicitly choose a fresh database path.

## Retention

Sockseek periodically removes old history so the database does not grow forever.
The defaults are:

| History | Retained for |
| --- | ---: |
| Successful jobs | 90 days |
| Failed, cancelled, or interrupted jobs | 180 days |
| Search results | 30 days |
| Transfers | 90 days |
| Private messages | Forever |
| Chatroom messages | 30 days |

The daemon also keeps at most 100,000 completed jobs by default. Active work is
never removed.

Each type of history can be configured independently:

```ini
successful-job-retention-days = 90
unsuccessful-job-retention-days = 180
transfer-retention-days = 90
search-result-retention-days = 30
private-message-retention-days = forever
room-message-retention-days = 30
```

Use `--no-retention` to disable automatic cleanup. Retention periods also accept
`forever`, for example:

```bash
sockseek daemon --search-result-retention-days forever
```

Setting either job retention period to `forever` also removes the default
100,000-job limit.

Retention does not delete backup files.

Private messages are retained forever by default because they are durable user
conversations. Higher-volume chatroom messages default to 30 days. The two
settings are independent and each accepts a positive day count or `forever`.
They are age thresholds, not message-count ceilings. Scheduled or manual
maintenance deletes the oldest eligible messages in bounded batches.
Notifications whose source messages expire are deleted with them; conversation
and room resources remain, their unread and last-message state is repaired, and
connected clients receive replacement live tails. The maintenance API reports
the combined number removed as `PrunedChatMessages`.

## Backup and restore

Do not copy the live database file directly while the daemon is running. Use
Sockseek's backup command or maintenance API so the backup is complete and
verified.

For offline maintenance, stop the daemon first.

Create a backup:

```bash
sockseek database backup --backup /safe/location/sockseek-backup.db
```

Check a database:

```bash
sockseek database integrity
```

Restore a backup:

```bash
sockseek database restore --backup /safe/location/sockseek-backup.db
```

These commands read `data-dir` from the normal Sockseek config. Use
`--data-dir /path/to/sockseek-data` to operate on another data directory, or
`--config /path/to/sockseek.conf` to select a particular configuration.

Sockseek verifies backups and restored databases before reporting success. Keep
at least one backup outside the data directory and test restoring it
occasionally.

Database upgrades are normally applied when the daemon starts. To apply them
while the daemon is stopped:

```bash
sockseek database migrate
```

## Status and troubleshooting

Daemon status, including database health, is available from:

```text
GET /api/server/status
```

Common problems:

- **The address or port is already in use:** stop the other service or select a
  different `--server-port`.
- **The database is already in use:** stop the other daemon or maintenance
  command that owns the same database.
- **Remote commands cannot connect:** confirm the daemon address, port, firewall,
  and configured or `--remote` URL.
- **History reports a database error:** active downloads can continue, but new
  history may not be saved. Check the daemon log and run an integrity check after
  stopping the daemon.
- **Startup reports an integrity or upgrade error:** do not delete the database.
  Make a copy, run the offline integrity command, and restore a known-good backup
  if necessary.
