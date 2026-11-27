<!-- sockseek-help:start(input) -->

# Input types

The input type is usually determined automatically. You can also manually set it with `--input-type`.

The following input types are supported:

## CSV file

A path to a local CSV file listing songs or albums.

In the CSV file, rows with a track title are song downloads, and rows without a title are treated as album downloads. Only a title or album column is required in the CSV file (extra info may improve search results). If the columns have common names (like 'Artist', 'Title', 'Album', 'Length', etc) then it's not required to manually specify them, otherwise you must provide at least `--title-col` or `--album-col`.

See [CSV options](../reference/options.md#csv-file-options) for all options.

## YouTube

A YouTube playlist URL.

The default method to retrieve playlists might not reliably return all videos. If you're having issues, you can use the official API by providing a key with `--youtube-key`. A key can be obtained at https://console.cloud.google.com (create a new project, click 'Enable API' and search for 'YouTube Data', then follow the prompts).

See [YouTube options](../reference/options.md#youtube-options) for all options.

## Spotify

Spotify inputs support:

- Public and private playlist URLs
- Album URLs
- Liked songs, with `sockseek spotify-likes`
- Liked albums, with `sockseek spotify-albums`

Spotify API access requires your own Spotify developer application for all Spotify inputs,
including public playlists. Spotify also requires the owner of that application to have an
active Spotify Premium subscription. If you do not have Premium, you can export the Spotify playlist
with a Spotify-to-CSV converter and pass the CSV file to Sockseek instead.

See [Spotify options](../reference/options.md#spotify-options) for all options.

### Using Credentials

<details>
  <summary>Click to expand</summary>

Create a Spotify application at https://developer.spotify.com/dashboard/applications with a redirect URL http://127.0.0.1:48721/callback. The Spotify account that owns the application must have an active Premium subscription. Obtain an application ID and secret from the created application dashboard.

For public playlists and albums, pass the application credentials:

```bash
sockseek "https://open.spotify.com/playlist/id" --spotify-id 123456 --spotify-secret 123456
```

For private playlists, liked songs, liked albums, or `--remove-from-source`, start Sockseek with the obtained credentials and an action that requires auth to trigger the Spotify app login flow:

```bash
sockseek spotify-likes --spotify-id 123456 --spotify-secret 123456 -n 1 --print jobs
```

Sockseek will try to open a browser automatically but will fall back to logging the login flow URL to output. After login flow is complete it will output a token and refresh token and finish running the current command.

To skip requiring login flow every time Sockseek is used the token and refresh token can be provided (hint: store this info in the config file to make commands less verbose):

```bash
sockseek spotify-likes --spotify-id 123456 --spotify-secret 123456 --spotify-refresh 123456 --spotify-token 123456 -n 1 --pt
```

spotify-token access is only valid for 1 hour. spotify-refresh will enable Sockseek to renew access every time it is run (and can be used without including spotify-token)

</details>

## Bandcamp

Bandcamp inputs support:

- Track URLs
- Album URLs
- Artist URLs, to download all albums by the artist.
- Wishlist URLs, to download all albums on the wishlist.

Extraction might fail due to Cloudflare; download the HTML to a local file and point Sockseek to it using `--from-html` in case of issues.

<!-- Uncomment if it gains more than one option: See [Bandcamp options](../reference/options.md#bandcamp-options) for all options. -->

## MusicBrainz

MusicBrainz inputs support:

- `/release/...` URLs
- `/release-group/...` URLs, where it picks the most common version of the album.
- `/collection/...` URLs, to download a list of albums.

## SoundCloud

SoundCloud inputs support:

- Track URLs
- Public and private playlist URLs

Requires a SoundCloud OAuth token, passed with `--soundcloud-token`, also for public items.

When a track has no publisher artist, the artist is taken from titles like "Artist - Title", falling back to the uploader's name.

<!-- Uncomment if it gains more than one option: See [SoundCloud options](../reference/options.md#soundcloud-options) for all options. -->

### Getting the OAuth token

<details>
  <summary>Click to expand</summary>

1. Log in to https://soundcloud.com in your browser.
2. Open the developer tools (F12) and go to the Storage tab (Application in Chrome).
3. Under Cookies > https://soundcloud.com, copy the value of `oauth_token`.

Pass it with `--soundcloud-token`, or put it in your config file:

```
soundcloud-token = <your-token>
```

</details>

## Soulseek Link

A direct path starting with `slsk://`. By default, a path ending in `/` is an ordinary
folder download and a path without it is an ordinary exact-file download. Use `--song`
or `--album` to request music-specific handling. `--song` rejects directory links;
`--album` treats the supplied remote path as the selected album directory, whether or
not it has a trailing slash.

## Search string

A string input is a name of the track, album, or artist to search for. They come in these forms:

1.
   Arbitrary search queries (like what you would type in the Soulseek search bar), e.g. `sockseek "blah blah"`.

   _This is equivalent to `sockseek "album=blah blah"` by default, and to `sockseek "title=blah blah"` when using `-s`/`--song` mode._

2.
   An artist-album or artist-song combination, separated by a hyphen with spaces ` - `:

   - `sockseek "Artist - Album"` downloads an album
   - `sockseek "Artist - Song" -s` downloads a song

   It's always better to split the artist from song/album like this when you want to provide an artist due to [ranking and filtering](./ranking-and-filtering.md).

   _This is equivalent to `sockseek "artist=Artist, album=Album"` in the first case, and to `sockseek "artist=Artist, title=Song"` in the second case._

3.
   A comma-separated list of properties, like `title=Song Name, artist=Artist Name, length=215`.
   With an `album` field (and no `title`) it's treated as an album download. With a `title` field it's treated as a song download (even if it includes an `album` field).

Accepted fields are:
- Song title `title`
- Artist name `artist`
- Album name `album`
- Song `length` (in seconds)
<!-- Technically supports two others, but album-track-count is better passed as a condition, and artist-maybe-wrong is quite niche, need to think how to explain it. -->

Notes:
- You can combine the second and third form, e.g. `Artist - Song, length=123` is the same as `artist=Artist, title=Song, length=123` in `--song` mode.
- You can use `"artist=Artist, title=Song" --album` if you want `title=` to act as an album search hint, i.e. you want to search for an album by the name of one of its tracks.

## List file

A path to a local text file listing items of any supported input type. List inputs **must be manually activated** with `--input-type=list`.

Each line has an input followed by optional required and preferred conditions. Separate the fields with at least two spaces or a tab.

```
Any input type       conditions (optional)      pref. conditions (optional)
```

Here is an example list file:

```yaml
Artist - Album    strict-album=true; album-track-count>=13     br >= 320

# Use s: for one song, or --song to treat all unprefixed string entries as songs.
s:Artist - Song   strict-title=true

# Any other input type is also accepted:
path/to/tracks.csv
https://www.youtube.com/playlist?list=blah

# Quote a field to preserve repeated spaces within it:
s:"Artist - Song  (Live)"   "strict-title=true; format=flac"
```

The conditions are added on top of the configured conditions and can be omitted. Lines starting with `#` are treated as comments and ignored.

<!-- sockseek-help:end -->
