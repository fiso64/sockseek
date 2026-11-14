using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Jobs;
    // Finds all distinct albums matching a query (e.g. by an artist); converts each to an AlbumJob added to the queue.
    // Has no download state of its own after conversion.
    public class AlbumGroupsJob : Job, IUpgradeable
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view for base-class helpers. Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, URI = Query.URI };

        protected override bool DefaultCanBeSkipped => false;

        // Populated after group search: one AlbumJob per distinct album candidate.
        private readonly JobMembers<AlbumJob> members;
        public IReadOnlyList<AlbumJob> Albums
        {
            get => members.Snapshot;
            set => members.Replace(value);
        }
        internal JobOutcomeSummary MemberOutcomes => members.Summary;

        public AlbumGroupsJob(AlbumQuery query)
        {
            Query = query;
            members = new(false, NotifyStructureChanged);
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        public IEnumerable<Job> Upgrade(bool album, bool group)
        {
            yield return this;
        }
    }
