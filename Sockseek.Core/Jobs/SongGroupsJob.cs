using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Jobs;
    // Downloads all search results matching a query (e.g. all songs by an artist,
    // all versions of a song, or all results for a given name). Groups same songs.
    public class SongGroupsJob : Job, IUpgradeable
    {
        public SongQuery Query { get; }
        public override SongQuery QueryTrack => Query;

        protected override bool DefaultCanBeSkipped => false;

        // One SongJob per found variant. Populated after search.
        private readonly JobMembers<SongJob> members;
        public IReadOnlyList<SongJob> Songs
        {
            get => members.Snapshot;
            set => members.Replace(value);
        }
        internal JobOutcomeSummary MemberOutcomes => members.Summary;

        public SongGroupsJob(SongQuery query)
        {
            Query = query;
            members = new(false, NotifyStructureChanged);
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        public IEnumerable<Job> Upgrade(bool album, bool group)
        {
            if (album)
            {
                var newQuery = AlbumQuery.FromSongQuery(Query);
                var newJob = new AlbumGroupsJob(newQuery);
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
