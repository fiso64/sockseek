using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers;
using System.Text;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Planning;

public sealed record SubmissionSourceRevision(
    string Kind,
    string Identity,
    string? Digest,
    long? Length,
    DateTimeOffset? LastModifiedUtc);

public sealed record RetainedJobSettings(string Json, IReadOnlyList<string> CredentialBindings);

/// <summary>
/// Durable accepted intent. EffectiveSettingsJson contains the complete
/// execution settings except configured credentials; credential binding names
/// record which current operator-owned secret slots execution may require.
/// </summary>
public sealed record SubmissionSpecification(
    int SchemaVersion,
    NormalizedJobCommand Command,
    string EffectiveSettingsJson,
    IReadOnlyList<string> CredentialBindings,
    SearchDefinition? Search,
    SubmissionSourceRevision? SourceRevision)
{
    public const int CurrentSchemaVersion = 5;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RetainedJobSettings>? SharedSettings { get; init; }

    /// <summary>Capture the prepared root's frozen settings and descendants.</summary>
    public static SubmissionSpecification Create(Job job)
        => CreateCore(job, effectiveSettings: null, sourceRevision: null, includeDescendants: true);

    /// <summary>Capture an unprepared root with explicit effective settings.</summary>
    public static SubmissionSpecification Create(
        Job job,
        DownloadSettings effectiveSettings,
        SubmissionSourceRevision? sourceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(effectiveSettings);
        if (job.PlannedSettings is not null)
            throw new InvalidOperationException("Capture a prepared root without supplying a second settings value.");
        return CreateCore(job, effectiveSettings, sourceRevision, includeDescendants: true);
    }

    // Preview nodes retain their own settings and provenance without copying an
    // entire extracted tree into every ancestor's temporary spool row.
    public static SubmissionSpecification CreateShallow(
        Job job,
        DownloadSettings effectiveSettings,
        SubmissionSourceRevision? sourceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveSettings);
        return CreateCore(job, effectiveSettings, sourceRevision, includeDescendants: false);
    }

    private static SubmissionSpecification CreateCore(
        Job job,
        DownloadSettings? effectiveSettings,
        SubmissionSourceRevision? sourceRevision,
        bool includeDescendants)
    {
        ArgumentNullException.ThrowIfNull(job);
        JobSettingsSnapshot? prepared = effectiveSettings is null
            ? job.PlannedSettings ?? throw new InvalidOperationException("Prepare the root's settings before capture.")
            : null;
        sourceRevision ??= job.PlannedSourceRevision;
        using var writer = includeDescendants
            && job is JobList { Count: > 0 } or ExtractJob { HasPlannedExtraction: true, Result: not null }
            ? new RetainedSettingsWriter() : null;
        RetainedJobSettings retained = prepared?.Retain(writer) ?? RetainSettings(effectiveSettings!, writer);
        NormalizedJobCommand command = CommandFrom(job, includeDescendants, writer);

        return new SubmissionSpecification(
            CurrentSchemaVersion,
            command,
            retained.Json,
            retained.CredentialBindings,
            command.SearchDefinition ?? (prepared is not null
                ? prepared.Describe(job) : CaptureSearch(job, effectiveSettings!.Search)),
            sourceRevision);
    }

    internal static SearchDefinition? CaptureSearch(Job job, SearchSettings settings)
        => job switch
        {
            SearchJob searchJob => CaptureSearch(searchJob.Definition)
                ?? SearchDefinition.Create(searchJob, settings),
            SongJob or AlbumJob or AggregateJob or AlbumAggregateJob =>
                SearchDefinition.Create(job, settings),
            _ => null,
        };

    public DownloadSettings MaterializeSettings(DownloadSettings? credentialSource = null)
    {
        Dictionary<string, DownloadSettings> settings = ReadValidatedSettings();
        return RestoreSettings(EffectiveSettingsJson, CredentialBindings, credentialSource, settings);
    }

    public Job MaterializeJob(DownloadSettings? credentialSource = null)
        => MaterializeJob(new SettingsMaterializer(this, credentialSource, ReadValidatedSettings()));

    /// <summary>Restores jobs and root settings with one validation and decoding pass.</summary>
    public (Job Job, DownloadSettings Settings) Materialize(DownloadSettings? credentialSource = null)
    {
        var settings = new SettingsMaterializer(this, credentialSource, ReadValidatedSettings());
        return (MaterializeJob(settings), settings.ReadRoot());
    }

    private Job MaterializeJob(SettingsMaterializer settings)
    {
        Job job = JobFrom(Command, settings);
        if (job is SearchJob searchJob && Search != null)
            searchJob.Definition = Search;
        return job;
    }

    // Validated source definitions do not yet have retained settings. Use the
    // same runtime factory as Review/rerun without inventing settings, decoding
    // JSON or constructing a second set of per-kind execution mappings.
    internal static Job MaterializeCommand(NormalizedJobCommand command)
    {
        var specification = new SubmissionSpecification(CurrentSchemaVersion, command, "{}", [], null, null);
        return specification.JobFrom(command, new SettingsMaterializer(specification, null, new(StringComparer.Ordinal)));
    }

    // Materialization creates fresh runtime identities for Review and reruns.
    // Rebind only those identities at acceptance, never recapture settings or
    // invoke extraction from the new runtime tree.
    internal SubmissionSpecification BindAcceptedJobIds(Job root)
    {
        NormalizedJobCommand command = BindCommandJobIds(Command, root);
        return ReferenceEquals(command, Command)
            ? this
            : this with { Command = command };
    }

    private static NormalizedJobCommand BindCommandJobIds(NormalizedJobCommand command, Job job)
    {
        if (command.Kind != Kind(job))
            throw new InvalidOperationException("The accepted job does not match its retained command kind.");

        NormalizedJobCommand? result = command.ExtractedResult;
        if (result != null)
        {
            if (job is not ExtractJob { Result: { } extracted })
                throw new InvalidOperationException("The accepted extraction is missing its retained result.");
            result = BindCommandJobIds(result, extracted);
        }

        NormalizedJobCommand[]? children = null;
        if (command.Children is { } retainedChildren)
        {
            if (job is not JobList list || list.Count != retainedChildren.Count)
                throw new InvalidOperationException("The accepted job list does not match its retained children.");
            for (int index = 0; index < retainedChildren.Count; index++)
            {
                NormalizedJobCommand child = BindCommandJobIds(retainedChildren[index], list[index]);
                if (!ReferenceEquals(child, retainedChildren[index]))
                    (children ??= retainedChildren.ToArray())[index] = child;
            }
        }

        return command.AcceptedJobId == job.Id
            && ReferenceEquals(result, command.ExtractedResult) && children == null
            ? command
            : command with
            {
                AcceptedJobId = job.Id,
                ExtractedResult = result,
                Children = children ?? command.Children,
            };
    }

    public SubmissionSpecification? ForSearchJob(Guid acceptedJobId)
    {
        Validate();
        NormalizedJobCommand? command = FindCommand(Command, acceptedJobId);
        if (command == null)
            return null;
        RetainedJobSettings? planned = GetPlannedSettings(command);
        if (command.Kind != JobSnapshotKind.Search || planned == null)
        {
            throw new ArgumentException(
                $"Accepted job {acceptedJobId} is not a replayable planned search.");
        }
        return new SubmissionSpecification(
            CurrentSchemaVersion,
            command,
            planned.Json,
            planned.CredentialBindings,
            command.SearchDefinition,
            SourceRevision: null) { SharedSettings = SharedSettings };
    }

    private static NormalizedJobCommand? FindCommand(NormalizedJobCommand command, Guid acceptedJobId)
    {
        if (command.AcceptedJobId == acceptedJobId)
            return command;
        if (command.ExtractedResult is { } result)
        {
            NormalizedJobCommand? found = FindCommand(result, acceptedJobId);
            if (found != null)
                return found;
        }
        if (command.Children != null)
        {
            foreach (NormalizedJobCommand child in command.Children)
            {
                NormalizedJobCommand? found = FindCommand(child, acceptedJobId);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    public void Validate() => _ = ReadValidatedSettings();

    internal void Validate(Dictionary<string, DownloadSettings> validatedSettings)
        => _ = ReadValidatedSettings(validatedSettings);

    private Dictionary<string, DownloadSettings> ReadValidatedSettings(
        Dictionary<string, DownloadSettings>? validatedSettings = null)
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported submission-specification schema version {SchemaVersion}.");
        ArgumentNullException.ThrowIfNull(Command);
        // An operation owns these unmodified prototypes. Repeated JSON is
        // decoded once; materialization clones it before rebinding per-command
        // credentials or exposing mutable settings to a job. Nothing is cached
        // across submissions or calls.
        var settings = validatedSettings ?? new Dictionary<string, DownloadSettings>(StringComparer.Ordinal);
        if (SharedSettings != null)
        {
            foreach (RetainedJobSettings retained in SharedSettings)
            {
                if (retained == null || retained.Json == null || retained.CredentialBindings == null)
                    throw new InvalidDataException("Shared submission settings are incomplete.");
                _ = ReadSettings(retained.Json, settings);
            }
        }
        ValidateCommand(Command, settings);
        _ = ReadSettings(EffectiveSettingsJson, settings);
        Search?.Validate();
        return settings;
    }

    internal RetainedJobSettings? GetPlannedSettings(NormalizedJobCommand command)
    {
        if (command.PlannedSettingsRef is int reference)
        {
            if (SharedSettings == null
                || reference < 0 || reference >= SharedSettings.Count
                || command.PlannedEffectiveSettingsJson != null || command.PlannedCredentialBindings != null)
                throw new InvalidDataException("The command has an invalid or ambiguous planned-settings reference.");
            return SharedSettings[reference];
        }
        return command.PlannedEffectiveSettingsJson is { } json
            ? new(json, command.PlannedCredentialBindings ?? []) : null;
    }

    // This is the ownership boundary for captured input. Reuse immutable Core
    // values and freeze mutable collections once; planning must not recopy the
    // resulting payload just to keep it independent of the runtime Job.
    internal static NormalizedJobCommand CommandFrom(Job job, bool includeDescendants = true,
        RetainedSettingsWriter? writer = null)
    {
        var command = new NormalizedJobCommand
        {
            Kind = Kind(job),
            AcceptedJobId = job.Id,
            ItemName = job.ItemName,
            ItemNumber = job.ItemNumber,
            LineNumber = job.LineNumber,
            SourceMutation = job.SourceMutation,
            SourceInputType = job.SourceInputType,
            SourceInfo = job.SourceInfo,
            SourceCollectionInputType = job.SourceCollectionInputType,
            EnablesIndexByDefault = job.EnablesIndexByDefault,
            CanBeSkippedOverride = job.CanBeSkippedOverride,
            DownloadBehavior = job.DownloadBehaviorPolicy,
            ExtractorConditions = job.ExtractorCond?.Clone(),
            ExtractorPreferredConditions = job.ExtractorPrefCond?.Clone(),
            ExtractorFolderConditions = job.ExtractorFolderCond?.Clone(),
            ExtractorPreferredFolderConditions = job.ExtractorPrefFolderCond?.Clone(),
            ArtifactId = job.ArtifactId,
            ExtractorInfo = job.ExtractorInfo,
        };
        NormalizedJobCommand normalized = job switch
        {
            ExtractJob extract => command with
            {
                Input = extract.Input,
                InputType = extract.InputType,
                RequestedMode = extract.RequestedModeOverride,
                ResultDownloadBehavior = extract.ResultDownloadBehaviorPolicy,
                HasPlannedExtraction = includeDescendants && extract.HasPlannedExtraction,
                PlannedExtractionFailure = includeDescendants ? extract.PlannedExtractionFailure : null,
                ExtractedResult = includeDescendants && extract.HasPlannedExtraction && extract.Result != null
                    ? CommandFrom(extract.Result, writer: writer)
                    : null,
            },
            SearchJob search when search.DefaultFolderProjection is { } folder => command with
            {
                QueryText = search.QueryText,
                AlbumQuery = AlbumQueryDefinition.From(folder.Query),
                SearchDefinition = CaptureSearch(search.Definition),
            },
            SearchJob search when search.DefaultFileProjection is { } file => command with
            {
                QueryText = search.QueryText,
                SongQuery = SongQueryDefinition.From(file.Query),
                IncludeFullResults = file.IncludeFullResults,
                SearchDefinition = CaptureSearch(search.Definition),
            },
            SearchJob search => command with
            {
                QueryText = search.QueryText,
                SearchDefinition = CaptureSearch(search.Definition),
            },
            SongJob song => command with
            {
                SongQuery = SongQueryDefinition.From(song.Query),
                SongCandidates = song.Candidates is null ? null
                    : SnapshotCollections.Freeze(song.Candidates.Select(CaptureCandidate)),
                SelectedSong = song.ResolvedTarget is { } selected ? CaptureCandidate(selected) : null,
                PeerFile = song.ExactTarget,
            },
            AlbumJob album => CaptureAlbum(command, album),
            AggregateJob aggregate => command with { SongQuery = SongQueryDefinition.From(aggregate.Query) },
            AlbumAggregateJob aggregate => command with { AlbumQuery = AlbumQueryDefinition.From(aggregate.Query) },
            JobList list => command with
            {
                Children = includeDescendants
                    ? CaptureChildren(list, writer)
                    : [],
            },
            RemoteFileJob remote => command with
            {
                PeerFile = remote.Target,
                OutputPathComponents = remote.OutputPath.Components,
            },
            RemoteDirectoryJob remote when remote.Source is RemoteDirectorySource.PeerDirectory peer => command with
            {
                PeerDirectory = new(peer.Directory.Username, peer.Directory.FolderPath),
                ExcludedDirectoryFilenames = SnapshotCollections.Freeze(peer.ExcludedFilenames.Order(StringComparer.Ordinal)),
            },
            RemoteDirectoryJob remote when remote.Source is RemoteDirectorySource.Resolved resolved => command with
            {
                DirectoryPlan = CoreSnapshotFactory.CreateDirectoryTransferPlan(resolved.Plan),
            },
            RetrieveFolderJob retrieve => command with
            {
                PeerDirectory = new(
                    retrieve.Directory.Username,
                    retrieve.Directory.FolderPath),
            },
            _ => throw new NotSupportedException($"Job type '{job.GetType().Name}' has no normalized command."),
        };
        // A shallow node already carries its resolved settings in the enclosing
        // specification. Only a retained tree needs settings on child commands;
        // capturing them here for a preview just serializes a second copy that
        // its storage adapter immediately discards.
        if (includeDescendants && job.PlannedSettings is { } planned)
        {
            RetainedJobSettings retained = planned.Retain(writer);
            normalized = normalized with
            {
                PlannedEffectiveSettingsJson = retained.Json,
                PlannedCredentialBindings = retained.CredentialBindings,
            };
        }
        return normalized;
    }

    private static IReadOnlyList<NormalizedJobCommand> CaptureChildren(JobList list, RetainedSettingsWriter? writer)
    {
        var children = new NormalizedJobCommand[list.Jobs.Count];
        for (int index = 0; index < children.Length; index++)
            children[index] = CommandFrom(list.Jobs[index], writer: writer);
        return Array.AsReadOnly(children);
    }

    private Job JobFrom(
        NormalizedJobCommand command,
        SettingsMaterializer settings)
    {
        Job job = command.Kind switch
        {
            JobSnapshotKind.Extract => new ExtractJob(
                command.Input!,
                command.InputType)
            {
                RequestedModeOverride = command.RequestedMode,
                ResultDownloadBehaviorPolicy = command.ResultDownloadBehavior,
                HasPlannedExtraction = command.HasPlannedExtraction,
                PlannedExtractionFailure = command.PlannedExtractionFailure,
                Result = command.ExtractedResult == null
                    ? null
                    : JobFrom(command.ExtractedResult, settings),
            },
            JobSnapshotKind.Search when command.AlbumQuery != null =>
                new SearchJob(command.AlbumQuery.ToQuery()),
            JobSnapshotKind.Search when command.SongQuery != null =>
                new SearchJob(command.SongQuery.ToQuery(), command.IncludeFullResults),
            JobSnapshotKind.Search => new SearchJob(command.QueryText!),
            JobSnapshotKind.Song => new SongJob(command.SongQuery!.ToQuery()),
            JobSnapshotKind.Album => new AlbumJob(command.AlbumQuery!.ToQuery()),
            JobSnapshotKind.Aggregate => new AggregateJob(command.SongQuery!.ToQuery()),
            JobSnapshotKind.AlbumAggregate => new AlbumAggregateJob(command.AlbumQuery!.ToQuery()),
            JobSnapshotKind.JobList => new JobList(
                command.ItemName,
                command.Children!.Select(child => JobFrom(child, settings))),
            JobSnapshotKind.RetrieveFolder => new RetrieveFolderJob(
                new PeerDirectoryIdentity(
                    command.PeerDirectory!.Username,
                    command.PeerDirectory.FolderPath)),
            JobSnapshotKind.RemoteFile => new RemoteFileJob(
                command.PeerFile!,
                new RelativeOutputPath(command.OutputPathComponents!)),
            JobSnapshotKind.RemoteDirectory when command.PeerDirectory != null =>
                new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                    new PeerDirectoryIdentity(
                        command.PeerDirectory.Username,
                        command.PeerDirectory.FolderPath), command.ExcludedDirectoryFilenames)),
            JobSnapshotKind.RemoteDirectory => new RemoteDirectoryJob(
                new RemoteDirectorySource.Resolved(command.ResolvedDirectoryPlan ?? DirectoryPlan(command.DirectoryPlan!))),
            _ => throw new NotSupportedException($"Normalized command kind '{command.Kind}' is not executable."),
        };
        job.ItemName = command.ItemName;
        job.ItemNumber = command.ItemNumber;
        job.LineNumber = command.LineNumber;
        job.SourceMutation = command.SourceMutation;
        job.SourceInputType = command.SourceInputType;
        job.SourceInfo = command.SourceInfo;
        job.SourceCollectionInputType = command.SourceCollectionInputType;
        job.EnablesIndexByDefault = command.EnablesIndexByDefault;
        job.CanBeSkippedOverride = command.CanBeSkippedOverride;
        job.DownloadBehaviorPolicy = command.DownloadBehavior;
        job.ExtractorCond = command.ExtractorConditions;
        job.ExtractorPrefCond = command.ExtractorPreferredConditions;
        job.ExtractorFolderCond = command.ExtractorFolderConditions;
        job.ExtractorPrefFolderCond = command.ExtractorPreferredFolderConditions;
        job.ArtifactId = command.ArtifactId;
        job.ExtractorInfo = command.ExtractorInfo;
        job.PlannedSettings = settings.ForCommand(command);
        if (job is SearchJob searchJob && command.SearchDefinition != null)
            searchJob.Definition = command.SearchDefinition;
        if (job is SongJob song)
        {
            song.Candidates = command.SongCandidates?.Select(candidate => candidate.ToFileCandidate()).ToList();
            song.ResolvedTarget = command.SelectedSong?.ToFileCandidate();
            song.ExactTarget = command.PeerFile;
        }
        if (job is AlbumJob album)
        {
            album.Results = command.AlbumOptions?.Select(RestoreAlbumFolder).ToList() ?? [];
            album.ResolvedTarget = command.SelectedAlbumOption is int selected ? album.Results[selected]
                : command.SelectedAlbum is { } folder ? RestoreAlbumFolder(folder) : null;
            album.DirectoryResolutionPolicy = command.AlbumResolutionPolicy ?? AlbumDirectoryResolutionPolicy.CompleteIfNeeded;
            album.ValidationRequirement = command.AlbumValidation ?? AlbumValidationRequirement.Standard;
        }
        return job;
    }

    // One restoration owns parsed prototypes and credential bindings. Review
    // assembles inline settings before writing compact refs; both forms must
    // share frozen values here, without serializing and parsing a second tree.
    private sealed class SettingsMaterializer(
        SubmissionSpecification specification,
        DownloadSettings? credentials,
        Dictionary<string, DownloadSettings> prototypes)
    {
        private Dictionary<int, JobSettingsSnapshot>? references;
        private Dictionary<DownloadSettings, List<(IReadOnlyList<string> Bindings, JobSettingsSnapshot Value)>>? inline;

        public DownloadSettings ReadRoot() => RestoreSettings(specification.EffectiveSettingsJson,
            specification.CredentialBindings, credentials, prototypes);

        public JobSettingsSnapshot? ForCommand(NormalizedJobCommand command)
        {
            if (command.PlannedSettingsRef is int reference)
            {
                references ??= [];
                if (!references.TryGetValue(reference, out var value))
                    references.Add(reference, value = Restore(specification.GetPlannedSettings(command)!));
                return value;
            }
            return command.PlannedEffectiveSettingsJson is { } json
                ? FromInline(json, command.PlannedCredentialBindings ?? []) : null;
        }

        private JobSettingsSnapshot FromInline(string json, IReadOnlyList<string> bindings)
        {
            var prototype = ReadSettings(json, prototypes);
            inline ??= new(ReferenceEqualityComparer.Instance);
            if (!inline.TryGetValue(prototype, out var matches))
                inline.Add(prototype, matches = []);
            foreach (var entry in matches)
                if (entry.Bindings.SequenceEqual(bindings, StringComparer.Ordinal)) return entry.Value;
            var restored = JobSettingsSnapshot.FromOwned(RestoreSettings(
                prototype, bindings, credentials));
            matches.Add((bindings, restored));
            return restored;
        }

        private JobSettingsSnapshot Restore(RetainedJobSettings settings)
            => JobSettingsSnapshot.FromOwned(RestoreSettings(settings.Json,
                settings.CredentialBindings, credentials, prototypes));
    }

    private static SearchDefinition? CaptureSearch(SearchDefinition? definition)
    {
        if (definition is null) return null;
        static FileConditionsDefinition File(FileConditionsDefinition value) => value with
        {
            Formats = SnapshotCollections.Freeze(value.Formats),
            BannedUsers = SnapshotCollections.Freeze(value.BannedUsers),
            AllowedUsers = SnapshotCollections.Freeze(value.AllowedUsers),
        };
        static FolderConditionsDefinition Folder(FolderConditionsDefinition value) => value with
        {
            RequiredTrackTitles = SnapshotCollections.Freeze(value.RequiredTrackTitles),
        };
        return definition with
        {
            ProjectionSettings = definition.ProjectionSettings with
            {
                NecessaryFile = File(definition.ProjectionSettings.NecessaryFile),
                PreferredFile = File(definition.ProjectionSettings.PreferredFile),
                NecessaryFolder = Folder(definition.ProjectionSettings.NecessaryFolder),
                PreferredFolder = Folder(definition.ProjectionSettings.PreferredFolder),
            },
        };
    }

    private static SearchProjectionInput CaptureCandidate(FileCandidate candidate)
        => candidate.ToProjectionInput(candidate.Evidence.Sequence, candidate.Evidence.Revision, candidate.ObservedAtUtc);

    private static NormalizedJobCommand CaptureAlbum(NormalizedJobCommand command, AlbumJob album)
    {
        IReadOnlyList<AlbumFolder> options = album.Results;
        int? selected = null;
        if (album.ResolvedTarget is { } target)
        {
            int index = album.Results.IndexOf(target);
            if (index >= 0) selected = index;
        }
        return command with
        {
            AlbumQuery = AlbumQueryDefinition.From(album.Query),
            AlbumOptions = options.Count == 0 ? null : SnapshotCollections.Freeze(options
                .Select(folder => CoreSnapshotFactory.CreateAlbumFolder(folder, includeFiles: true))),
            SelectedAlbumOption = selected,
            SelectedAlbum = selected is null && album.ResolvedTarget is { } chosen
                ? CoreSnapshotFactory.CreateAlbumFolder(chosen, includeFiles: true) : null,
            AlbumResolutionPolicy = album.DirectoryResolutionPolicy,
            AlbumValidation = album.ValidationRequirement,
        };
    }

    private static AlbumFolder RestoreAlbumFolder(AlbumFolderSnapshot folder)
        => new(folder.Username, folder.FolderPath, () => folder.Files.Select(RestoreAlbumFile).ToList(),
            folder.SearchFileCount, folder.SearchAudioFileCount,
            folder.Files.Where(file => Utils.IsMusicFile(file.Filename))
                .Select(file => file.Candidate.Length ?? -1).Order().ToArray(),
            folder.Files.FirstOrDefault(file => Utils.IsMusicFile(file.Filename))?.Filename)
        { IsFullyRetrieved = folder.IsFullyRetrieved };

    private static AlbumFile RestoreAlbumFile(AlbumFileSnapshot file)
    {
        var query = new SongQuery
        {
            Artist = file.Query.Artist ?? "",
            Title = file.Query.Title ?? "",
            Album = file.Query.Album ?? "",
            URI = file.Query.URI ?? "",
            Length = file.Query.Length ?? -1,
            ArtistMaybeWrong = file.Query.ArtistMaybeWrong,
        };
        FileCandidateSnapshot candidate = file.Candidate;
        var input = new SearchProjectionInput(0, 0, candidate.Username, candidate.ResponseFileCount ?? 0,
            candidate.Filename, candidate.Size, candidate.BitRate, candidate.BitDepth,
            candidate.SampleRate, candidate.Length, candidate.Extension,
            candidate.Peer.UploadSpeed, candidate.Peer.HasFreeUploadSlot, candidate.Attributes,
            candidate.Peer.ObservedAtUtc ?? DateTimeOffset.UnixEpoch, candidate.Peer.QueueLength, candidate.Visibility);
        return new AlbumFile(query, input.ToFileCandidate());
    }

    private void ValidateCommand(
        NormalizedJobCommand command,
        Dictionary<string, DownloadSettings> settings)
    {
        bool valid = command.Kind switch
        {
            // A failed extractor is retained as an independent planned outcome,
            // including an empty source; replay must not reject its valid siblings.
            JobSnapshotKind.Extract => command.Input is not null
                && (command.Input.Length > 0
                    || command.HasPlannedExtraction && command.PlannedExtractionFailure is not null),
            JobSnapshotKind.Search => !string.IsNullOrWhiteSpace(command.QueryText),
            JobSnapshotKind.Song or JobSnapshotKind.Aggregate => command.SongQuery != null,
            JobSnapshotKind.Album or JobSnapshotKind.AlbumAggregate => command.AlbumQuery != null,
            JobSnapshotKind.JobList => command.Children != null,
            JobSnapshotKind.RetrieveFolder => command.PeerDirectory != null,
            JobSnapshotKind.RemoteFile => command.PeerFile != null
                && command.OutputPathComponents?.Count > 0,
            JobSnapshotKind.RemoteDirectory => (command.PeerDirectory != null)
                != (command.DirectoryPlan != null),
            _ => false,
        };
        if (!valid)
            throw new InvalidDataException($"Normalized {command.Kind} command is incomplete.");
        if ((command.SongCandidates != null || command.SelectedSong != null) && command.Kind != JobSnapshotKind.Song
            || (command.AlbumOptions != null || command.SelectedAlbumOption != null || command.SelectedAlbum != null
                || command.AlbumResolutionPolicy != null || command.AlbumValidation != null)
                && command.Kind != JobSnapshotKind.Album)
            throw new InvalidDataException("Accepted download inputs do not match the command kind.");
        if (command.SelectedAlbumOption is int selected
            && (command.AlbumOptions == null || selected < 0 || selected >= command.AlbumOptions.Count))
            throw new InvalidDataException("The selected album option is not in its accepted candidate list.");
        if (command.SelectedAlbumOption is not null && command.SelectedAlbum is not null)
            throw new InvalidDataException("An accepted album must have one selected target.");
        if (command.AlbumResolutionPolicy is { } policy && !Enum.IsDefined(policy)
            || command.AlbumValidation is { } validation && !Enum.IsDefined(validation))
            throw new InvalidDataException("The accepted album execution policy is invalid.");
        if (command.Children != null)
        {
            foreach (NormalizedJobCommand child in command.Children)
                ValidateCommand(child, settings);
        }
        if (command.ExtractedResult != null)
            ValidateCommand(command.ExtractedResult, settings);
        if (command.HasPlannedExtraction
            && command.PlannedExtractionFailure == null
            && command.ExtractedResult == null)
        {
            throw new InvalidDataException(
                "A successfully planned extraction has no retained result command.");
        }
        if (GetPlannedSettings(command) is { } planned)
            _ = ReadSettings(planned.Json, settings);
        command.SearchDefinition?.Validate();
    }

    internal static RetainedJobSettings RetainSettings(
        DownloadSettings effectiveSettings, RetainedSettingsWriter? writer = null)
    {
        var bindings = new List<string>();
        if (!string.IsNullOrEmpty(effectiveSettings.Spotify.ClientId)) bindings.Add("spotify.client-id");
        if (!string.IsNullOrEmpty(effectiveSettings.Spotify.ClientSecret)) bindings.Add("spotify.client-secret");
        if (!string.IsNullOrEmpty(effectiveSettings.Spotify.Token)) bindings.Add("spotify.token");
        if (!string.IsNullOrEmpty(effectiveSettings.Spotify.Refresh)) bindings.Add("spotify.refresh");
        if (!string.IsNullOrEmpty(effectiveSettings.YouTube.ApiKey)) bindings.Add("youtube.api-key");
        if (!string.IsNullOrEmpty(effectiveSettings.SoundCloud.AuthToken)) bindings.Add("soundcloud.auth-token");
        IReadOnlyList<string> slots = bindings.Count == 0 ? [] : Array.AsReadOnly(bindings.ToArray());
        return writer is null ? new(RetainedSettingsCodec.Serialize(effectiveSettings), slots)
            : writer.Write(effectiveSettings, slots);
    }

    private static DownloadSettings RestoreSettings(
        string json,
        IReadOnlyList<string> credentialBindings,
        DownloadSettings? credentialSource,
        Dictionary<string, DownloadSettings> prototypes)
        => RestoreSettings(ReadSettings(json, prototypes), credentialBindings, credentialSource);

    private static DownloadSettings RestoreSettings(
        DownloadSettings prototype,
        IReadOnlyList<string> credentialBindings,
        DownloadSettings? credentialSource)
    {
        DownloadSettings settings = SettingsCloner.Clone(prototype);
        if (credentialSource == null)
            return settings;
        if (credentialBindings.Contains("spotify.client-id"))
            settings.Spotify.ClientId = credentialSource.Spotify.ClientId;
        if (credentialBindings.Contains("spotify.client-secret"))
            settings.Spotify.ClientSecret = credentialSource.Spotify.ClientSecret;
        if (credentialBindings.Contains("spotify.token"))
            settings.Spotify.Token = credentialSource.Spotify.Token;
        if (credentialBindings.Contains("spotify.refresh"))
            settings.Spotify.Refresh = credentialSource.Spotify.Refresh;
        if (credentialBindings.Contains("youtube.api-key"))
            settings.YouTube.ApiKey = credentialSource.YouTube.ApiKey;
        if (credentialBindings.Contains("soundcloud.auth-token"))
            settings.SoundCloud.AuthToken = credentialSource.SoundCloud.AuthToken;
        return settings;
    }

    private static DownloadSettings ReadSettings(
        string json,
        Dictionary<string, DownloadSettings> prototypes)
    {
        if (!prototypes.TryGetValue(json, out DownloadSettings? settings))
        {
            // Preview commands refer to separately spooled settings and use
            // this canonical empty value. Its validated meaning is precisely
            // the ordinary defaults; no JSON metadata is needed to construct it.
            settings = json == "{}" ? new DownloadSettings() : RetainedSettingsCodec.Deserialize(json);
            prototypes.Add(json, settings);
        }
        return settings;
    }

    private static JobSnapshotKind Kind(Job job) => job switch
    {
        ExtractJob => JobSnapshotKind.Extract,
        SearchJob => JobSnapshotKind.Search,
        SongJob => JobSnapshotKind.Song,
        AlbumJob => JobSnapshotKind.Album,
        AggregateJob => JobSnapshotKind.Aggregate,
        AlbumAggregateJob => JobSnapshotKind.AlbumAggregate,
        JobList => JobSnapshotKind.JobList,
        RetrieveFolderJob => JobSnapshotKind.RetrieveFolder,
        RemoteFileJob => JobSnapshotKind.RemoteFile,
        RemoteDirectoryJob => JobSnapshotKind.RemoteDirectory,
        _ => JobSnapshotKind.Generic,
    };

    private static DirectoryTransferPlan DirectoryPlan(DirectoryTransferPlanSnapshot plan)
        => new(
            plan.DisplayRoot,
            plan.Entries.Select(entry => new DirectoryTransferEntry(
                entry.Target,
                entry.RelativeDirectoryComponents)).ToArray(),
            plan.RepresentsDirectorySelection,
            plan.SourceDirectory == null ? null : new PeerDirectoryIdentity(
                plan.SourceDirectory.Username, plan.SourceDirectory.FolderPath));
}

public static class SubmissionSpecificationCodec
{
    private static readonly JsonSerializerOptions Options = new(RetainedSettingsCodec.CreateOptions())
    {
        // Commands retain immutable facts as well as init-only records. Settings
        // keep their own policy for excluding computed read-only properties.
        IgnoreReadOnlyProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(SubmissionSpecification specification)
        => Serialize(specification, validatedSettings: null);

    internal static string Serialize(SubmissionSpecification specification,
        Dictionary<string, DownloadSettings>? validatedSettings)
    {
        if (validatedSettings is null) specification.Validate();
        else specification.Validate(validatedSettings);
        specification = CompactSettings(specification);
        return JsonSerializer.Serialize(specification, Options);
    }

    private static SubmissionSpecification CompactSettings(SubmissionSpecification specification)
    {
        // Shallow preview nodes have no per-command settings to intern. They
        // still pass normal validation; don't rebuild an already compact leaf.
        var root = specification.Command;
        if (root.PlannedEffectiveSettingsJson is null && root.PlannedCredentialBindings is null
            && root.PlannedSettingsRef is null && root.Children is null or { Count: 0 }
            && root.ExtractedResult is null)
            return specification.SharedSettings is null ? specification : specification with { SharedSettings = null };

        // The table belongs to this immutable command, not a cache or a shared
        // database resource. Equal JSON with different secret bindings must
        // remain distinct. Compact again when serializing a retained subtree so
        // it does not carry unrelated siblings' settings into its new command.
        var shared = new List<RetainedJobSettings>();
        var byJson = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        NormalizedJobCommand Compact(NormalizedJobCommand command)
        {
            int? reference = null;
            if (specification.GetPlannedSettings(command) is { } planned)
            {
                if (!byJson.TryGetValue(planned.Json, out List<int>? matches))
                    byJson.Add(planned.Json, matches = []);
                foreach (int index in matches)
                {
                    if (shared[index].CredentialBindings.SequenceEqual(planned.CredentialBindings, StringComparer.Ordinal))
                    {
                        reference = index;
                        break;
                    }
                }
                if (reference == null)
                {
                    reference = shared.Count;
                    matches.Add(reference.Value);
                    shared.Add(planned);
                }
            }
            return command with
            {
                PlannedEffectiveSettingsJson = null,
                PlannedCredentialBindings = null,
                PlannedSettingsRef = reference,
                Children = command.Children?.Select(Compact).ToArray(),
                ExtractedResult = command.ExtractedResult is { } result ? Compact(result) : null,
            };
        }
        NormalizedJobCommand compact = Compact(specification.Command);
        return specification with { Command = compact, SharedSettings = shared.Count == 0 ? null : shared.ToArray() };
    }

    public static SubmissionSpecification Deserialize(string json)
        => Deserialize(json, new Dictionary<string, DownloadSettings>(StringComparer.Ordinal));

    internal static SubmissionSpecification Deserialize(
        string json, Dictionary<string, DownloadSettings> validatedSettings)
    {
        SubmissionSpecification specification =
            JsonSerializer.Deserialize<SubmissionSpecification>(json, Options)
            ?? throw new InvalidDataException("Submission specification is empty.");
        specification.Validate(validatedSettings);
        return specification;
    }
}

internal static class RetainedSettingsCodec
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(DownloadSettings settings)
        => JsonSerializer.Serialize(settings, RetainedSettingsJsonContext.Default.DownloadSettings);

    internal static void Serialize(Utf8JsonWriter writer, DownloadSettings settings)
        => JsonSerializer.Serialize(writer, settings, RetainedSettingsJsonContext.Default.DownloadSettings);

    public static DownloadSettings Deserialize(string json)
        => JsonSerializer.Deserialize(json, RetainedSettingsJsonContext.Default.DownloadSettings)
            ?? throw new InvalidDataException("Retained effective settings are empty.");
}

// One capture/batch owns the scratch buffers and distinct retained values.
// Look up JSON through a span before allocating a string: large plans often
// repeat identical settings. No cache or mutable settings escape this operation.
internal sealed class RetainedSettingsWriter : IDisposable
{
    private readonly ArrayBufferWriter<byte> buffer = new();
    private readonly Utf8JsonWriter writer;
    private readonly Dictionary<string, List<RetainedJobSettings>> values = new(StringComparer.Ordinal);
    private char[]? characters;
    private RetainedJobSettings? previous;
    public RetainedSettingsWriter()
        => writer = new(buffer, new JsonWriterOptions { SkipValidation = true });

    public RetainedJobSettings Write(DownloadSettings settings, IReadOnlyList<string> bindings)
    {
        buffer.Clear();
        writer.Reset(buffer);
        RetainedSettingsCodec.Serialize(writer, settings);
        writer.Flush();
        if (characters is null || characters.Length < buffer.WrittenCount)
        {
            if (characters is not null) ArrayPool<char>.Shared.Return(characters, clearArray: true);
            characters = ArrayPool<char>.Shared.Rent(buffer.WrittenCount);
        }
        int length = Encoding.UTF8.GetChars(buffer.WrittenSpan, characters);
        ReadOnlySpan<char> json = characters.AsSpan(0, length);
        if (previous is not null && json.SequenceEqual(previous.Json)
            && previous.CredentialBindings.SequenceEqual(bindings, StringComparer.Ordinal)) return previous;
        if (values.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(json, out var matches))
        {
            foreach (var value in matches)
                if (value.CredentialBindings.SequenceEqual(bindings, StringComparer.Ordinal)) return previous = value;
            var distinct = new RetainedJobSettings(matches[0].Json, bindings);
            matches.Add(distinct);
            return previous = distinct;
        }
        var retained = new RetainedJobSettings(new string(json), bindings);
        values.Add(retained.Json, [retained]);
        return previous = retained;
    }

    // Preview capture clears each persisted batch rather than retaining every
    // distinct settings value in a potentially large streamed plan.
    public void Clear()
    {
        values.Clear();
        previous = null;
    }

    public void Dispose()
    {
        writer.Dispose();
        if (characters is not null)
        {
            ArrayPool<char>.Shared.Return(characters, clearArray: true);
            characters = null;
        }
    }
}

// One model-derived codec for single and batch capture and settings reads.
// Credentials can be rebound on read but are never serialized.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    IncludeFields = true, IgnoreReadOnlyProperties = true, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DownloadSettings))]
internal partial class RetainedSettingsJsonContext : JsonSerializerContext;
