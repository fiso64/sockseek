using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    // Universal grouping container. Holds any mix of Job subtypes.
    // Replaces SongListQueryJob, AlbumListJob, and JobQueue as the root container.
    //
    // State is derived: Done when all children are done, Failed if all failed.
    public class JobList : Job, IUpgradeable
    {
        public List<Job> Jobs { get; } = new();

        public int Count => Jobs.Count;

        public Job this[int index] => Jobs[index];

        protected override bool DefaultCanBeSkipped => false;

        public JobList(string? name = null, IEnumerable<Job>? jobs = null)
        {
            ItemName = name;
            if (jobs != null) Jobs.AddRange(jobs);
        }

        public void Add(Job job) => Jobs.Add(job);

        public void Reverse()
        {
            Jobs.Reverse();
            foreach (var job in Jobs)
            {
                if (job is JobList jl)
                    jl.Jobs.Reverse();
            }
        }

        // Yields all SongJobs reachable from this list, traversing ExtractJob.Result nodes.
        public IEnumerable<SongJob> AllSongs() => AllJobs().OfType<SongJob>();

        // Yields every Job reachable from this list (depth-first), following ExtractJob.Result.
        public IEnumerable<Job> AllJobs()
        {
            foreach (var job in Jobs)
                foreach (var j in WalkJob(job))
                    yield return j;
        }

        private static IEnumerable<Job> WalkJob(Job job)
        {
            yield return job;

            if (job is ExtractJob ej && ej.Result != null)
                foreach (var j in WalkJob(ej.Result))
                    yield return j;

            if (job is JobList jl)
                foreach (var child in jl.Jobs)
                    foreach (var j in WalkJob(child))
                        yield return j;
        }

        // Reverses a list of jobs and the children inside any JobList containers.
        public static void ReverseJobList(List<Job> jobs)
        {
            jobs.Reverse();
            foreach (var job in jobs)
            {
                if (job is JobList jl)
                    jl.Jobs.Reverse();
            }
        }

        public override string ToString(bool noInfo)
            => ItemName ?? $"JobList ({Jobs.Count} jobs)";

        public IEnumerable<Job> Upgrade(bool album, bool aggregate)
        {
            if (!album && !aggregate)
            {
                yield return this;
                yield break;
            }

            if (album && !aggregate)
            {
                var albumList = new JobList();
                albumList.CopySharedFieldsFrom(this);
                foreach (var job in Jobs)
                {
                    if (job is IUpgradeable u)
                    {
                        foreach (var upgraded in u.Upgrade(album, aggregate))
                            albumList.Add(upgraded);
                    }
                    else
                    {
                        albumList.Add(job);
                    }
                }
                DeduplicateSongUpgradedAlbums(albumList);
                yield return albumList;
            }
            else
            {
                foreach (var job in Jobs)
                {
                    if (job is IUpgradeable u)
                    {
                        foreach (var upgraded in u.Upgrade(album, aggregate))
                            yield return upgraded;
                    }
                    else
                    {
                        yield return job;
                    }
                }
            }
        }

        private static void DeduplicateSongUpgradedAlbums(JobList list)
        {
            var keep = new List<Job>();
            var albumsByKey = new Dictionary<string, List<AlbumJob>>();

            foreach (var job in list.Jobs)
            {
                if (job is not AlbumJob album || album.UpgradeSources.Count == 0)
                {
                    keep.Add(job);
                    continue;
                }

                string? key = AlbumDedupKey(album.Query);
                AlbumJob? existing = key != null && albumsByKey.TryGetValue(key, out var candidates)
                    ? candidates.FirstOrDefault(candidate => CanMergeSongUpgradedAlbums(candidate, album))
                    : null;

                if (key == null || existing == null)
                {
                    keep.Add(job);
                    if (key != null)
                    {
                        if (!albumsByKey.TryGetValue(key, out var albums))
                            albumsByKey[key] = albums = new List<AlbumJob>();
                        albums.Add(album);
                    }
                    continue;
                }

                MergeSongUpgradedAlbum(existing, album);
            }

            list.Jobs.Clear();
            list.Jobs.AddRange(keep);
        }

        private static string? AlbumDedupKey(AlbumQuery query)
        {
            if (query.Album.Length == 0)
                return null;

            return "album:" + query.Artist.Trim().ToUpperInvariant() + "\n" + query.Album.Trim().ToUpperInvariant();
        }

        private static bool CanMergeSongUpgradedAlbums(AlbumJob a, AlbumJob b)
            => a.UpgradeSources.Count > 0
            && b.UpgradeSources.Count > 0
            && AlbumQueriesAreEquivalent(a.Query, b.Query)
            && ConditionsEqual(a.ExtractorCond, b.ExtractorCond)
            && ConditionsEqual(a.ExtractorPrefCond, b.ExtractorPrefCond)
            && FolderConditionsEqual(a.ExtractorFolderCond, b.ExtractorFolderCond, ignoreRequiredTrackTitles: true)
            && FolderConditionsEqual(a.ExtractorPrefFolderCond, b.ExtractorPrefFolderCond, ignoreRequiredTrackTitles: false)
            && a.EnablesIndexByDefault == b.EnablesIndexByDefault
            && a.CanBeSkippedOverride == b.CanBeSkippedOverride;

        private static bool AlbumQueriesAreEquivalent(AlbumQuery a, AlbumQuery b)
            => a.Artist == b.Artist
            && a.Album == b.Album
            && a.SearchHint == b.SearchHint
            && a.URI == b.URI
            && a.ArtistMaybeWrong == b.ArtistMaybeWrong;

        private static bool ConditionsEqual(FileConditionPatch? a, FileConditionPatch? b)
        {
            if (a == null || b == null)
                return (a == null || a.IsEmpty()) && (b == null || b.IsEmpty());

            return a.Equals(b);
        }

        private static bool FolderConditionsEqual(FolderConditionPatch? a, FolderConditionPatch? b, bool ignoreRequiredTrackTitles)
        {
            if (a == null || b == null)
                return (a == null || a.IsEmpty()) && (b == null || b.IsEmpty());

            return a.MinTrackCount == b.MinTrackCount
                && a.MaxTrackCount == b.MaxTrackCount
                && (ignoreRequiredTrackTitles || ((a.RequiredTrackTitles == null && b.RequiredTrackTitles == null)
                    || (a.RequiredTrackTitles != null && b.RequiredTrackTitles != null
                        && a.RequiredTrackTitles.SequenceEqual(b.RequiredTrackTitles))));
        }

        private static void MergeSongUpgradedAlbum(AlbumJob target, AlbumJob duplicate)
        {
            target.UpgradeSources.AddRange(duplicate.UpgradeSources.Select(s => new SongQuery(s)));
            target.ExtractorFolderCond ??= new FolderConditionPatch();
            if (duplicate.ExtractorFolderCond?.RequiredTrackTitles != null)
                target.ExtractorFolderCond.AddRequiredTrackTitles(duplicate.ExtractorFolderCond.RequiredTrackTitles);
        }
    }
