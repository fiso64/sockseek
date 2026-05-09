## TODO

- Add {outputdir} and {configdir} variable. {configdir} resolves to the parent of the currently used config file location.
    - Should work everywhere (input/config paths resolution, on-complete, name-format)

- (breaking) Replace the --failed-album-path option by a new option called --album-fail-action. Can be
    - "" or "default" - move all album files to {configured output dir}/failed when not in interactive mode. In interactive mode, ask what to do, with the same default action.
    - "move:{path, with possible {} variables}" - move to specified path. 
    - "delete" - delete the downloaded files
    - "keep" - do nothing, keep files where they are
    - "ask" - Ask what to do: Can be delete, keep, move, or retry. If move is selected ask for the path in a second prompt. Retry will reattempt to download the incomplete files.

- (breaking) Make album download mode the default, add -s/--song flag. Don't forget to update it for lists as well (add s: prefix). Also explain that the previous default behavior (default to song search, album with -a) can be restored by adding `song = true` to the config (ensure this works).

- Skip retrieve full folder contents whenever it's already guaranteed to contain all files (e.g. when it was `cd`'d into).

- Introduce a new state `pending search` (set unconditionally before waiting on the search rate & concurrency semaphores). `searching` state should only be set while actually searching.  

- Logging is scattered & inconsistent. Centralize and make it more defined.
    - Consider storing errors on the job objects and DTOs
    - or even full per-job logs?

- Implement sharing service. Look how slskd does it for a start.

- (breaking) Maybe use yaml for settings instead of our custom format, and improve structure.