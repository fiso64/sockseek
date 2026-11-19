<!-- sockseek-help:start(variables) -->
# Variables

Variables are shared by name formats and on-complete commands.

## Shared placement variables

These variables are available for all download jobs.

```
peer-username / username        Username of the peer serving the file
filename / slsk-filename        Remote filename without its extension
ext                             Final output file extension, including the dot
relative-path                   Relative directory plus filename, without the extension
foldername / slsk-foldername    Selected root plus relative directory; for a direct
                                file, its immediate remote parent folder
item-name                       Current song, album, file, or folder name
default-folder                  Enclosing list/playlist folders for the current job
                                (e.g. Spotify playlist name)
output-dir / outputdir          Output parent directory (--output)
type                            Job type (Song, Album, RemoteFile, RemoteDirectory, ..)
extractor                       Name of the input extractor (Spotify, CSV, ..)
input                           Original input string
bindir                          Base application directory
configdir                       Active config file directory
```

## Music variables

These are read from the downloaded file's tags:
```
artist                         First artist
artists                        Artists, joined with '&'
albumartist                    First album artist
albumartists                   Album artists, joined with '&'
title                          Track title
album                          Album name
year                           Track year
track                          Track number
disc                           Disc number
length                         Track length (in seconds)
```

These are taken from the input source (CSV file data, Spotify, etc):
```
sartist / sartists             Source artist
stitle                         Source track title
salbum                         Source album name
slength                        Source track length
uri / url                      Track URI
snum                           Source item number (1-indexed, including offset)
row / line                     Line number (1-indexed, only for CSV or list input)
artist-maybe-wrong             If the source artist might be incorrect (true/false)
```

<!-- sockseek-help:end -->
