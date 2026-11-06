using Soulseek;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;

public sealed record SearchRawResult
{
    internal SearchResponse Response { get; }
    internal Soulseek.File File { get; }
    public SearchProjectionInput ProjectionInput { get; }

    public long Sequence => ProjectionInput.Sequence;
    public int Revision => ProjectionInput.Revision;
    public string Username => ProjectionInput.Username;
    public string Filename => ProjectionInput.Filename;
    public long Size => ProjectionInput.Size;
    public int? BitRate => ProjectionInput.BitRate;
    public int? BitDepth => ProjectionInput.BitDepth;
    public int ResponseFileCount => ProjectionInput.ResponseFileCount;
    public int? SampleRate => ProjectionInput.SampleRate;
    public int? Length => ProjectionInput.Length;
    public string Extension => ProjectionInput.Extension;
    public int? UploadSpeed => ProjectionInput.UploadSpeed;
    public bool? HasFreeUploadSlot => ProjectionInput.HasFreeUploadSlot;
    public int? QueueLength => ProjectionInput.QueueLength;
    public SearchResultVisibility Visibility => ProjectionInput.Visibility;
    public IReadOnlyList<FileAttributeSnapshot>? Attributes => ProjectionInput.Attributes;
    public DateTimeOffset ObservedAtUtc => ProjectionInput.ObservedAtUtc;

    internal SearchRawResult(
        long sequence,
        int revision,
        SearchResponse response,
        Soulseek.File file,
        DateTimeOffset observedAtUtc,
        SearchResultVisibility visibility = SearchResultVisibility.Public)
    {
        Response = response;
        File = file;
        ProjectionInput = SearchProjectionInput.FromLive(
            sequence, revision, response, file, observedAtUtc, visibility);
    }
}
