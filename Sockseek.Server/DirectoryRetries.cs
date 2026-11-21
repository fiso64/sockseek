using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server;

public sealed partial class JobSubmissions
{
    private readonly record struct RetryMember(PeerFileTarget Target, IReadOnlyList<string> RelativeDirectoryComponents,
        Guid? ChildJobId, SongQuerySnapshot? Query);

    private sealed record RetrySelection(PersistedJob Directory, string DisplayRoot,
        List<RetryMember> Entries, Dictionary<string, string> Completed,
        Dictionary<string, string> PathsBeforeMove, DirectoryRetryClaim Claim, DownloadSettings Settings, NormalizedJobCommand Command, bool Succeeded)
    {
        public int Missing => Entries.Count - Completed.Count;
    }

    internal async Task<JobSummaryDto?> RetryDirectoryAsync(Guid jobId, Guid? memberJobId, string? filename,
        Guid? transferId, CancellationToken ct, bool recovering = false)
    {
        if (persistence?.JobHistory == null) throw new InvalidOperationException("Folder retry requires retained history.");
        await persistence.WaitForRetryHistoryAsync(StateStore, jobId, ct);
        var owner = await persistence.JobHistory.GetJobAsync(jobId, ct);
        if (owner == null) return null;
        if (owner.SubmissionId is not Guid submission || owner.LifecycleState != "Terminal")
            throw new InvalidOperationException("The original folder is still running or its settings are unavailable.");
        var json = await persistence.Submissions!.GetSpecificationJsonAsync(submission, ct)
            ?? throw new InvalidOperationException("The original settings are unavailable.");
        var selection = await ReadSelectionAsync(owner, SubmissionSpecificationCodec.Deserialize(json), memberJobId, ct);
        bool wasComplete = selection is { Missing: 0, Succeeded: true };
        if (!recovering && filename != null && transferId.HasValue)
            selection?.Completed.Remove(filename);
        if (selection == null || selection.Missing == 0 || filename != null &&
            (selection.Completed.ContainsKey(filename) || !selection.Entries.Any(e => e.Target.Filename == filename)))
            throw new InvalidOperationException("This selection has no matching unfinished files or already has an active retry.");
        if (!CanAcceptLoginRequiredJobs()) throw new InvalidOperationException("Configure Soulseek login before retrying.");

        HashSet<string>? requested = filename == null ? null : new(StringComparer.Ordinal) { filename };
        if (recovering)
        {
            // A restart resumes only the interrupted transfers in this attempt.
            // Older failures and siblings which never started stay untouched.
            var pending = await persistence.TransferHistory!.GetPendingDirectoryFilesAsync(jobId,
                selection.Entries.Select(e => e.ChildJobId).OfType<Guid>().ToArray(), ct);
            requested = pending.ToHashSet(StringComparer.Ordinal);
            if (requested.Count == 0) throw new InvalidOperationException("These transfers are no longer awaiting recovery.");
        }
        var job = CreateDirectoryRetry(selection, requested, wasComplete);
        var missingChildren = selection.Entries.Where(e => !selection.Completed.ContainsKey(e.Target.Filename)
                && (requested == null || requested.Contains(e.Target.Filename)))
            .Select(e => e.ChildJobId).OfType<Guid>().ToArray();
        await AcceptAsync(job, selection.Settings, selection.Claim.DirectoryId, ct, settingsAreFinal: true,
            retryTransferId: transferId, directoryRetry: selection.Claim with { RetriedMembers = missingChildren });
        StateStore.MarkTransferHistoryChanged();
        return StateStore.GetJobSummary(job.Id) ?? ServerSnapshotMapper.ToSubmittedJobSummary(job, selection.Claim.DirectoryId);
    }

    private async Task<RetrySelection?> ReadSelectionAsync(PersistedJob owner, SubmissionSpecification spec,
        Guid? memberJobId, CancellationToken ct)
    {
        var history = persistence!.JobHistory!;
        var previous = spec.Command.DirectoryRetry;
        Guid original = previous?.SourceDirectoryId ?? owner.Id;
        int? attempt = previous?.SourceAttempt;
        if (attempt == null && memberJobId.HasValue)
        {
            attempt = await history.GetDirectoryAttemptForChildAsync(original, memberJobId.Value, ct);
            if (attempt == null) return null;
        }
        var manifest = await history.GetDirectoryManifestAsync(original, attempt, -1, 1024, ct);
        if (manifest == null) return null;
        var claim = new DirectoryRetryClaim(original, manifest.AttemptNumber, manifest.RetryJobId);
        var (settings, command) = spec.MaterializeMemberSettings(owner.Id, owner.ParentJobId, defaultDownloadSettings);

        // The latest continuation carries the whole selection, including completed
        // and deferred members. Read it before deciding which files to schedule.
        Guid current = original;
        bool succeeded = false;
        if (manifest.RetryJobId is Guid latest)
        {
            await persistence.WaitForRetryHistoryAsync(StateStore, latest, ct);
            var latestJob = await history.GetJobAsync(latest, ct);
            if (latestJob?.LifecycleState != "Terminal")
                return null;
            succeeded = latestJob.TerminalOutcome == "Succeeded";
            var latestManifest = await history.GetDirectoryManifestAsync(latest, null, -1, 1024, ct);
            if (latestManifest != null)
            {
                current = latest;
                manifest = latestManifest;
            }
        }
        var members = new List<RetryMember>();
        // Early album manifests used different orders for children and entries.
        // Retain only retry facts from each page, keyed by the actual wire identity.
        var saved = new Dictionary<PeerFileIdentity, (Guid Id, SongQuerySnapshot? Query, FileDownloadStateSnapshot? Completed)>();
        var page = manifest;
        while (true)
        {
            foreach (var entry in page.Items)
            {
                // Supplemental artwork is outside the accepted directory plan.
                if (entry.Ordinal >= manifest.EntryCount) break;
                if (entry.Target == null) return null;
                var child = entry.Child;
                var payload = child == null ? null : RetainedJobPayloadCodec.Read(child);
                (PeerFileIdentity? identity, SongQuerySnapshot? query, FileDownloadStateSnapshot? file) = payload switch
                {
                    SongJobSnapshotPayload song => (song.ResolvedTarget is { } target
                        ? new PeerFileIdentity(target.Username, target.Filename) : song.ExactTarget?.Identity, song.Query, song.File),
                    RemoteFileJobSnapshotPayload remote => (remote.Target.Identity, null, remote.File),
                    _ => (null, null, null),
                };
                members.Add(new(entry.Target, entry.RelativeDirectoryComponents, entry.ChildJobId,
                    identity == null ? query : null));
                if (identity == null || child == null) continue;
                bool complete = child.TerminalOutcome == "Succeeded"
                    || child is { TerminalOutcome: "Skipped", SkipReason: "AlreadyExists" };
                saved[identity] = (child.Id, query, complete ? file : null);
            }
            if (page.NextOrdinal is not int after || after >= manifest.EntryCount - 1) break;
            page = await history.GetDirectoryManifestAsync(current, manifest.AttemptNumber, after, 1024, ct);
            if (page == null) return null;
        }
        if (members.Count == 0) return null;
        var completed = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathsBeforeMove = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < members.Count; index++)
        {
            var member = members[index];
            if (!saved.TryGetValue(member.Target.Identity, out var facts)) continue;
            members[index] = member with { ChildJobId = facts.Id, Query = facts.Query };
            if (facts.Completed?.DownloadPath is { } path && !OutputStaging.Contains(path, settings.Output) && File.Exists(path))
            {
                completed[member.Target.Filename] = path;
                if (facts.Completed.PathBeforeIncompleteMove is { } beforeMove)
                    pathsBeforeMove[member.Target.Filename] = beforeMove;
            }
        }
        return new(owner, manifest.DisplayRoot, members, completed, pathsBeforeMove, claim, settings, command, succeeded);
    }

    private static DirectoryDownloadJob CreateDirectoryRetry(RetrySelection selection, IReadOnlySet<string>? requested, bool wasComplete)
    {
        var first = selection.Entries[0];
        var target = first.Target;
        // Retained attempts can belong to an earlier candidate than the album's
        // final selected peer. The member's relative directory identifies its root.
        string remoteRoot = target.Filename;
        for (int i = 0; i <= first.RelativeDirectoryComponents.Count; i++)
        {
            int separator = remoteRoot.LastIndexOfAny(['/', '\\']);
            remoteRoot = separator < 0 ? "" : remoteRoot[..separator];
        }
        DirectoryDownloadJob job;
        if (RetainedJobPayloadCodec.Read(selection.Directory) is AlbumExecutionPayload album)
        {
            var query = album.Query.ToQuery();
            var hint = new SongQuery { Artist = query.Artist, Album = query.Album };
            var peer = new SearchPeerSnapshot(target.Username, 0, null, null);
            var files = selection.Entries.Select(entry => new AlbumFile(
                entry.Query?.ToQuery() ?? Searcher.InferSongQuery(entry.Target.Filename, hint),
                new FileCandidate(entry.Target, peer))).ToList();
            job = new AlbumJob(query)
            {
                ResolvedTarget = new AlbumFolder(target.Username, remoteRoot, files) { IsFullyRetrieved = true },
                ValidationRequirement = AlbumValidationRequirement.UserAccepted,
                DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot,
            };
        }
        else
        {
            var originalSource = selection.Command.PeerDirectory ?? selection.Command.DirectoryPlan?.SourceDirectory;
            var plan = new DirectoryTransferPlan(selection.DisplayRoot,
                selection.Entries.Select(entry => new DirectoryTransferEntry(entry.Target, entry.RelativeDirectoryComponents)).ToArray(),
                representsDirectorySelection: selection.Command.DirectoryPlan?.RepresentsDirectorySelection ?? selection.DisplayRoot.Length > 0,
                sourceDirectory: originalSource == null ? null : new(originalSource.Username, originalSource.FolderPath));
            job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));
        }
        job.Retry = new(selection.Claim.DirectoryId, selection.Claim.Attempt, requested, selection.Completed, selection.PathsBeforeMove, wasComplete);
        job.ItemName = selection.Directory.ItemName;
        job.ItemNumber = selection.Command.ItemNumber;
        job.LineNumber = selection.Command.LineNumber;
        job.SourceInfo = selection.Command.SourceInfo;
        job.SelectionSource = selection.Command.SelectionSource;
        job.InheritedFolders = selection.Command.InheritedFolders ?? [];
        job.WorkflowId = selection.Directory.WorkflowId;
        return job;
    }
}
