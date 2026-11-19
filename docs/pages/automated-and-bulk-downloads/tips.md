<!-- sockseek-help:start(notes-and-tips) -->

# Tips

## Rate limits

The Soulseek server bans users for 30 minutes if too many searches are performed within a short timespan. Sockseek has a search limiter which can be adjusted with `--searches-per-time` and `--searches-renew-time` (when the limit is reached, the status of the downloads will be 'Waiting'). By default it is configured to allow up to 34 searches every 220 seconds.

## Why are some of my downloads wrong or missing?

### **It downloaded a wrong song or album!**

By default, Sockseek doesn't do any filtering on the results returned by Soulseek, erring on the side of downloading incorrect files rather than missing files entirely. If the correct result exists among the things returned by the network, it will usually be chosen. But if Soulseek doesn't have it, Sockseek might download something incorrect. If you're bothered by this, you can make matching more strict as shown below, at the cost of increased chance of missing correct items in other cases.

**For song downloads, you can:**

-
   Set the length tolerance `--length-tol` to something like 3, in order to ignore all files whose length differs from the supplied length by more than 3 seconds. This is useful only when your source actually provides length metadata, such as in the case of playlists from streaming services or a CSV file with a track duration column. Note that lengths from streaming services can differ from the actual files on Soulseek (like from a CD rip).
-
   Add filters for the title and artist:
   ```bash
   sockseek "https://spotify.com/playlist/blah" --strict-title --strict-artist
   ```
   These require that the file path contains the song title and artist name (case-insensitive). Be careful with spelling differences and weird names.

**For album downloads**, the cleanest filter is usually the expected track count:

```bash
sockseek "Artist - Album" --album-track-count 10
```

Use an inequality like `10+` when an expanded edition is acceptable. You can also add `--strict-album` to require the album name in the folder path, or `--strict-artist` as in the song case.

### **It says no results for a song or album!**
Sockseek passes your input (i.e. artist + song name in case of songs, and artist + album name in case of albums) directly to Soulseek. It doesn't do any hard filtering by default, which means that no results found almost always implies that your inputs itself have no matches on Soulseek. Some things to try:

- Clean up your inputs when they contain uncommon names or artist spellings, for example, with options like `--regex` and `--remove-ft`. See also the next section.
- Set `--fallback-level 3`. This will make Sockseek launch additional searches (e.g. without the artist name) whenever it receives no results. Note that this can make searches slower and sometimes results in more incorrect files getting downloaded.

Use `--print results-full` to inspect what Soulseek returned without downloading anything.

## Searching

- It's always best to provide the least input necessary to uniquely identify an album or song.
  - Sometimes including the artist can be undesirable (e.g. "Various Artists"). For spotify or bandcamp inputs, you can remove the artist name with `--regex A:.*`.
  - Use `--remove-ft` to remove "feat." or "ft." artists
- You can download an entire album based on the name of one of its songs by searching for that name in album mode: `"artist=ARTIST, title=SONG TITLE" --album`.
- When searching for a single song with a string input, you can provide the album name in addition. The album name will not be included in the query, but search results containing it will be preferred (due to pref-strict-album).
- When dealing with YouTube playlists you may want to remove any text in parentheses (like (Video)), as well as "Official" and "Lyrics" with `--regex "[\[\(].*?[\]\)]|(?i:lyrics)|(?i:official)"`

## Testing Options

You can test almost any aspect of the search and downloading logic with `--mock-files-dir` and pointing it to a local directory containing audio files. This directory will then be used instead of searching and downloading from Soulseek. Example:

```bash
sockseek "Artist - Album" -t --mock-files-dir /path/to/dir
```

If you plan to use a large music library, you might want to add `--mock-files-no-read-tags` to improve the initial loading performance. But note that reading tags is required when filtering by metadata such as length or bitrate.

<!-- sockseek-help:end -->
