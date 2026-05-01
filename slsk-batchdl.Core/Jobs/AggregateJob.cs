using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    // Downloads all search results matching a query (e.g. all songs by an artist,
    // all versions of a song, or all results for a given name). Groups same songs.
    public class AggregateJob : Job, IUpgradeable
    {
        public SongQuery Query { get; }
        public override SongQuery QueryTrack => Query;

        protected override bool DefaultCanBeSkipped => false;

        // One SongJob per found variant. Populated after search.
        public List<SongJob> Songs { get; set; } = new();

        public AggregateJob(SongQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        public IEnumerable<Job> Upgrade(bool album, bool aggregate)
        {
            if (album)
            {
                var newQuery = AlbumQuery.FromSongQuery(Query);
                var newJob = new AlbumAggregateJob(newQuery);
                newJob.CopySharedFieldsFrom(this);
                if (Query.Title.Length > 0)
                {
                    newJob.ExtractorFolderCond ??= new FolderConditionPatch();
                    newJob.ExtractorFolderCond.AddRequiredTrackTitle(Query.Title);
                }
                newJob.ItemName ??= newJob.ToString(noInfo: true);
                yield return newJob;
            }
            else
            {
                yield return this;
            }
        }
    }
