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

### YAML
Maybe use yaml for settings instead of our custom format, and improve structure.