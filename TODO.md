## TODO

- Add {outputdir} variable

- Replace the --failed-album-path option by a new option called --album-fail-action. Can be
    - ""/"default" - move all album files to {configured output dir}/failed when not in interactive mode. In interactive mode, ask what to do, with the same default action.
    - "move:{path, with possible {} variables}" - move to specified path. 
    - "delete" - delete the downloaded files
    - "keep" - do nothing, keep files where they are
    - "ask" - Ask what to do: Can be delete, keep, move, or retry. If move is selected ask for the path in a second prompt. Retry will reattempt to download the incomplete files.

- Make album download mode the default, add -s/--song flag. Don't forget to update it for lists as well (add s: prefix). Also explain that the previous default behavior (default to song search, album with -a) can be restored by adding `song = true` to the config (ensure this works).

- Why do all active downloads always go stale after disconnecting and reconnecting?

- Improve reconnection logic (more than 3 attempts, increasing delay)

- Skip retrieve full folder contents whenever it's already guaranteed to contain all files (e.g. when it was `cd`'d into).

- In interactive mode, show search results (for albums or individual files) immediately as soon as they arrive instead of waiting for the search to complete. Sort every time before showing the updated results. Show a loading indicator while the search is in progress. When the user has e.g. some result selected, updates should be handled cleanly:
    - If a new result arrives that will be sorted before the currently selected result, set the selected result index to the minimal index of the new results after updating.
    - If all new results are to be sorted AFTER the currently selected result, there is no need to change the currently selected index. 
    E.g.: When the current selected index is 5 and a new result arrives: If after sorting the new result has index <= 5, set the selection to its index. If the new result has index > 5, keep the current selection index at 5. 

### CLI Rendering
Progress rendering is broken garbage. Try to use Terminal.Gui in a future PR instead. 

- Leave --no-progress and json progress modes as they are
- In normal progress mode, replace everything by a full TUI using terminal.gui
- Old rendering logic should remain available with --legacy-rendering, but the code should be isolated.

Console rendering will be completely redone.

Design:
- Main list of jobs. Each job entry has two text lines, one of which includes a progress bar (and/or percentage).
- Each entry is clickable. When clicked, create a right side panel and show job details.
    - For song jobs, show useful info like peer username and speed, file properties including full filename (the name in the job list might be truncated), etc.
    - For album jobs, the details show album folder info and individual track progress (similar to how it is rendered now without --album-compact-progress)
- For interactive album downloads when interaction is required, create and navigate to a new tab where the choices (results) are listed. For now, this can remain similar to how interactive mode is currently rendered: One result at a time, prev/next navigation, some shortcuts. Accepting the result closes the tab (if possible, switch to the next interactive tab on close if it exists).
- Logs can also be printed to the main job list if possible (single line). Long logs (like errors) should still be printed as single lines, but clickable and show the full message in the details pane when clicked.
    - If this is not possible, we can also add a bottom log pane (though that would make the UI cluttered) or a dedicated log tab.
- Cancellation prompt becomes an actual prompt box.
- Esc, q should show a prompt asking to cancel all jobs (like `c` -> `all`).
- Status bar which shows the `c     cancel job` shortcut (`c` is clickable if possible) (Esc/q is self-explanatory) and overall progress.
    - Need to think how overall progress should be determined, as we don't always know things ahead of time. Maybe dynamic completed/total counts for every job type (extract, song, album). So it would start with 0/1 extract jobs -> 1/1 extract jobs and 0/Total song jobs and 0/Total album jobs based on what was extracted, or if the extract job produces more extract jobs, -> 1/Total extract jobs.

### YAML
Maybe use yaml for settings instead of our custom format, and improve structure.