<!-- sockseek-help:start(name-format) -->

# Name format

Use `--name-format` to choose how Sockseek names downloaded files and arranges them in folders.

When `--name-format` is blank (the default), songs keep their filenames, albums and whole folders keep their tree, and a named list or playlist may add an enclosing folder.

Suppose `01.flac` has the artist tag `North Yard` and the title tag `Opening`. Sockseek substitutes those values wherever you write `{artist}` and `{title}`:

```text
{artist} - {title}
```

Saves the file as `North Yard - Opening.flac`.

---

```text
{artist}/{title}
```

Saves it as `North Yard/Opening.flac`, with a folder for the artist.

These two forms are simple, but they assume every file has the tags. A missing tag is left blank, so a file with no artist tag is saved as ` - Opening.flac`. In almost every case you should use the form below, which falls back to the original filename.

---

```text
{artist( - )title|filename}
```

**Prefer this form.** It tries the part before `|` first. If both tags are present, it inserts the text inside `( - )` between them, giving `North Yard - Opening.flac`. If either tag is missing, it uses `filename` and keeps `01.flac`. Note that you can chain `|` more than once.

---


```text
{albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}
```

Organizes tagged tracks by album artist and album. If a required tag is missing, the file goes under `missing-tags` with its remote folder and filename.

---

See the [variable reference](../reference/variables.md) or run `sockseek --help variables`.

<!-- sockseek-help:end -->
