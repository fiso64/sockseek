## TODO

### 3.0

- Add {outputdir} and {configdir} variable. {configdir} resolves to the parent of the currently used config file location.
    - Should work everywhere (input/config paths resolution, on-complete, name-format)

- (breaking) Replace the --failed-album-path option by a new option called --album-fail-action. Can be
    - "" or "default" - move all album files to {configured output dir}/failed when not in interactive mode. In interactive mode, ask what to do, with the same default action.
    - "move:{path, with possible {} variables}" - move to specified path. 
    - "delete" - delete the downloaded files
    - "keep" - do nothing, keep files where they are
    - "ask" - Ask what to do: Can be delete, keep, move, or retry. If move is selected ask for the path in a second prompt. Retry will reattempt to download the incomplete files. 
    - Need to think how to implement this cleanly in API.

- (breaking) Make album download mode the default, add -s/--song flag. Don't forget to update it for lists as well (add s: prefix). Also explain that the previous default behavior (default to song search, album with -a) can be restored by adding `song = true` to the config (ensure this works).

- Skip retrieve full folder contents whenever it's already guaranteed to contain all files (e.g. when it was `cd`'d into).

- Introduce a new state `pending search` (set unconditionally before waiting on the search rate & concurrency semaphores). `searching` state should only be set while actually searching.  
    - At the same time, handle the todo starting with `TODO: Split this into lifecycle state`.

- Do the `TODO [ARCHITECTURE]: Standardize on the Result/Outcome pattern across all job processors.`.

### Later

- Add `q` to quit. When any jobs are running or pending, prompt if should cancel [Y/n/Esc]. In local mode, n=Esc="do not cancel, keep running". In remote mode, n="exit without cancelling workflow remotely" and Esc="cancel prompt, keep running" (the prompt should be different depending on if local or remote mode for clarity).

- Add a `--idle-when-done` (or similar) option that will make it idle instead of exiting at the end. 

- Should be possible to connect to daemon without creating a new workflow, in order to see all jobs already running on the server.
    - This is high priority because it exercises the API (proving we can reconstruct all state without events)
    - Connect like this when running in remote mode and no input is supplied/or an explicit flag is passed
    - Automatically `--idle-when-done` in this case

- Add a shortcut to submit another job during execution. Pressing `a` prompts for an input.
    - args supported. E.g. `a` -> type `Artist - Title --format flac` is valid. Do not require wrapping the input in quotes. Treat everything after `--` as args.
    - `a` prompt should not pause rendering (unlike the other prompts), unless using no-progress/plain mode. When using live mode, need to display the prompt below the status bar _inside_ the live section (otherwise will get overwritten/interleave).
    - CLI should NOT exit while `a` prompt is active.
    

- Implement sharing service. Look how slskd does it for a start.

- (breaking) Maybe use yaml for settings instead of our custom format, and improve structure.

- Test performance again for song and album searches (CPU and allocations, include the raw search collection phase + projection) on big queries (e.g. `love`)