using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

/// <summary>
/// Owns output I/O and duplicate publication for an engine run. Cache-sensitive
/// operations run one at a time; stream writes/close/cleanup remain independent.
/// Waiting output updates stay queued, rather than occupying a request or file
/// worker behind another operation's disk I/O. Transfer slots bound demand; the
/// existing worker limit is unchanged. Shutdown drains all accepted operations.
/// </summary>
internal sealed class DownloadFileOperations : IDisposable
{
    private readonly int concurrency;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Queue<Work> operations = [];
    private readonly Queue<Work> outputUpdates = [];
    private readonly LinkedList<Work> outputDocuments = [];
    private readonly List<Thread> workers = [];
    private readonly Cache cache = new();
    private readonly Dictionary<DownloadOutputFile, OutputWrite> outputWrites = [];
    private readonly Dictionary<string, OutputPathClaim> outputPathClaims = new(PathComparer);
    private int runningWorkers;
    private bool updatingOutput;
    private bool preferOutputUpdate;
    private bool stopping;

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public DownloadFileOperations(int concurrency, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        this.concurrency = concurrency;
        this.logger = logger ?? NullLogger.Instance;
    }

    // The cache is accessible only inside serialized output operations. No lock
    // protecting an in-memory index is held across filesystem work or callbacks.
    public ValueTask<T> WithCacheAsync<T>(Func<Cache, T> work)
        => ExecuteAsync(() => work(cache), outputUpdate: true);

    public bool MayHaveReusable(PeerFileTarget target) => cache.Contains(target.Identity);
    public void RetireWorkflow(Guid workflowId) => cache.RetireWorkflow(workflowId);
    internal int CachedDownloadCount => cache.Count;
    internal int ActiveCachedDownloadCount => cache.ActiveCount;

    private sealed class OutputWrite
    {
        public Task<Exception?> Completion = Task.FromResult<Exception?>(null);
        public bool Queued;
        public bool Failing;
        public readonly long StartedAt = Stopwatch.GetTimestamp();
        public int Publications;
        public LinkedListNode<Work>? Pending;
    }

    public void RequestOutputUpdate(DownloadOutputFile document)
    {
        if (!document.IsWritable) return;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (!outputWrites.TryGetValue(document, out var state))
                outputWrites.Add(document, state = new());
            if (state.Queued) return;
            state.Queued = true;
            var work = new Work<Exception?>(() =>
            {
                // Coalesce only work which has not started. A request arriving
                // during capture/write owns a later completion and cannot be
                // acknowledged by a snapshot which predates its facts.
                lock (gate) { state.Queued = false; state.Pending = null; }
                try
                {
                    if (document.PublishPending()) state.Publications++;
                    if (state.Failing)
                        DownloadLogMessages.OutputPublicationRecovered(logger, document.OperationId);
                    state.Failing = false;
                    return null;
                }
                catch (Exception exception)
                {
                    if (!state.Failing)
                        DownloadLogMessages.OutputPublicationDegraded(logger, document.OperationId);
                    state.Failing = true;
                    // The owning workflow reports a terminal failure once.
                    // A superseded background attempt has no unobserved fault.
                    return exception;
                }
            });
            state.Completion = work.Completion.Task;
            // A burst of payload work should produce a coalesced document,
            // not another complete replacement between each file operation.
            state.Pending = outputDocuments.AddLast(work);
            WakeWorker();
        }
    }

    public async Task<Exception?> FlushOutputAsync(DownloadOutputFile document)
    {
        if (!document.IsWritable) return null;
        OutputWrite state;
        Task<Exception?> completion;
        lock (gate)
        {
            RequestOutputUpdate(document);
            state = outputWrites[document];
            completion = state.Completion;
            if (state.Pending is { List: not null } pending)
            {
                // Finalization is a barrier, not idle maintenance: other live
                // workflows cannot indefinitely postpone a completed root.
                outputDocuments.Remove(pending);
                outputUpdates.Enqueue(pending.Value);
                state.Pending = null;
                WakeWorker();
            }
        }
        Exception? failure = await completion.ConfigureAwait(false);
        lock (gate)
            if (ReferenceEquals(outputWrites.GetValueOrDefault(document), state)
                && ReferenceEquals(state.Completion, completion) && !state.Queued)
                outputWrites.Remove(document);
        if (failure is null)
            DownloadLogMessages.OutputPublicationCompleted(logger,
                document.OperationId, (long)Stopwatch.GetElapsedTime(state.StartedAt).TotalMilliseconds, state.Publications);
        return failure;
    }

    public ValueTask<ExactFileTransferOutcome?> TryReuseAsync(PeerFileTarget target, string outputPath, Guid jobId, Guid workflowId,
        bool publish, bool allowOverwrite, bool protectPublishedOutput)
        => WithCacheAsync(state => TryReuse(state, target, outputPath, jobId, workflowId, publish, allowOverwrite, protectPublishedOutput));

    public ValueTask<(ExactFileTransferOutcome? Reused, Stream? Output)> PrepareAsync(
        PeerFileTarget target, string outputPath, string incompletePath, Guid jobId, Guid workflowId,
        bool publish, bool allowOverwrite, bool protectPublishedOutput)
        => WithCacheAsync(state =>
        {
            var reused = TryReuse(state, target, outputPath, jobId, workflowId, publish, allowOverwrite, protectPublishedOutput);
            // No callback/queue gap between the final reuse decision and opening
            // a new output: another completed publication cannot be missed here.
            return (reused, reused == null ? Open(state, incompletePath) : null);
        });

    public ValueTask<Stream> OpenAsync(string path) => WithCacheAsync(state => Open(state, path));

    private Stream Open(Cache state, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        state.InvalidatePath(path);
        // FileMode.Create unconditionally ftruncates even a new file on Unix.
        // On ext4 that can force buffered downloads through the journal at close,
        // stalling unrelated FULL-WAL commits. Only truncate an actual old payload.
        var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);
        try
        {
            if (output.Length != 0) output.SetLength(0);
        }
        catch { output.Dispose(); throw; }
        // One managed buffer preserves cheap tiny writes; large async writes
        // pass through WorkerStream onto these same owned I/O workers.
        return new BufferedStream(new WorkerStream(output, this), 4096);
    }

    private ExactFileTransferOutcome? TryReuse(Cache cache, PeerFileTarget target, string outputPath,
        Guid jobId, Guid workflowId, bool publishToDuplicateCache, bool allowOverwrite, bool protectPublishedOutput)
    {
        if (protectPublishedOutput
            && File.Exists(outputPath)
            && cache.IsPathOwnedByAnotherTarget(outputPath, target))
        {
            return ExactFileTransferOutcome.AlreadyExists(target, outputPath);
        }

        if (!allowOverwrite && File.Exists(outputPath))
            return ExactFileTransferOutcome.AlreadyExists(target, outputPath);
        if (!cache.TryGetReusable(target, out var existingDownload)) return null;
        var existingPath = existingDownload.OutputPath;

        DownloadLogMessages.JobDecision(
            logger,
            jobId,
            "reusing-existing-transfer",
            null);

        if (!PathEquals(existingPath, outputPath))
        {
            bool replacing = File.Exists(outputPath);
            if (replacing && (!allowOverwrite
                || protectPublishedOutput && cache.IsPathOwnedByAnotherTarget(outputPath, target)))
                return ExactFileTransferOutcome.AlreadyExists(target, outputPath);
            DownloadLogMessages.JobDecision(
                logger,
                jobId,
                "copying-reused-transfer",
                null);
            cache.InvalidatePath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            // Copy(overwrite: true) also ftruncates a fresh Unix destination.
            // Keep its existing-file behavior, but create new copies directly.
            try { File.Copy(existingPath, outputPath, overwrite: replacing); }
            catch (IOException) when (!replacing)
            {
                // Preserve overwrite behavior if a local administrator created
                // the path after our check (including an existing symlink).
                File.Copy(existingPath, outputPath, overwrite: true);
            }
        }

        // A reused path belongs to this workflow too. Staging copies remain
        // private until their finalizer publishes the organized path.
        if (publishToDuplicateCache) cache.Publish(outputPath, target, workflowId);

        return ExactFileTransferOutcome.Completed(new ExactFileTransferResult(outputPath, target));
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public ValueTask CloseAsync(Stream stream) => Enqueue(stream.Dispose);

    public ValueTask DeleteIncompleteAsync(string path, string? cleanupRoot = null)
        => Enqueue(() =>
        {
            if (!File.Exists(path)) return;
            if (string.IsNullOrWhiteSpace(cleanupRoot)) File.Delete(path);
            else Utils.DeleteFileAndParentsIfEmpty(path, cleanupRoot);
        });

    private ValueTask Enqueue(Action action, bool outputUpdate = false)
        => new(ExecuteAsync(() => { action(); return true; }, outputUpdate).AsTask());

    private ValueTask<T> ExecuteAsync<T>(Func<T> action, bool outputUpdate = false)
    {
        var work = new Work<T>(action);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            (outputUpdate ? outputUpdates : operations).Enqueue(work);
            WakeWorker();
        }
        return new(work.Completion.Task);
    }

    private void WakeWorker()
    {
        int runnable = operations.Count + (!updatingOutput && (outputUpdates.Count > 0 || outputDocuments.Count > 0) ? 1 : 0);
        if (runnable > workers.Count - runningWorkers && workers.Count < concurrency)
        {
            var worker = new Thread(Run) { IsBackground = true, Name = "Sockseek file I/O" };
            worker.UnsafeStart();
            workers.Add(worker);
        }
        Monitor.Pulse(gate);
    }

    private abstract class Work
    {
        public abstract void Execute();
    }

    private sealed class Work<T>(Func<T> action) : Work
    {
        private Func<T>? pending = action;
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Execute()
        {
            // Idle workers may retain their last Work. Release its callback so
            // a finished document cannot keep a retired workflow tree alive.
            var execute = Interlocked.Exchange(ref pending, null)!;
            try { Completion.SetResult(execute()); }
            catch (Exception exception) { Completion.SetException(exception); }
        }
    }

    private void Run()
    {
        while (true)
        {
            Work next;
            bool outputUpdate;
            lock (gate)
            {
                while (operations.Count == 0 && (updatingOutput || outputUpdates.Count == 0 && outputDocuments.Count == 0))
                {
                    if (stopping && outputUpdates.Count == 0 && outputDocuments.Count == 0) return;
                    Monitor.Wait(gate);
                }
                outputUpdate = !updatingOutput && (outputUpdates.Count > 0
                    && (operations.Count == 0 || preferOutputUpdate)
                    || operations.Count == 0 && outputDocuments.Count > 0);
                if (outputUpdate && outputUpdates.Count == 0)
                {
                    next = outputDocuments.First!.Value;
                    outputDocuments.RemoveFirst();
                }
                else next = (outputUpdate ? outputUpdates : operations).Dequeue();
                preferOutputUpdate = !outputUpdate;
                if (outputUpdate) updatingOutput = true;
                runningWorkers++;
            }
            try
            {
                next.Execute();
            }
            finally
            {
                lock (gate)
                {
                    runningWorkers--;
                    if (outputUpdate)
                    {
                        updatingOutput = false;
                        // Only one further output update can run. During drain,
                        // also let idle workers observe the final empty queue.
                        if (stopping) Monitor.PulseAll(gate);
                        else Monitor.Pulse(gate);
                    }
                }
            }
        }
    }

    public async ValueTask<IDisposable> ClaimOutputPathAsync(string outputPath, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(outputPath);
        OutputPathClaim claim;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (!outputPathClaims.TryGetValue(path, out claim!))
                outputPathClaims.Add(path, claim = new OutputPathClaim());
            claim.ReferenceCount++;
        }
        try
        {
            // An uncontended claim adds no cancellation checkpoint before normal
            // transfer lifecycle publication. Cancellation still aborts a wait.
            if (!claim.Semaphore.Wait(0))
                await claim.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new OutputPathLease(this, path, claim);
        }
        catch { ReleaseClaim(path, claim, acquired: false); throw; }
    }

    private void ReleaseClaim(string path, OutputPathClaim claim, bool acquired)
    {
        if (acquired) claim.Semaphore.Release();
        lock (gate)
        {
            if (--claim.ReferenceCount == 0)
            {
                outputPathClaims.Remove(path);
                claim.Semaphore.Dispose();
            }
        }
    }

    public void Dispose()
    {
        Thread[] draining;
        lock (gate)
        {
            stopping = true;
            Monitor.PulseAll(gate);
            draining = workers.ToArray();
        }
        foreach (var worker in draining) worker.Join();
    }

    private sealed class OutputPathClaim
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class OutputPathLease(DownloadFileOperations owner, string path, OutputPathClaim claim) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) owner.ReleaseClaim(path, claim, acquired: true);
        }
    }

    // Output I/O remains serialized by WithCacheAsync. Retirement only changes
    // metadata under this lock; it never waits for filesystem work or deletes files.
    internal sealed class Cache(int maximumIdleEntries = 10_000)
    {
        internal sealed record CachedDownload(string OutputPath, PeerFileTarget Target)
        {
            public HashSet<Guid> Workflows { get; } = [];
            public LinkedListNode<PeerFileIdentity>? IdleNode { get; set; }
        }
        private readonly Lock gate = new();
        private readonly ConcurrentDictionary<PeerFileIdentity, CachedDownload> downloadedFiles = new();
        public bool Contains(PeerFileIdentity identity) => downloadedFiles.ContainsKey(identity);
        public int Count => downloadedFiles.Count;
        public int ActiveCount { get { lock (gate) return downloadedFiles.Count - idle.Count; } }
        private readonly Dictionary<string, PeerFileIdentity> identitiesByPath = new(PathComparer);
        private readonly Dictionary<Guid, HashSet<PeerFileIdentity>> identitiesByWorkflow = [];
        // Retain reuse across submissions without retaining every download for
        // daemon uptime. Active workflows are never evicted to meet this target.
        private readonly LinkedList<PeerFileIdentity> idle = [];

        public bool TryGetReusable(PeerFileTarget target, [NotNullWhen(true)] out CachedDownload? result)
        {
            if (!downloadedFiles.TryGetValue(target.Identity, out var cached)) { result = null; return false; }
            var file = new FileInfo(cached.OutputPath);
            var expectedSize = target.Size ?? cached.Target.Size;
            if (file.Exists && (expectedSize is null || file.Length == expectedSize.Value))
            {
                result = cached;
                return true;
            }
            lock (gate)
                if (ReferenceEquals(downloadedFiles.GetValueOrDefault(target.Identity), cached))
                    RemoveIdentity(target.Identity);
            result = null;
            return false;
        }

        public void Publish(string outputPath, PeerFileTarget target, Guid workflowId)
        {
            string path = Path.GetFullPath(outputPath);
            lock (gate)
            {
                if (!downloadedFiles.TryGetValue(target.Identity, out var cached)
                    || !PathComparer.Equals(cached.OutputPath, path))
                {
                    Guid[] workflows = cached?.Workflows.ToArray() ?? [];
                    InvalidatePath(path);
                    RemoveIdentity(target.Identity);
                    cached = new(path, target);
                    cached.Workflows.UnionWith(workflows);
                    downloadedFiles[target.Identity] = cached;
                    identitiesByPath[path] = target.Identity;
                }
                else if (cached.Target != target)
                {
                    cached = cached with { Target = target };
                    downloadedFiles[target.Identity] = cached;
                }
                if (cached.IdleNode is { } node)
                {
                    idle.Remove(node);
                    cached.IdleNode = null;
                }
                cached.Workflows.Add(workflowId);
                foreach (Guid workflow in cached.Workflows)
                {
                    if (!identitiesByWorkflow.TryGetValue(workflow, out var identities))
                        identitiesByWorkflow.Add(workflow, identities = []);
                    identities.Add(target.Identity);
                }
            }
        }

        public void RetireWorkflow(Guid workflowId)
        {
            lock (gate)
            {
                if (!identitiesByWorkflow.Remove(workflowId, out var identities)) return;
                foreach (var identity in identities)
                    if (downloadedFiles.TryGetValue(identity, out var cached))
                    {
                        cached.Workflows.Remove(workflowId);
                        if (cached.Workflows.Count == 0)
                            cached.IdleNode = idle.AddLast(identity);
                    }
                while (idle.Count > maximumIdleEntries)
                    RemoveIdentity(idle.First!.Value);
            }
        }

        public void InvalidatePath(string outputPath)
        {
            lock (gate)
                if (identitiesByPath.TryGetValue(Path.GetFullPath(outputPath), out var identity)) RemoveIdentity(identity);
        }

        public bool IsPathOwnedByAnotherTarget(string path, PeerFileTarget target)
        {
            lock (gate) return identitiesByPath.TryGetValue(Path.GetFullPath(path), out var identity) && identity != target.Identity;
        }
        public bool HasPublishedPathOwner(string path)
        {
            lock (gate) return identitiesByPath.ContainsKey(Path.GetFullPath(path));
        }

        private void RemoveIdentity(PeerFileIdentity identity)
        {
            if (!downloadedFiles.TryRemove(identity, out var previous)) return;
            if (previous.IdleNode is { } node) idle.Remove(node);
            identitiesByPath.Remove(previous.OutputPath);
            foreach (Guid workflow in previous.Workflows)
                if (identitiesByWorkflow.TryGetValue(workflow, out var identities))
                {
                    identities.Remove(identity);
                    if (identities.Count == 0) identitiesByWorkflow.Remove(workflow);
                }
        }
    }

    // Stream's synchronous surface is used by BufferedStream.Dispose on an I/O
    // worker. Network async reads/writes/flushes must override both overloads:
    // Stream's default array overload otherwise schedules blocking work on the pool.
    private sealed class WorkerStream(FileStream file, DownloadFileOperations owner) : Stream
    {
        public override bool CanRead => file.CanRead;
        public override bool CanSeek => file.CanSeek;
        public override bool CanWrite => file.CanWrite;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => file.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
        public override void SetLength(long value) => file.SetLength(value);
        public override void Flush() => file.Flush();
        public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => file.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => file.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.Enqueue(() => { cancellationToken.ThrowIfCancellationRequested(); file.Write(buffer.Span); });
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.ExecuteAsync(() => { cancellationToken.ThrowIfCancellationRequested(); return file.Read(buffer.Span); });
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task FlushAsync(CancellationToken cancellationToken)
            => owner.Enqueue(() => { cancellationToken.ThrowIfCancellationRequested(); file.Flush(); }).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => owner.CloseAsync(this);
    }
}
