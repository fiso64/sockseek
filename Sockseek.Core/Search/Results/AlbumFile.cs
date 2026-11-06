namespace Sockseek.Core.Models;

// Search/browse result file inside an album folder. This is candidate data only;
// executable per-file download jobs are materialized on AlbumJob.TrackJobs.
public sealed class AlbumFile
{
    private readonly AlbumFileMatch match;

    public SongQuery Query => match.Query;
    public FileCandidate Candidate => match.Candidate;
    public string Filename => Candidate.Filename;
    public bool IsNotAudio => !Utils.IsMusicFile(Filename);

    public AlbumFile(SongQuery query, FileCandidate candidate)
        : this(new AlbumFileMatch(query, candidate))
    {
    }

    public static AlbumFile WithLazyQuery(Func<SongQuery> queryFactory, FileCandidate candidate)
        => new(AlbumFileMatch.WithLazyQuery(queryFactory, candidate));

    internal static AlbumFile WithProjectionEvidence(
        Func<SongQuery> queryFactory,
        Services.ProjectedFileCandidate file)
        => new(AlbumFileMatch.WithProjectionEvidence(queryFactory, file));

    internal AlbumFile(AlbumFileMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        this.match = match;
    }

    internal AlbumFileMatch Match => match;
}
