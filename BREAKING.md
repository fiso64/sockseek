# Breaking changes

| Change | Notes |
|--------|-------|
| `{state}` name-format / on-complete variable for songs now uses split lifecycle/activity/outcome wording | Runtime state is split into lifecycle, activity phase, terminal outcome, and skip reason |
| `--parallel-album-search` / `parallelAlbumSearch` removed | Superseded by full parallel search+download |
| `--concurrent-processes` / `--concurrent-downloads` removed | Downloads are now unlimited; `--concurrent-searches` replaces the search-limiting role |
