# Breaking changes

| Change | Notes |
|--------|-------|
| `{state}` name-format / on-complete variable for songs: `"Downloaded"` → `"Done"` | `JobState` replaces `TrackState` on `SongJob` |
| `--parallel-album-search` / `parallelAlbumSearch` removed | Superseded by full parallel search+download |
| `--concurrent-processes` / `--concurrent-downloads` removed | Downloads are now unlimited; `--concurrent-searches` replaces the search-limiting role |