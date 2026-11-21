# Sockseek 4.0 QA findings for review

**20 November**, `e8e155ce`.

## Resolved decisions

### F12 · When two albums have identical track lengths, how can a listener still pick both?

In Album Groups, the listener checks one row per album and can choose which peer supplies it. Folders with matching track lengths normally appear as alternatives on the same row. In QA, *North Yard* and *Paper Lanterns* both contained seven-second previews. Their track lengths matched, so selecting one album replaced the other.

**Decision.** When every track in either folder has the same reported duration, compare a representative filename from each folder too. This lets the listener pick both releases. Ordinary albums still group by track lengths.

### D01 · If I pick just one file, should Sockseek keep the peer’s folder name? What if another file lands at the same path?

A Song job normally saves a single filename, and an Album job keeps the folder tree. Picking individual tracks in File Search or Shares falls between those cases. Even keeping the immediate folder can leave two different performances from two peers headed for the same path, such as `Disc 1/01 Opening.wav`.

**Decision.** With a blank `name-format`, files picked in Search or Shares keep their immediate folder and filename. The first performance lands at `Disc 1/01 Opening.wav`. Different contents get `01 Opening (2).wav` in that folder, and identical contents reuse the existing file. Selecting a whole folder keeps its tree.

### D03 · If I already own a song or album, how can I tell? What happens if I select it anyway?

Automatic jobs already had **Skip existing**. They could recognize music through the completion index or a library configured with `--skip-music-dir` and skip downloading it again. Song Groups and Album Groups offered no such cue in the WebUI. When choosing what to download, you couldn’t tell which results you already owned. Simply skipping those results would also get in the way if you wanted a particular peer’s copy.

**Decision.** Use the configured matching rules to show **In library** when the local file or album folder exists. You can leave that group unchecked. If you own the CD but select a peer’s vinyl pressing and press **Download**, Sockseek requests it from the chosen peer even with **Skip existing** on.

### D04 · What should an automatic job do when a peer offers several editions of the requested recording?

Sockseek uses a release link to search for a recording on Soulseek. In QA, an original-CD link downloaded the remaster even though the peer shared both pressings. A request for a clean track likewise downloaded the explicit version. Both files passed the job’s checks, but the ranking missed the edition the listener asked for.

**Decision.** Automatic jobs should pick the best available match without prompting. Improving edition ranking is still open. With the incomplete-album action set to **Keep**, a retry keeps completed tracks, so an album can also end up containing tracks from different pressings.

### D05 · When a filter hides some files, what does checking their folder select?

In Shares, filtering a folder for FLAC left one recording visible. Checking the folder showed **Download 3** and fetched the hidden MP3 and PDF as well. A listener who wanted only the visible matches had to check individual files. File Search has the same choice between matching files and the whole folder.

**Decision.** The first click selects the visible FLAC and shows `[-]`. The second turns the checkbox blue and selects all three. The third clears it. If every downloadable file matches, the usual two-click toggle applies.

In File Search, selecting the whole folder covers the files already returned in search results. **Load all files in this folder** asks the peer for the rest.

### D06 · If a transfer breaks halfway through, when is it safe to reuse the partial file?

During a Soulseek download, Sockseek normally writes to a temporary `.incomplete` file. After a connection drops, that file may already contain half the track. Resuming saves a second download of those bytes, but the peer could have replaced the file at the same path.

**Decision.** On an automatic retry from the same peer, resume if the reported size is unchanged. Start over if it changed. A same-size replacement can therefore leave the listener with parts of two versions.

### D07 · How long should a queued download wait, and what gets another try after a restart or a failure?

In QA, a peer stayed online but its only upload slot was busy. A song the listener picked in Shares failed as stale after 30 seconds at zero bytes. In a separate restart check, the listener recovered missing files by selecting them again. An automatic job can try another peer, but someone who picked a particular copy needs a way to wait for it or retry it.

**Decision.** Automated jobs release a stale transfer at `max-stale-time` so they can try another candidate. Moving from position 500 to 499 does not extend the wait. After a daemon restart, Sockseek requeues interrupted downloads on login. Earlier failures stay failed until the listener chooses **Retry** in Downloads or on the file’s job.

For a failed album track, **Retry** uses its original peer and album destination. **Retry missing files** finishes the original folder selection and keeps completed tracks. **Run again** repeats the automatic job being viewed, whether that is one album or the whole batch.

**Deferred.** A file picked in Search or Shares can keep waiting while the peer confirms it is queued. Whether to cap that wait remains open.

### D08 · If I edit a CSV and reuse its playlist, should the playlist follow the new CSV order or keep my edits?

With **Write playlist** enabled, a CSV import creates an M3U that the listener can edit in a player. On a second import, the CSV and the playlist may disagree. A song missing from the revised CSV could still be in the playlist because the listener added it by hand.

**Decision.** Keep existing playlist entries in order and append missing ones. If the M3U plays A, B, C and the revised CSV lists C, A, D, reusing the M3U gives A, B, C, D. A fresh M3U gives C, A, D.

### D09 · If Sockseek renames album tracks, should it also rewrite the publisher’s playlist or cue sheet?

In QA, the publisher’s M3U played three tracks in a deliberate order and repeated the station ident. Its CUE sheet also referred to the original filenames. A custom `name-format` renamed the audio and left both files pointing at names that no longer existed. Sockseek’s own generated playlist still played, but it used album order and played each track once.

**Decision.** Preserve the publisher’s files as supplied, even if renaming breaks a reference. Playlists that Sockseek generates use the final paths.

### D10 · If a song downloads but my converter fails, what happens to its history and its row in the source CSV?

In QA, a FLAC downloaded and the `on-complete` converter failed. Sockseek still recorded the FLAC as successful and removed its CSV row under `--remove-from-source`. The listener had the source audio, but the player copy was missing and the work queue no longer showed anything to do.

**Decision.** The FLAC stays indexed as successful, and `--remove-from-source` removes its row. Only an `on-complete` command using `update-index` and exiting with code 0 can change the recorded outcome or path. A script that needs to keep the row for retry can report `failed` and exit 0.

### D11 · If I download a folder with three songs, how often does my completion command run?

A successful three-song folder produces four completion events, one per file and one for the folder. An `on-complete` rule using `when=success` alone matches all four. That makes sense for some scripts, but a listener asking for an “album ready” notification only wants it once.

**Decision.** Use `scope=file` for individual files, including songs and files picked from Shares. Use `scope=folder` to run once for the requested album or folder. With no scope, a successful three-song folder runs the command four times. An auto-profile can limit it to music download modes.

### D12 · Which older databases must a v4 upgrade preserve?

The database layout changed during v4 development. Someone testing an early build could already have job history they wanted to keep. The upgrade promise needed a starting point so operators would know which databases a later release could open.

**Decision.** Start fresh with the first published v4 schema. Later migrations must preserve data from that baseline. Unpublished previews have no migration or export requirement.

### D13 · What does the database backup cover, and what else would I need to restore my server?

Restoring the daemon’s database brings back records of past jobs. A restored CSV import can therefore appear in Jobs even if its uploaded CSV and the music it downloaded are absent from the new machine. Those files live outside the database.

**Decision.** Use the daemon’s backup endpoint while Sockseek is running, or `sockseek database backup` offline, to capture job and transfer history. Copying the live database file alone can miss recent writes. Back up inputs and music separately if you want to restore them. Browse the peer again to rebuild its cached Shares list.

### D14 · If I open one song in a four-song import, what history should I be able to delete from there?

A four-song import has a top-level Jobs entry and a detail page for each song. A Delete button on a song page could look like it removes just that song while actually clearing the whole import.

**Decision.** Offer one-click deletion of a finished submission’s job history only on its top-level entry. Downloaded files and transfer history are kept.

### D15 · After an automatic job finishes, should I be able to inspect every file the search found?

In QA, an automatic job tried 99 failing sources before the hundredth succeeded. While it ran, all 100 candidates were available to inspect. After completion, the chosen file and transfer attempts were still in history, but reopening the full candidate list returned an error. That left a question about what history should retain, especially for jobs that succeed on the first try.

**Decision.** Keep the selected file and the transfers actually attempted in history. Let untried offers expire when the job finishes. Saving all 100 after every search would grow the database over time.

### D16 · If my music drive is missing when the daemon starts, who makes sure downloads wait for it?

Sockseek’s output directory (`--output-dir`) is a path. On Linux, `/mnt/music` can still exist as a writable directory when the music drive is unplugged. A download to that path can succeed and fill the system disk instead.

**Decision.** Sockseek writes to the configured path. An operator who wants the daemon to wait for the drive can set `RequiresMountsFor=/mnt/music` in the systemd service’s `[Unit]` section.

### D17 · If my shared library contains a link to another directory, will peers see that directory too?

An operator could configure `share = /srv/music` and see the scan reject the library because that path was a symlink to another drive. Following every link would solve that case but could also publish a private directory linked from inside the library.

**Decision.** Follow a symlink the operator explicitly configures as a share root. Skip links to files or directories found inside that root.

### D18 · What must happen on disk before Downloads can show a file as Completed?

QA found a download marked **Completed** while its only file still had a `.incomplete` name. Receiving the last byte left two chances for failure. Sockseek still had to close the file and move it to the path the listener expected to open.

**Decision.** Report **Completed** once the file closes and reaches its final path. A write, close or move error fails the download. The OS handles the final write to disk, so sudden power loss can still affect a file shown as complete.

### D19 · If I leave New job after choosing songs, when should I have to review them again?

The checkboxes in **New job** belong to a draft until the listener presses **Start**. If you left Review to look up one recording in Shares and lost the draft, Jobs had no submitted job to reopen. You had to review the playlist and pick the songs again.

**Decision.** Keep the draft and its checked songs when the listener returns in the same tab. Reloading or signing out clears the draft.

### D20 · Should an open tab switch theme or search mode as soon as the operator changes the default?

Changing `web-theme` or `web-default-search-mode` on the daemon left an already-open tab showing its previous settings. Reloading picked up the change. The choice was whether to push it immediately, even to someone halfway through entering a search.

**Decision.** The open tab keeps its current theme and search mode. It picks up the changed defaults on reload.

### D21 · If a new message arrives while I’m reading older ones, when should the unread indicator clear?

In QA, a reader scrolled back to older messages and switched to another browser tab. Three messages arrived. The hidden Chat tab marked all three as read and cleared their notifications, even though the reader had seen none of them.

**Decision.** Keep the reader’s place. Clear the unread indicator once the latest message is visible in the active conversation.

### D22 · If I delete a chat, should Sockseek remember which messages it has already received?

In QA, a listener read two messages and deleted the chat. The Soulseek server had missed the acknowledgement for the first message. On reconnect, that deleted message appeared as unread alongside a genuinely new one. Suppressing the repeat would require remembering its delivery ID after deletion.

**Decision.** Deleting a chat also deletes its delivery IDs. If the server sends a deleted message again, it can reappear.

### D23 · If I paste three lines into chat, how many messages will I send?

The chat composer sends one message at a time. Pasting an artist and a list of songs raises a separate choice about line breaks. The recipient could get one multiline message, several separate messages, or a single line.

**Decision.** Replace pasted line breaks with spaces. The sender sees one message and sends it by pressing **Send**.

### D24 · What should Sockseek open when a link identifies a single Spotify track or a YouTube video within a playlist?

In **New job → Spotify**, pasting a track link into the playlist/album field made Review look up a playlist with the track’s ID. The listener saw “playlist not found” even though the track existed. YouTube has a different ambiguity. A watch link copied from a playlist names both the video and the list.

**Decision.** Reject a Spotify track URL before contacting Spotify. Explain that albums and playlists are supported and that a single track can be requested by artist and title in **New job → Song**. Open the whole YouTube playlist for review when a watch link also identifies a playlist.

### D25 · If I filter for M4A, am I choosing a filename extension or a particular audio codec?

Song and Album searches use `--format` to require a format and `--pref-format` to rank matching files higher. AAC and Apple Lossless recordings can both be named `song.m4a`. A listener looking for lossless audio cannot tell which encoding a peer used from the filename alone.

**Decision.** Match the advertised extension, so an M4A filter includes both recordings. A separate codec filter can wait for a concrete request.

### D26 · Does a search limit of one mean one reply from a peer or one matching file?

A listener can set `response-limit` to one and still see two recordings. A single peer can return a studio take and a live take in the same reply. The setting’s name makes sense at the network level, but the results screen shows files.

**Decision.** Count peer replies per search attempt. With `response-limit` set to one, both recordings in that reply still appear.

### D27 · If a busy server misses a file it shares, is that expected above a certain library size?

When someone else searches Soulseek, the daemon checks its own shared catalogue for files to offer. In the 100,000-file catalogue check, it stopped returning a known file under sustained load and found it again when requests slowed down. Measuring response time alone would miss that failure.

**Decision.** Treat a missing known match as a bug. Track correct matches, searches handled and response times on that catalogue. The test size is a QA benchmark rather than a published homeserver limit.

### D28 · When I reopen a peer’s Shares, do I need to see when the list was last fetched?

In a live check, reopening a peer’s Shares showed the previously saved catalogue. A new three-file folder appeared only after **Refresh**. The list gave the listener no timestamp to judge how old it was.

**Decision.** Keep the saved list and the **Refresh** button, which fetches the current list. A “last fetched” timestamp can wait.

### D29 · What happens if a peer offers me a file I never asked for?

Normally a file arrives because you started a download in Sockseek. Soulseek also supports manual uploads started by the sender. For example, a friend can offer to send you a recording even though you never selected it in Search or Shares. Some clients let the recipient choose who can send files this way. [Nicotine+ release notes](https://nicotine-plus.org/NEWS)

**Decision.** Sockseek declines the offer automatically, without starting a download or asking the operator to accept it.

### D30 · If I select a file that is already queued, should that give it a new place in line?

Suppose **Downloads** shows `Opening.flac` as remotely queued. Sockseek has already asked Robin for the file, but Robin’s only upload slot is busy. You find the same file in Robin’s Shares and click **Download** again, hoping it will start sooner. Robin’s client controls which waiting transfer gets the next slot.

**Decision.** Selecting the file again leaves its place in Robin’s queue alone. Keep the ordinary queue controls.

### D31 · Where can I use Soulseek search operators such as `-live`? (formerly F09)

In QA, File Search for `Glass Ferry Crossing` found the studio recording and two live takes. Adding `-live` left only the live takes. The search box still showed the minus sign, but Sockseek sent `live` to the peer as a word to include. Applying the same syntax to Song and Album jobs raises a separate question because those jobs also use the text to match titles.

**Decision.** Keep `-term` and `*suffix` in File Search. How to interpret these operators in Song Search, Album Search and automated jobs remains open.

## Deferred decisions

### D02 · Should the same recordings always form the same Song Groups, regardless of which peer replies first?

Song Groups uses `--group-length-tol` to allow small differences between copies of a track. With the default three seconds, a 12-second copy matches both a 10-second and a 14-second copy, but those two do not match each other. The same search can therefore show a different number of groups depending on which peer replies first.

**Fine for now.** If the 12-second offer arrives first, all three join its group. If 10 and 14 arrive first, they form separate groups. Whether the result should be independent of reply order remains open. Any replacement should be easy to predict and fast enough to update as replies arrive.

## Straightforward bugs and fixes

### Release and data loss

- **F01 · Clean builds.** Building from a source archive or Dockerfile stopped because the checked-in CLI help disagreed with the manual. v4 fixed the mismatch. The worktree also keeps a Linux-specific package restore from changing the lock files used by portable builds.
- **F02 · Truncated replacements.** By default, Sockseek keeps the existing recording until its replacement has finished writing. With `--no-incomplete-ext`, the download writes directly to its destination. We accept that an interruption can cost the old copy or leave a partial file visible to a share scan.
- **F03 · Stale backups and unsafe restores.** Copying a freshly rotated backup could restore an older set of transfers. Restore also accepted the peer-browse database as a job-history backup. Backup now waits for pending writes and produces a standalone snapshot. Restore checks the database type and schema.
- **F04 · Wrong source row deleted.** With `--remove-from-source`, review a CSV, insert a new first row, then press Start. Finishing the song erased the new request. Removal now checks the reviewed record and preserves edited lists and quoted multiline fields.
- **F05 · Incomplete HTTP audio.** During HTTP fallback, one missing HLS segment produced a radio programme six seconds short, marked successful. Sockseek keeps yt-dlp’s default of skipping unavailable segments, so a download can still finish with a gap. A segmented retry starts fresh. An ordinary HTTP retry can resume the same file by byte offset.
- **F06 · Lost index and playlist updates.** A CLI import and a daemon import saved all six tracks but left only three entries in their shared completion index. Writers merge updates under a lock and replace the old file atomically, without forcing each rewrite to disk. A failed required output keeps its source request. Moving the index preserves absolute paths to files outside its folder.
- **F07 · Lost offline jobs.** Jobs could show a Song as Pending during a Soulseek login outage, then lose it on daemon restart. The daemon now saves the job with its receipt and accepts cancellation while waiting for login.

### Search, downloads and recovery

- **F08 · Lost selections.** Changing the destination in playlist Review rechecked songs the listener had removed. Comparing pressings in Album Groups expanded one checked track to a disc. In File Search, Shift-click across Load more skipped a folder. All three now preserve the selection.
- **F10 · First-time publishers.** A peer can report zero speed until its first completed upload. Automatic Song used to skip that peer even if it had the only matching file. It now considers the offer.
- **F11 · WebUI Track Search offered files that failed required filters.** With a 192 kbps minimum, the server correctly flagged a 93 kbps MP3 as below the minimum. The WebUI requested full results, so the listener could still select and download it. The WebUI now applies required filters to new, reopened and rerun searches, including jobs created with CLI full-results output.
- **F13 · False library matches.** With **Skip existing** enabled, finding a similarly named lyric sheet or a short promo in the local library could prevent the full song from downloading. Lookup now checks the media, artist, requested album and applicable duration conditions.
- **F14 · Stale completion history.** Retrying *Arrival* could add an old WAV to a new playlist because a failed index entry named that file. An artwork-only request could block a later request for the full album. Only successful, applicable index entries count. With default index-based skipping, turning on playlist output or adding a music library still trusts a successful entry after the file moves. With `--skip-check-cond`, the same album checks apply with or without a playlist.
- **F15 · Missing playlist entries.** With **Write playlist** enabled, downloading two folders from Shares could leave an empty M3U, and an album already in the library could be omitted. Generated playlists now use the final paths, include M4B audiobooks, preserve encoding and play numbered chapters in order.
- **F16 · Cancel stopped the wrong work.** In Downloads, **Cancel queued** missed songs waiting for a peer’s upload slot. Cancelling a folder stopped the active track but let the next one start. Cancellation now reaches the selected queue, folder or external transfer. Clearing completed transfers leaves failed entries visible in Downloads.
- **F17 · Stale tabs and duplicate downloads.** A phone returning to Shares could show the old destination after the operator changed drives. Losing the Download confirmation and pressing Retry could queue the same song twice. Reconnected tabs now refresh the destination and results, disable expired selections and recover the original job.
- **F18 · CLI replay and cancellation.** Reimporting a CSV could download everything again under a temporary name. Reconnecting could hang after success, and cancellation left an album prompt live. The CLI preserves the source name, reads the saved outcome and closes the cancelled prompt.
- **F19 · Wrong file or peer path.** Opening a copied Soulseek link for `A+B sessions` could download from `A B sessions`. On a case-sensitive share, `Live.bin` and `LIVE.bin` could also resolve to the same file. Links and lookups now preserve the publisher’s exact spelling, including accents.
- **F20 · Provider lists.** Importing a Bandcamp wishlist with a limit of one could select all four albums. SoundCloud and YouTube playlist Review silently skipped unavailable tracks. Review now respects the range and shows gaps such as “Track 2 unavailable.”
- **F21 · Unavailable drives blocked unrelated work.** One unreadable folder could fail an import, and a sleeping share or indexed album could stall another listener’s download. Failures now stay with the affected work. Saved upload IDs recover when their volume returns.
- **F22 · Transfer history disagreed with disk.** Downloads could say Completed while the player still held only a `.incomplete` file. Uploads could merge two sends or show a finished upload as active after restart. History now marks completion when the file reaches its final path and records each attempt, storage failure and partial folder count.

### Interface, reporting and performance

- **F23 · Phone controls and navigation.** On a narrow File Search page, **Deselect all** covered **Load more**, so a tap could clear a shortlist. The layout now separates the controls. Other fixes keep delayed results from pulling the user off Downloads, wait for Japanese text composition to finish and preserve the reader’s place on a rescan.
- **F24 · Text matching.** Typing `Cafe` hid `Café` in results and Shares. Pasting an artist and title separated by an en dash failed to split them. The inputs now handle both.
- **F25 · Chat and rooms.** Typing a second message while the first was sending let a late confirmation erase the new draft. Retry could send the first message twice. Each receipt now applies to its own message. Reconnect also restores conversations and private-room membership. Transcript dates and CLI JSON history were corrected.
- **F26 · Dashboard totals.** All-time totals dropped the first partial interval, external downloads counted zero bytes, and folder rankings split the same content. Totals now use the full range and correct byte counts.
- **F27 · Selection size.** When choosing files from Shares, the listener could see how many were checked but had to work out their total size. Shares now shows the combined size before Download.
- **F28 · Peer pictures.** After a peer changed their photo, the browser could keep showing its cached copy even after a profile refresh. Refreshing now shows the new photo.
- **F29 · Hosting under a URL prefix.** Serving the WebUI at `/music/` gave a blank page because assets used the wrong base path. The WebUI and manual now honor it.
- **F30 · CLI browsing and exit status.** Terminal Shares keeps the selected row visible, its filter opens, and a successful alternate album returns success.
- **F31 · Audio metadata and filenames.** When publishing Shares, missing RF64 or lossless M4A properties could hide a suitable recording from quality searches. A variable-rate MP3 could appear to meet the minimum bitrate on its first frame alone. Shares now publishes the available audio properties and omits a bitrate estimate when it detects inconsistent frame rates. The `{length}` filename field uses playback seconds, and generated names handle long titles and paths.
- **F32 · Unnecessary work on large libraries and slow links.** Hidden tabs streamed unused progress, rescans read embedded cover images, CSV cleanup rewrote the queue per song, and job details repeatedly queried the same search. The fixes reduce background traffic, skip artwork and batch or reuse expensive work. Large Shares filters, track sorts and phone Downloads lists also received targeted fixes.

[Workflow evidence](lab/COVERAGE.md) · [Performance findings](lab/LOAD-RECONCILIATION.md)
