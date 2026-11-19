namespace Sockseek.Core.Models;

// Search/browse result file inside an album folder. This is candidate data only;
// executable per-file download jobs are materialized on AlbumJob.TrackJobs.
public sealed class AlbumFile
{
    private readonly Lazy<SongQuery> query;

    public SongQuery Query => query.Value;
    public FileCandidate Candidate { get; }
    public string Filename => Candidate.Filename;
    public bool IsNotAudio => !Utils.IsMusicFile(Filename);

    public AlbumFile(SongQuery query, FileCandidate candidate)
        : this(() => query, candidate)
    {
    }

    public static AlbumFile WithLazyQuery(Func<SongQuery> queryFactory, FileCandidate candidate)
        => new(queryFactory, candidate);

    private AlbumFile(Func<SongQuery> queryFactory, FileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(queryFactory);
        ArgumentNullException.ThrowIfNull(candidate);
        query = new Lazy<SongQuery>(() => new SongQuery(queryFactory()));
        Candidate = candidate;
    }
}
