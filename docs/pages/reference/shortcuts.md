<!-- sockseek-help:start(shortcuts) -->

# Shortcuts & interactive mode

## CLI Shortcuts

You can use these at any time while things are searching or downloading:

```
c               cancel specific job by id or all jobs
t               try next candidate for a job id
i               get detailed info about job by id
```

## CLI Interactive Prompt Shortcuts

Interactive mode for albums can be enabled with `-t`/`--interactive`. It allows you to pick the desired folder or download only specific files from it, rather than automatically downloading the best match.

Key bindings:

```
Up/p            previous folder
Down/n          next folder
Enter/d         download selected folder
y               download folder and disable interactive mode
r               retrieve all files in the folder
s/q/Esc         skip current album
Q/S             skip current and all remaining new album prompts
h               print this help text

d:1,2,3         download specific files
d:start-end     download a range of files
f               filter folders containing files matching query
cd ..           load parent folder
cd subdir       go to subfolder
```

<!-- sockseek-help:end -->
