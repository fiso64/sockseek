<!-- sockseek-help:start(name-format) -->

# Name format
Variables enclosed in {} will be replaced by the corresponding available value.
Name format supports subdirectories as well as conditional expressions like {tag1|tag2} - If
tag1 is null, use tag2. This can be chained arbitrarily many times. String literals enclosed
in parentheses are ignored in the null check. See [Variables](../reference/variables.md) for the complete
reference, also available with `sockseek --help variables`.

Sockseek can format the downloaded filenames using `--name-format <yourformat>`. Variables enclosed in `{}` will be replaced by the corresponding file tag value.
Name format supports subdirectories as well as conditional expressions like `{tag1|tag2}` - If
tag1 is null, use tag2 (can be chained). Examples:

- `{artist} - {title}`
    Always name it 'Artist - Title'. Because some files on Soulseek are untagged, the
    following is generally preferred:
- `{artist( - )title|filename}`
    Everything inside round parentheses `()` inside braces `{}` is treated as a raw string and ignored in the null check. Here, if artist and title are both not null, name it 'Artist - Title', otherwise use the original filename.
- `{albumartist(/)album(/)track(. )title|(missing-tags/)slsk-foldername(/)slsk-filename}`
    Sort files into artist/album folders if all tags are present, otherwise put them in
    the 'missing-tags' folder.
<!-- sockseek-help:end -->
