using Sldl.Core.Models;

namespace Sldl.Core.Jobs;
    // Finds all distinct albums matching a query (e.g. by an artist); converts each to an AlbumJob added to the queue.
    // Has no download state of its own after conversion.
    public class AlbumAggregateJob : Job, IUpgradeable
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view for base-class helpers. Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, URI = Query.URI };

        protected override bool DefaultCanBeSkipped => false;

        // Populated after aggregate search: one AlbumJob per distinct album candidate.
        public List<AlbumJob> Albums { get; set; } = new();

        public AlbumAggregateJob(AlbumQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        public IEnumerable<Job> Upgrade(bool album, bool aggregate)
        {
            yield return this;
        }
    }
