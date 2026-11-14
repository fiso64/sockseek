using Sockseek.Core.Sharing;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Transfers.Uploads;

public enum UploadAdmissionResultKind
{
    Accepted,
    Duplicate,
    Rejected,
}

public enum UploadSchedulerEntryState
{
    Queued,
    Active,
}

public sealed record UploadAdmissionRequest(
    Guid TransferId,
    string Username,
    string RemotePath,
    RemotePathKey RemotePathKey,
    long SizeBytes,
    DateTimeOffset RequestedAtUtc);

public sealed record UploadSchedulerEntrySnapshot(
    Guid TransferId,
    string Username,
    string RemotePath,
    RemotePathKey RemotePathKey,
    long SizeBytes,
    DateTimeOffset RequestedAtUtc,
    UploadSchedulerEntryState State);

public sealed record UploadSchedulerGrant(UploadSchedulerEntrySnapshot Entry);

public sealed record UploadAdmissionResult(
    UploadAdmissionResultKind Kind,
    UploadSchedulerEntrySnapshot? Entry,
    IReadOnlyList<UploadSchedulerGrant> Grants);

public sealed record UploadSchedulerMutationResult(
    UploadSchedulerEntrySnapshot? Removed,
    IReadOnlyList<UploadSchedulerGrant> Grants);

public sealed record UploadQueueRuntimeSnapshot(
    long QueueRevision,
    int QueuedFiles,
    long QueuedBytes,
    int ActiveSlots,
    int TotalSlots,
    bool AcceptingUploads);

public sealed record UploadQueueEstimate(
    int? AheadCount,
    long QueueRevision);

public sealed record UploadQueuePage(
    IReadOnlyList<UploadSchedulerEntrySnapshot> Items,
    DateTimeOffset? NextRequestedAtUtc,
    Guid? NextTransferId,
    long ObservedQueueRevision,
    bool QueueChanged);

/// <summary>
/// Authoritative compact upload scheduler. It creates no Tasks for waiting
/// entries and grants work in strict round-robin order between users while
/// retaining FIFO order within each user.
/// </summary>
public sealed class UploadScheduler
{
    private readonly object sync = new();
    private int slots;
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Dictionary<DuplicateKey, Guid> duplicateIndex = [];
    private readonly Dictionary<string, UserState> users = new(StringComparer.Ordinal);
    private readonly LinkedList<string> readyUsers = [];
    private readonly HashSet<Guid> active = [];
    private readonly SortedSet<AdmissionKey> waitingByAdmission =
        new(AdmissionKeyComparer.Instance);

    private long queueRevision;
    private int queuedFiles;
    private long queuedBytes;

    public UploadScheduler(UploadSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Slots is < 1 or > SharingSettingsValidator.MaximumUploadSlots)
            throw new ArgumentOutOfRangeException(nameof(settings), "Upload slots are invalid.");

        slots = settings.Slots;
    }

    public UploadAdmissionResult Admit(UploadAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TransferId == Guid.Empty)
            throw new ArgumentException("Transfer ID cannot be empty.", nameof(request));
        if (request.SizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Upload size cannot be negative.");

        string username = PeerUsername.Validate(request.Username);
        var duplicateKey = new DuplicateKey(username, request.RemotePath);

        lock (sync)
        {
            if (duplicateIndex.TryGetValue(duplicateKey, out Guid duplicateId))
            {
                return new UploadAdmissionResult(
                    UploadAdmissionResultKind.Duplicate,
                    Snapshot(entries[duplicateId]),
                    []);
            }

            if (entries.ContainsKey(request.TransferId))
                throw new InvalidOperationException($"Transfer {request.TransferId} already exists.");

            UserState user = GetOrCreateUser(username);
            var entry = new Entry(
                request.TransferId,
                username,
                request.RemotePath,
                request.RemotePathKey,
                request.SizeBytes,
                request.RequestedAtUtc);

            entries.Add(entry.TransferId, entry);
            duplicateIndex.Add(duplicateKey, entry.TransferId);
            entry.UserQueueNode = user.Waiting.AddLast(entry.TransferId);
            waitingByAdmission.Add(entry.AdmissionKey);
            queuedFiles++;
            queuedBytes = checked(queuedBytes + entry.SizeBytes);
            user.OutstandingFiles++;
            queueRevision++;

            EnsureReady(user);
            var grants = TakeAvailableGrants();

            return new UploadAdmissionResult(
                UploadAdmissionResultKind.Accepted,
                Snapshot(entry),
                grants);
        }
    }

    public IReadOnlyList<UploadSchedulerGrant> SetSlots(int value)
    {
        if (value is < 1 or > SharingSettingsValidator.MaximumUploadSlots)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync)
        {
            if (slots == value) return [];
            slots = value;
            queueRevision++;
            // Lowering the limit lets active transfers finish; raising it
            // immediately admits waiting users through the normal scheduler.
            return TakeAvailableGrants();
        }
    }

    /// <summary>
    /// Removes a queued entry. Active work remains owned by the coordinator until
    /// its protocol operation exits and calls <see cref="Terminalize"/>.
    /// </summary>
    public UploadSchedulerMutationResult CancelQueued(Guid transferId)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(transferId, out var entry)
                || entry.State != UploadSchedulerEntryState.Queued)
            {
                return new UploadSchedulerMutationResult(null, []);
            }

            var snapshot = Snapshot(entry);
            RemoveEntry(entry);
            queueRevision++;
            return new UploadSchedulerMutationResult(snapshot, TakeAvailableGrants());
        }
    }

    /// <summary>
    /// Terminalizes active or queued work exactly once and returns newly available
    /// grants. A repeated/late terminal callback is a no-op.
    /// </summary>
    public UploadSchedulerMutationResult Terminalize(Guid transferId)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(transferId, out var entry))
                return new UploadSchedulerMutationResult(null, []);

            var snapshot = Snapshot(entry);
            UserState user = users[entry.Username];

            if (entry.State == UploadSchedulerEntryState.Active)
            {
                active.Remove(entry.TransferId);
                user.ActiveTransferId = null;
            }

            RemoveEntry(entry);
            queueRevision++;

            if (user.Waiting.Count > 0 && user.ActiveTransferId is null)
                EnsureReady(user);

            CleanupEmptyUser(user);
            return new UploadSchedulerMutationResult(snapshot, TakeAvailableGrants());
        }
    }

    public bool TryGet(Guid transferId, out UploadSchedulerEntrySnapshot? entry)
    {
        lock (sync)
        {
            if (entries.TryGetValue(transferId, out var found))
            {
                entry = Snapshot(found);
                return true;
            }

            entry = null;
            return false;
        }
    }

    public UploadQueueRuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (sync)
        {
            return new UploadQueueRuntimeSnapshot(
                queueRevision,
                queuedFiles,
                queuedBytes,
                active.Count,
                slots,
                true);
        }
    }

    public bool CouldStartImmediately(string username)
    {
        username = PeerUsername.Validate(username);
        lock (sync)
        {
            if (active.Count >= slots)
                return false;
            if (users.TryGetValue(username, out var user)
                && (user.ActiveTransferId is not null || user.Waiting.Count > 0))
            {
                return false;
            }

            // A newly admitted user is appended after all currently ready users.
            return readyUsers.Count < slots - active.Count;
        }
    }

    public UploadQueueEstimate Estimate(Guid transferId)
    {
        lock (sync)
            return EstimateLocked(transferId);
    }

    public UploadQueueEstimate Estimate(string username, string remotePath)
    {
        username = PeerUsername.Validate(username);
        lock (sync)
        {
            return duplicateIndex.TryGetValue(new DuplicateKey(username, remotePath), out Guid transferId)
                ? EstimateLocked(transferId)
                : new UploadQueueEstimate(null, queueRevision);
        }
    }

    private UploadQueueEstimate EstimateLocked(Guid transferId)
    {
        if (!entries.TryGetValue(transferId, out var target)
            || target.State != UploadSchedulerEntryState.Queued)
        {
            return new UploadQueueEstimate(
                null,
                queueRevision);
        }

        int localPosition = 0;
        UserState targetUser = users[target.Username];
        foreach (Guid id in targetUser.Waiting)
        {
            if (id == target.TransferId)
                break;
            localPosition++;
        }

        // This deliberately remains a cheap, best-effort queue hint. It
        // counts earlier files for the same user and users currently ahead
        // in the ready ring; transfer durations are unknowable.
        int readyAhead = 0;
        foreach (string username in readyUsers)
        {
            if (StringComparer.Ordinal.Equals(username, target.Username))
                break;
            readyAhead++;
        }
        return new UploadQueueEstimate(
            checked(localPosition + readyAhead),
            queueRevision);
    }

    public UploadQueuePage GetPage(
        DateTimeOffset? afterRequestedAtUtc,
        Guid? afterTransferId,
        int limit,
        long? previousQueueRevision = null,
        string? username = null)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        if (afterRequestedAtUtc.HasValue != afterTransferId.HasValue)
            throw new ArgumentException("Both live queue cursor fields must be supplied together.");
        if (username is not null)
            username = PeerUsername.Validate(username);

        lock (sync)
        {
            var items = new List<UploadSchedulerEntrySnapshot>();
            AdmissionKey? last = null;
            bool hasMore = false;
            AdmissionKey? after = afterRequestedAtUtc is { } time && afterTransferId is { } id
                ? new AdmissionKey(time, id)
                : null;

            foreach (var key in waitingByAdmission)
            {
                if (after is not null
                    && AdmissionKeyComparer.Instance.Compare(key, after.Value) <= 0)
                {
                    continue;
                }
                if (username is not null
                    && !StringComparer.Ordinal.Equals(entries[key.TransferId].Username, username))
                {
                    continue;
                }

                if (items.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                items.Add(Snapshot(entries[key.TransferId]));
                last = key;
            }

            return new UploadQueuePage(
                items,
                hasMore ? last?.RequestedAtUtc : null,
                hasMore ? last?.TransferId : null,
                queueRevision,
                previousQueueRevision is not null
                && previousQueueRevision != queueRevision);
        }
    }

    public UploadQueuePage GetNewestPage(
        DateTimeOffset? beforeRequestedAtUtc,
        Guid? beforeTransferId,
        int limit,
        string? username = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        if (beforeRequestedAtUtc.HasValue != beforeTransferId.HasValue)
            throw new ArgumentException("Both timeline cursor fields must be supplied together.");
        if (username is not null)
            username = PeerUsername.Validate(username);

        lock (sync)
        {
            var items = new List<UploadSchedulerEntrySnapshot>();
            AdmissionKey? last = null;
            bool hasMore = false;
            AdmissionKey? before = beforeRequestedAtUtc is { } time
                && beforeTransferId is { } id
                    ? new AdmissionKey(time, id)
                    : null;

            foreach (AdmissionKey key in waitingByAdmission.Reverse())
            {
                if (before is not null && CompareTimelineKey(key, before.Value) >= 0)
                    continue;
                Entry entry = entries[key.TransferId];
                if (username is not null
                    && !StringComparer.Ordinal.Equals(entry.Username, username))
                    continue;
                if (fromUtc is { } from && key.RequestedAtUtc < from)
                    break;
                if (toUtc is { } to && key.RequestedAtUtc > to)
                    continue;

                if (items.Count == limit)
                {
                    hasMore = true;
                    break;
                }
                items.Add(Snapshot(entry));
                last = key;
            }

            return new UploadQueuePage(
                items,
                hasMore ? last?.RequestedAtUtc : null,
                hasMore ? last?.TransferId : null,
                queueRevision,
                QueueChanged: false);
        }
    }

    private static int CompareTimelineKey(AdmissionKey left, AdmissionKey right)
    {
        int time = left.RequestedAtUtc.ToUnixTimeMilliseconds()
            .CompareTo(right.RequestedAtUtc.ToUnixTimeMilliseconds());
        return time != 0 ? time : left.TransferId.CompareTo(right.TransferId);
    }

    private IReadOnlyList<UploadSchedulerGrant> TakeAvailableGrants()
    {
        if (active.Count >= slots || readyUsers.Count == 0)
            return [];

        var grants = new List<UploadSchedulerGrant>(Math.Min(slots - active.Count, readyUsers.Count));

        while (active.Count < slots && readyUsers.First is { } readyNode)
        {
            string username = readyNode.Value;
            readyUsers.RemoveFirst();

            if (!users.TryGetValue(username, out var user)
                || user.ReadyNode != readyNode)
            {
                continue;
            }

            user.ReadyNode = null;
            if (user.ActiveTransferId is not null || user.Waiting.First is not { } transferNode)
                continue;

            user.Waiting.RemoveFirst();
            Entry entry = entries[transferNode.Value];
            entry.UserQueueNode = null;
            waitingByAdmission.Remove(entry.AdmissionKey);
            queuedFiles--;
            queuedBytes -= entry.SizeBytes;
            entry.State = UploadSchedulerEntryState.Active;
            user.ActiveTransferId = entry.TransferId;
            active.Add(entry.TransferId);
            queueRevision++;
            grants.Add(new UploadSchedulerGrant(Snapshot(entry)));
        }

        return grants;
    }

    private void RemoveEntry(Entry entry)
    {
        UserState user = users[entry.Username];

        if (entry.UserQueueNode is not null)
        {
            user.Waiting.Remove(entry.UserQueueNode);
            entry.UserQueueNode = null;
            waitingByAdmission.Remove(entry.AdmissionKey);
            queuedFiles--;
            queuedBytes -= entry.SizeBytes;
        }

        entries.Remove(entry.TransferId);
        duplicateIndex.Remove(new DuplicateKey(entry.Username, entry.RemotePath));
        user.OutstandingFiles--;

        if (user.Waiting.Count == 0 && user.ReadyNode is not null)
        {
            readyUsers.Remove(user.ReadyNode);
            user.ReadyNode = null;
        }
    }

    private UserState GetOrCreateUser(string username)
    {
        if (!users.TryGetValue(username, out var user))
        {
            user = new UserState(username);
            users.Add(username, user);
        }

        return user;
    }

    private void EnsureReady(UserState user)
    {
        if (user.ActiveTransferId is null
            && user.Waiting.Count > 0
            && user.ReadyNode is null)
        {
            user.ReadyNode = readyUsers.AddLast(user.Username);
        }
    }

    private void CleanupEmptyUser(UserState user)
    {
        if (user.OutstandingFiles == 0
            && user.ActiveTransferId is null
            && user.Waiting.Count == 0)
        {
            if (user.ReadyNode is not null)
                readyUsers.Remove(user.ReadyNode);
            users.Remove(user.Username);
        }
    }

    private static UploadSchedulerEntrySnapshot Snapshot(Entry entry)
        => new(
            entry.TransferId,
            entry.Username,
            entry.RemotePath,
            entry.RemotePathKey,
            entry.SizeBytes,
            entry.RequestedAtUtc,
            entry.State);

    private sealed class UserState(string username)
    {
        public string Username { get; } = username;
        public LinkedList<Guid> Waiting { get; } = [];
        public LinkedListNode<string>? ReadyNode { get; set; }
        public Guid? ActiveTransferId { get; set; }
        public int OutstandingFiles { get; set; }
    }

    private sealed class Entry(
        Guid transferId,
        string username,
        string remotePath,
        RemotePathKey remotePathKey,
        long sizeBytes,
        DateTimeOffset requestedAtUtc)
    {
        public Guid TransferId { get; } = transferId;
        public string Username { get; } = username;
        public string RemotePath { get; } = remotePath;
        public RemotePathKey RemotePathKey { get; } = remotePathKey;
        public long SizeBytes { get; } = sizeBytes;
        public DateTimeOffset RequestedAtUtc { get; } = requestedAtUtc;
        public UploadSchedulerEntryState State { get; set; } = UploadSchedulerEntryState.Queued;
        public LinkedListNode<Guid>? UserQueueNode { get; set; }
        public AdmissionKey AdmissionKey => new(RequestedAtUtc, TransferId);
    }

    private readonly record struct DuplicateKey(string Username, string RemotePath);

    private readonly record struct AdmissionKey(DateTimeOffset RequestedAtUtc, Guid TransferId);

    private sealed class AdmissionKeyComparer : IComparer<AdmissionKey>
    {
        public static AdmissionKeyComparer Instance { get; } = new();

        public int Compare(AdmissionKey x, AdmissionKey y)
        {
            int time = x.RequestedAtUtc.CompareTo(y.RequestedAtUtc);
            return time != 0 ? time : x.TransferId.CompareTo(y.TransferId);
        }
    }
}
