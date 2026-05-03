using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Server;

public static class JobRequestMapper
{
    public static ExtractJob CreateExtractJob(SubmitExtractJobRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("input is required for extract jobs");

        InputType? inputType = null;
        if (!string.IsNullOrWhiteSpace(request.InputType))
        {
            if (!Enum.TryParse<InputType>(request.InputType, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unsupported inputType '{request.InputType}'");
            inputType = parsed;
        }

        var job = new ExtractJob(request.Input, inputType);
        if (request.AutoStartExtractedResult.HasValue)
            job.AutoProcessResult = request.AutoStartExtractedResult.Value;

        return job;
    }

    public static SearchJob CreateSearchJob(SubmitSearchJobRequestDto request)
        => new(request.QueryText);

    public static SearchJob CreateTrackSearchJob(SubmitTrackSearchJobRequestDto request)
        => new(ToSongQuery(request.SongQuery), request.IncludeFullResults);

    public static SearchJob CreateAlbumSearchJob(SubmitAlbumSearchJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static SongJob CreateSongJob(SubmitSongJobRequestDto request)
        => new(ToSongQuery(request.SongQuery));

    public static AlbumJob CreateAlbumJob(SubmitAlbumJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static AggregateJob CreateAggregateJob(SubmitAggregateJobRequestDto request)
        => new(ToSongQuery(request.SongQuery));

    public static AlbumAggregateJob CreateAlbumAggregateJob(SubmitAlbumAggregateJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static JobList CreateJobList(SubmitJobListRequestDto request)
        => CreateJobList(request.Name, request.Jobs);

    public static SongQuery ToSongQuery(SongQueryDto dto) => new()
    {
        Artist = dto.Artist ?? "",
        Title = dto.Title ?? "",
        Album = dto.Album ?? "",
        URI = dto.Uri ?? "",
        Length = dto.Length ?? -1,
        ArtistMaybeWrong = dto.ArtistMaybeWrong,
    };

    public static AlbumQuery ToAlbumQuery(AlbumQueryDto dto) => new()
    {
        Artist = dto.Artist ?? "",
        Album = dto.Album ?? "",
        SearchHint = dto.SearchHint ?? "",
        URI = dto.Uri ?? "",
        ArtistMaybeWrong = dto.ArtistMaybeWrong,
    };

    public static Job CreateJob(JobDraftDto item)
        => item switch
        {
            ExtractJobDraftDto extract => CreateExtractJob(new SubmitExtractJobRequestDto(
                extract.Input,
                extract.InputType,
                extract.AutoStartExtractedResult)),
            TrackSearchJobDraftDto search => new SearchJob(ToSongQuery(search.SongQuery), search.IncludeFullResults),
            AlbumSearchJobDraftDto search => new SearchJob(ToAlbumQuery(search.AlbumQuery)),
            SongJobDraftDto song => new SongJob(ToSongQuery(song.SongQuery)),
            AlbumJobDraftDto album => new AlbumJob(ToAlbumQuery(album.AlbumQuery)),
            AggregateJobDraftDto aggregate => new AggregateJob(ToSongQuery(aggregate.SongQuery)),
            AlbumAggregateJobDraftDto aggregate => new AlbumAggregateJob(ToAlbumQuery(aggregate.AlbumQuery)),
            JobListJobDraftDto list => CreateJobList(list.Name, list.Jobs),
            _ => throw new ArgumentException($"Unsupported job draft type '{item.GetType().Name}'")
        };

    private static JobList CreateJobList(string? name, IReadOnlyList<JobDraftDto> jobs)
    {
        if (jobs.Count == 0)
            throw new ArgumentException("job-list must contain at least one child job");

        return new JobList(name, jobs.Select(CreateJob));
    }
}
