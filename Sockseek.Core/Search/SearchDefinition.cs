using System.Text.Json;
using System.Text.Json.Serialization;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter<SearchDefaultProjectionKind>))]
public enum SearchDefaultProjectionKind
{
    GenericFile,
    Track,
    Album,
}

public sealed record SongQueryDefinition(
    string Artist,
    string Title,
    string Album,
    string Uri,
    int Length,
    bool ArtistMaybeWrong)
{
    public static SongQueryDefinition From(SongQuery query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong);

    public SongQuery ToQuery() => new()
    {
        Artist = Artist,
        Title = Title,
        Album = Album,
        URI = Uri,
        Length = Length,
        ArtistMaybeWrong = ArtistMaybeWrong,
    };
}

public sealed record AlbumQueryDefinition(
    string Artist,
    string Album,
    string Uri,
    string SearchHint,
    bool ArtistMaybeWrong)
{
    public static AlbumQueryDefinition From(AlbumQuery query)
        => new(query.Artist, query.Album, query.URI, query.SearchHint, query.ArtistMaybeWrong);

    public AlbumQuery ToQuery() => new()
    {
        Artist = Artist,
        Album = Album,
        URI = Uri,
        SearchHint = SearchHint,
        ArtistMaybeWrong = ArtistMaybeWrong,
    };
}

public sealed record FileConditionsDefinition(
    int? LengthTolerance,
    int? MinBitrate,
    int? MaxBitrate,
    int? MinSampleRate,
    int? MaxSampleRate,
    int? MinBitDepth,
    int? MaxBitDepth,
    bool StrictTitle,
    bool StrictArtist,
    bool StrictAlbum,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> BannedUsers,
    IReadOnlyList<string> AllowedUsers,
    bool AcceptNoLength,
    bool AcceptMissingProps)
{
    public static FileConditionsDefinition From(FileConditions conditions)
        => new(
            conditions.LengthTolerance,
            conditions.MinBitrate,
            conditions.MaxBitrate,
            conditions.MinSampleRate,
            conditions.MaxSampleRate,
            conditions.MinBitDepth,
            conditions.MaxBitDepth,
            conditions.StrictTitle,
            conditions.StrictArtist,
            conditions.StrictAlbum,
            conditions.Formats.ToArray(),
            conditions.BannedUsers.ToArray(),
            conditions.AllowedUsers.ToArray(),
            conditions.AcceptNoLength,
            conditions.AcceptMissingProps);

    public FileConditions ToConditions() => new()
    {
        LengthTolerance = LengthTolerance,
        MinBitrate = MinBitrate,
        MaxBitrate = MaxBitrate,
        MinSampleRate = MinSampleRate,
        MaxSampleRate = MaxSampleRate,
        MinBitDepth = MinBitDepth,
        MaxBitDepth = MaxBitDepth,
        StrictTitle = StrictTitle,
        StrictArtist = StrictArtist,
        StrictAlbum = StrictAlbum,
        Formats = Formats.ToArray(),
        BannedUsers = BannedUsers.ToArray(),
        AllowedUsers = AllowedUsers.ToArray(),
        AcceptNoLength = AcceptNoLength,
        AcceptMissingProps = AcceptMissingProps,
    };
}

public sealed record FolderConditionsDefinition(
    int? MinTrackCount,
    int? MaxTrackCount,
    IReadOnlyList<string> RequiredTrackTitles)
{
    public static FolderConditionsDefinition From(FolderConditions conditions)
        => new(
            conditions.MinTrackCount,
            conditions.MaxTrackCount,
            conditions.RequiredTrackTitles.ToArray());

    public FolderConditions ToConditions()
    {
        var conditions = new FolderConditions
        {
            MinTrackCount = MinTrackCount,
            MaxTrackCount = MaxTrackCount,
        };
        conditions.AddRequiredTrackTitles(RequiredTrackTitles);
        return conditions;
    }
}

public sealed record SearchProjectionSettingsDefinition(
    FileConditionsDefinition NecessaryFile,
    FileConditionsDefinition PreferredFile,
    FolderConditionsDefinition NecessaryFolder,
    FolderConditionsDefinition PreferredFolder,
    bool StrictAlbumQuality,
    int DownrankOn,
    int IgnoreOn,
    int MinSharesAggregate,
    int AggregateLengthTolerance)
{
    public static SearchProjectionSettingsDefinition From(SearchSettings settings)
        => new(
            FileConditionsDefinition.From(settings.NecessaryCond),
            FileConditionsDefinition.From(settings.PreferredCond),
            FolderConditionsDefinition.From(settings.NecessaryFolderCond),
            FolderConditionsDefinition.From(settings.PreferredFolderCond),
            settings.StrictAlbumQuality,
            settings.DownrankOn,
            settings.IgnoreOn,
            settings.MinSharesAggregate,
            settings.AggregateLengthTol);

    public SearchSettings ToSettings()
    {
        SearchSettings settings = SearchSettingsBaselines
            .Create(SearchSettingsBaselineKind.Generic)
            .Search;
        settings.NecessaryCond = NecessaryFile.ToConditions();
        settings.PreferredCond = PreferredFile.ToConditions();
        settings.NecessaryFolderCond = NecessaryFolder.ToConditions();
        settings.PreferredFolderCond = PreferredFolder.ToConditions();
        settings.StrictAlbumQuality = StrictAlbumQuality;
        settings.DownrankOn = DownrankOn;
        settings.IgnoreOn = IgnoreOn;
        settings.MinSharesAggregate = MinSharesAggregate;
        settings.AggregateLengthTol = AggregateLengthTolerance;
        return settings;
    }
}

public sealed record SearchDefinition(
    int SchemaVersion,
    SearchSettingsBaselineKind Baseline,
    SearchDefaultProjectionKind DefaultProjection,
    string NetworkQuery,
    SongQueryDefinition? FileQuery,
    AlbumQueryDefinition? AlbumQuery,
    bool IncludeFullFileResults,
    SearchProjectionSettingsDefinition ProjectionSettings,
    bool AggregatePresentation = false)
{
    public const int CurrentSchemaVersion = 1;

    public static SearchDefinition Create(SearchJob job, SearchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);

        SearchDefaultProjectionKind projection;
        SongQueryDefinition? fileQuery = null;
        AlbumQueryDefinition? albumQuery = null;
        bool includeFullResults = false;
        if (job.DefaultFolderProjection is { } folder)
        {
            projection = SearchDefaultProjectionKind.Album;
            albumQuery = AlbumQueryDefinition.From(folder.Query);
        }
        else if (job.DefaultFileProjection is { } file)
        {
            projection = SearchDefaultProjectionKind.Track;
            fileQuery = SongQueryDefinition.From(file.Query);
            includeFullResults = file.IncludeFullResults;
        }
        else
        {
            projection = SearchDefaultProjectionKind.GenericFile;
            fileQuery = SongQueryDefinition.From(new SongQuery { Title = job.QueryText });
        }

        return new SearchDefinition(
            CurrentSchemaVersion,
            SearchSettingsBaselines.For(job),
            projection,
            job.QueryText,
            fileQuery,
            albumQuery,
            includeFullResults,
            SearchProjectionSettingsDefinition.From(settings),
            job.AggregatePresentation || job.Definition?.AggregatePresentation == true);
    }

    public static SearchDefinition Create(Job job, SearchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);
        if (job is SearchJob search)
            return Create(search, settings);

        return job switch
        {
            SongJob song => Create(SongQueryDefinition.From(song.Query), settings),
            AggregateJob aggregate => Create(SongQueryDefinition.From(aggregate.Query), settings),
            AlbumJob album => Create(AlbumQueryDefinition.From(album.Query), settings),
            AlbumAggregateJob aggregate => Create(AlbumQueryDefinition.From(aggregate.Query), settings),
            _ => throw new ArgumentException(
                $"Job type '{job.GetType().Name}' does not execute a search.",
                nameof(job)),
        };
    }

    internal static SearchDefinition Create(SongQueryDefinition query, SearchSettings settings)
        => new(
            CurrentSchemaVersion,
            SearchSettingsBaselineKind.Music,
            SearchDefaultProjectionKind.Track,
            query.ToQuery().ToString(noInfo: true),
            query,
            null,
            false,
            SearchProjectionSettingsDefinition.From(settings));

    internal static SearchDefinition Create(AlbumQueryDefinition query, SearchSettings settings)
        => new(
            CurrentSchemaVersion,
            SearchSettingsBaselineKind.Music,
            SearchDefaultProjectionKind.Album,
            SearchResultProjector.AlbumNetworkQuery(query.ToQuery()).ToString(noInfo: true),
            null,
            query,
            false,
            SearchProjectionSettingsDefinition.From(settings));

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported search-definition schema version {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(NetworkQuery))
            throw new InvalidDataException("Search definition has no network query.");
        if (DefaultProjection is SearchDefaultProjectionKind.GenericFile or SearchDefaultProjectionKind.Track
            && FileQuery == null)
            throw new InvalidDataException("File search definition has no file query.");
        if (DefaultProjection == SearchDefaultProjectionKind.Album && AlbumQuery == null)
            throw new InvalidDataException("Album search definition has no album query.");
        if (DefaultProjection == SearchDefaultProjectionKind.GenericFile && AggregatePresentation)
            throw new InvalidDataException("Generic search definitions cannot request aggregate presentation.");
    }
}

public static class SearchDefinitionCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(SearchDefinition definition)
    {
        definition.Validate();
        return JsonSerializer.Serialize(definition, Options);
    }

    public static SearchDefinition Deserialize(string json)
    {
        SearchDefinition definition = JsonSerializer.Deserialize<SearchDefinition>(json, Options)
            ?? throw new InvalidDataException("Search definition is empty.");
        definition.Validate();
        return definition;
    }
}
