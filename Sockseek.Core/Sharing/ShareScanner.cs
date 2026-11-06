using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Sockseek.Core.Settings;
using FileAttributeType = Soulseek.FileAttributeType;

namespace Sockseek.Core.Sharing;

public sealed record ShareScanError(string Code, string RelativePath, string Message);

public sealed record ShareScanProgress(
    long DirectoriesDiscovered,
    long FilesDiscovered,
    long BytesDiscovered,
    int ErrorCount);

public sealed record ShareScanResult(
    ShareCatalogMetadata ProvisionalMetadata,
    long DirectoriesVisited,
    long FilesIndexed,
    long BytesIndexed,
    long FilesFiltered,
    long DirectoriesExcluded,
    long EntriesSkipped,
    long MetadataFailures,
    long IoFailures,
    TimeSpan Elapsed,
    IReadOnlyList<ShareScanError> Errors,
    TimeSpan DatabaseFinalizationElapsed = default,
    TimeSpan BrowseArtifactBuildElapsed = default,
    TimeSpan ValidationElapsed = default,
    TimeSpan PublicationElapsed = default,
    TimeSpan TotalElapsed = default);

/// <summary>
/// Builds protocol-neutral catalog rows through a bounded discovery/metadata
/// pipeline. Publication and browse-artifact construction remain separate.
/// </summary>
public sealed class ShareScanner
{
    private const int MaximumErrorSamples = 100;
    private static readonly int MetadataWorkerCount =
        Math.Clamp(Environment.ProcessorCount, 1, 16);

    public async ValueTask<ShareScanResult> ScanAsync(
        SharingSettings settings,
        IShareCatalogGenerationWriter writer,
        Guid generationId,
        string settingsHash,
        CancellationToken cancellationToken = default,
        Action<ShareScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsHash);
        var started = System.Diagnostics.Stopwatch.StartNew();
        Regex[] filters = settings.Filters
            .Select(SharingSettingsValidator.CompileFilter)
            .ToArray();
        var exclusions = new HashSet<string>(
            settings.ExcludedDirectories.Select(Path.GetFullPath),
            LocalPathComparer);
        int capacity = Math.Clamp(MetadataWorkerCount * 8, 32, 256);
        var candidates = Channel.CreateBounded<FileCandidate>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = MetadataWorkerCount == 1,
            });
        var records = Channel.CreateBounded<CatalogRecord>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        var counters = new ScanCounters();
        var errors = new ConcurrentQueue<ShareScanError>();
        var progressReporter = new ScanProgressReporter(counters, errors, progress);
        using var pipelineStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken pipelineToken = pipelineStop.Token;

        Task writerTask = WriteRecordsAsync(
            writer,
            records.Reader,
            counters,
            errors,
            progressReporter,
            pipelineToken);
        Task[] workers = Enumerable.Range(0, MetadataWorkerCount)
            .Select(_ => ReadMetadataAsync(
                candidates.Reader,
                records.Writer,
                counters,
                errors,
                progressReporter,
                pipelineToken))
            .ToArray();
        Task allWorkers = Task.WhenAll(workers);
        Task writerFailureMonitor = CancelPipelineOnFailureAsync(writerTask, pipelineStop);
        Task workerFailureMonitor = CancelPipelineOnFailureAsync(allWorkers, pipelineStop);

        Exception? discoveryFailure = null;
        try
        {
            await DiscoverAsync(
                settings,
                exclusions,
                filters,
                candidates.Writer,
                records.Writer,
                counters,
                errors,
                progressReporter,
                pipelineToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            discoveryFailure = ex;
        }
        finally
        {
            candidates.Writer.TryComplete(discoveryFailure);
        }

        Exception? workerFailure = null;
        try
        {
            await allWorkers.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            workerFailure = ex;
        }
        finally
        {
            records.Writer.TryComplete(discoveryFailure ?? workerFailure);
        }
        Exception? writerFailure = null;
        try
        {
            await writerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            writerFailure = ex;
        }
        await Task.WhenAll(writerFailureMonitor, workerFailureMonitor).ConfigureAwait(false);

        Exception? failure = FirstPipelineFailure(writerFailure, workerFailure, discoveryFailure);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        progressReporter.Report(force: true);

        started.Stop();
        var metadata = new ShareCatalogMetadata(
            generationId,
            DateTimeOffset.UtcNow,
            settingsHash,
            counters.DirectoriesVisited,
            counters.FilesIndexed,
            counters.BytesIndexed,
            ShareBrowseStatus.UnavailableOversize,
            null,
            null,
            null);
        return new ShareScanResult(
            metadata,
            counters.DirectoriesVisited,
            counters.FilesIndexed,
            counters.BytesIndexed,
            counters.FilesFiltered,
            counters.DirectoriesExcluded,
            counters.EntriesSkipped,
            counters.MetadataFailures,
            counters.IoFailures,
            started.Elapsed,
            errors.ToArray());
    }

    private static async Task CancelPipelineOnFailureAsync(
        Task stage,
        CancellationTokenSource pipelineStop)
    {
        try
        {
            await stage.ConfigureAwait(false);
        }
        catch
        {
            pipelineStop.Cancel();
        }
    }

    private static Exception? FirstPipelineFailure(params Exception?[] failures)
        => failures.FirstOrDefault(failure => failure is not null and not OperationCanceledException)
           ?? failures.FirstOrDefault(failure => failure is not null);

    private static async Task DiscoverAsync(
        SharingSettings settings,
        HashSet<string> exclusions,
        Regex[] filters,
        ChannelWriter<FileCandidate> candidates,
        ChannelWriter<CatalogRecord> records,
        ScanCounters counters,
        ConcurrentQueue<ShareScanError> errors,
        ScanProgressReporter progress,
        CancellationToken cancellationToken)
    {
        long rootId = 0;
        long directoryId = 0;
        foreach (var configured in settings.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileAttributes rootAttributes = File.GetAttributes(configured.LocalPath);
                if ((rootAttributes & FileAttributes.Directory) == 0
                    || (rootAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The configured path is not a safe directory.");
                }
            }
            catch (Exception ex) when (IsExpectedIo(ex))
            {
                Interlocked.Increment(ref counters.IoFailures);
                Interlocked.Increment(ref counters.EntriesSkipped);
                Record(
                    errors,
                    "root-unavailable",
                    configured.EffectiveAlias,
                    $"Configured root could not be read ({ex.GetType().Name}).");
                progress.Report();
                continue;
            }

            var root = new ShareCatalogRoot(
                ++rootId,
                configured.EffectiveAlias,
                configured.LocalPath,
                RemotePathKey.CreateAlias(configured.EffectiveAlias));
            await records.WriteAsync(
                new RootRecord(root),
                cancellationToken).ConfigureAwait(false);

            var stack = new Stack<DirectoryFrame>();
            try
            {
                var rootDirectory = new ShareCatalogDirectory(
                    ++directoryId,
                    root.RootId,
                    "",
                    configured.EffectiveAlias,
                    RemotePathKey.Create(configured.EffectiveAlias));
                await records.WriteAsync(
                    new DirectoryRecord(rootDirectory),
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref counters.DirectoriesVisited);
                progress.Report();
                stack.Push(OpenDirectory(
                    configured.LocalPath,
                    "",
                    rootDirectory));

                while (stack.TryPeek(out DirectoryFrame? frame))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool hasEntry;
                    try
                    {
                        hasEntry = frame.Entries.MoveNext();
                    }
                    catch (Exception ex) when (IsExpectedIo(ex))
                    {
                        stack.Pop();
                        frame.Dispose();
                        if (frame.RelativePath.Length == 0)
                        {
                            Interlocked.Increment(ref counters.IoFailures);
                            Interlocked.Increment(ref counters.EntriesSkipped);
                            Record(
                                errors,
                                "root-unavailable",
                                configured.EffectiveAlias,
                                $"Configured root could not be enumerated ({ex.GetType().Name}).");
                        }
                        else
                        {
                            RecordIo(counters, errors, frame.RelativePath, ex);
                        }
                        continue;
                    }
                    if (!hasEntry)
                    {
                        stack.Pop();
                        frame.Dispose();
                        continue;
                    }

                    string entry = frame.Entries.Current;
                    string entryRelative =
                        Path.GetRelativePath(configured.LocalPath, entry);
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (IsExpectedIo(ex))
                    {
                        RecordIo(counters, errors, entryRelative, ex);
                        continue;
                    }

                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if (ShouldSkipByAttributes(entry, attributes, isDirectory))
                    {
                        Interlocked.Increment(ref counters.EntriesSkipped);
                        continue;
                    }

                    string entryRemote = CombineRemote(
                        configured.EffectiveAlias,
                        entryRelative);
                    if (isDirectory)
                    {
                        if (exclusions.Contains(Path.GetFullPath(entry)))
                        {
                            Interlocked.Increment(ref counters.DirectoriesExcluded);
                            continue;
                        }
                        if (IsFiltered(filters, entryRemote))
                        {
                            Interlocked.Increment(ref counters.DirectoriesExcluded);
                            continue;
                        }
                        if (!TryCreateRemotePathKey(
                                entryRemote,
                                entryRelative,
                                counters,
                                errors,
                                out RemotePathKey entryRemoteKey))
                        {
                            continue;
                        }

                        var child = new ShareCatalogDirectory(
                            ++directoryId,
                            root.RootId,
                            entryRelative,
                            entryRemote,
                            entryRemoteKey);
                        DirectoryFrame childFrame;
                        try
                        {
                            childFrame = OpenDirectory(entry, entryRelative, child);
                        }
                        catch (Exception ex) when (IsExpectedIo(ex))
                        {
                            RecordIo(counters, errors, entryRelative, ex);
                            continue;
                        }
                        await records.WriteAsync(
                            new DirectoryRecord(child),
                            cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref counters.DirectoriesVisited);
                        progress.Report();
                        stack.Push(childFrame);
                        continue;
                    }

                    if (IsFiltered(filters, entryRemote))
                    {
                        Interlocked.Increment(ref counters.FilesFiltered);
                        continue;
                    }
                    if (!TryCreateRemotePathKey(
                            entryRemote,
                            entryRelative,
                            counters,
                            errors,
                            out RemotePathKey fileRemoteKey))
                    {
                        continue;
                    }
                    await candidates.WriteAsync(
                        new FileCandidate(
                            root,
                            frame.Directory,
                            entryRelative,
                            entryRemote,
                            fileRemoteKey),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                while (stack.TryPop(out DirectoryFrame? frame))
                    frame.Dispose();
            }
        }
    }

    private static DirectoryFrame OpenDirectory(
        string path,
        string relativePath,
        ShareCatalogDirectory directory)
        => new(
            relativePath,
            directory,
            Directory.EnumerateFileSystemEntries(path).GetEnumerator());

    private static async Task ReadMetadataAsync(
        ChannelReader<FileCandidate> candidates,
        ChannelWriter<CatalogRecord> records,
        ScanCounters counters,
        ConcurrentQueue<ShareScanError> errors,
        ScanProgressReporter progress,
        CancellationToken cancellationToken)
    {
        await foreach (var candidate in candidates.ReadAllAsync(cancellationToken))
        {
            try
            {
                await using var opened = SafeSharedFileOpener.Open(
                    candidate.Root.LocalPath,
                    candidate.RelativePath);
                IReadOnlyList<ShareFileAttribute> attributes = [];
                if (Utils.IsMusicFile(candidate.RelativePath))
                {
                    try
                    {
                        attributes = ReadAudioAttributes(
                            candidate.RelativePath,
                            opened.Stream);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref counters.MetadataFailures);
                        Record(
                            errors,
                            "metadata",
                            candidate.RelativePath,
                            $"Metadata could not be read ({ex.GetType().Name}).");
                    }
                }

                long fileId = Interlocked.Increment(ref counters.NextFileId);
                var file = new ShareCatalogFile(
                    fileId,
                    candidate.Root.RootId,
                    candidate.Directory.DirectoryId,
                    candidate.RelativePath,
                    candidate.RemotePath,
                    candidate.RemotePathKey,
                    candidate.RemotePath.Replace('\\', ' '),
                    opened.Fingerprint.SizeBytes,
                    opened.Fingerprint.LastWriteTimeUtc,
                    1,
                    Path.GetExtension(candidate.RelativePath).TrimStart('.').ToLowerInvariant(),
                    attributes);
                await records.WriteAsync(
                    new FileRecord(file),
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref counters.FilesIndexed);
                Interlocked.Add(ref counters.BytesIndexed, file.SizeBytes);
                progress.Report();
            }
            catch (Exception ex) when (IsExpectedIo(ex))
            {
                RecordIo(counters, errors, candidate.RelativePath, ex);
            }
        }
    }

    private static IReadOnlyList<ShareFileAttribute> ReadAudioAttributes(
        string name,
        Stream stream)
    {
        stream.Position = 0;
        var abstraction = new ReadOnlyFileAbstraction(name, stream);
        using var file = TagLib.File.Create(abstraction, TagLib.ReadStyle.Average);
        var properties = file.Properties;
        if (properties is null)
            return [];

        var attributes = new List<ShareFileAttribute>(5);
        if (properties.AudioBitrate > 0)
            attributes.Add(new((int)FileAttributeType.BitRate, properties.AudioBitrate));
        if (properties.Duration > TimeSpan.Zero)
            attributes.Add(new((int)FileAttributeType.Length, checked((int)properties.Duration.TotalSeconds)));
        attributes.Add(new((int)FileAttributeType.VariableBitRate, properties.BitsPerSample > 0 ? 1 : 0));
        if (properties.AudioSampleRate > 0)
            attributes.Add(new((int)FileAttributeType.SampleRate, properties.AudioSampleRate));
        if (properties.BitsPerSample > 0)
            attributes.Add(new((int)FileAttributeType.BitDepth, properties.BitsPerSample));
        return attributes;
    }

    private static async Task WriteRecordsAsync(
        IShareCatalogGenerationWriter writer,
        ChannelReader<CatalogRecord> records,
        ScanCounters counters,
        ConcurrentQueue<ShareScanError> errors,
        ScanProgressReporter progress,
        CancellationToken cancellationToken)
    {
        await foreach (var record in records.ReadAllAsync(cancellationToken))
        {
            try
            {
                switch (record)
                {
                    case RootRecord root:
                        await writer.AddRootAsync(root.Value, cancellationToken).ConfigureAwait(false);
                        break;
                    case DirectoryRecord directory:
                        await writer.AddDirectoryAsync(
                            directory.Value,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case FileRecord file:
                        await writer.AddFileAsync(file.Value, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (ShareCatalogEntryCollisionException ex)
            {
                Interlocked.Increment(ref counters.EntriesSkipped);
                switch (record)
                {
                    case DirectoryRecord:
                        Interlocked.Decrement(ref counters.DirectoriesVisited);
                        break;
                    case FileRecord file:
                        Interlocked.Decrement(ref counters.FilesIndexed);
                        Interlocked.Add(ref counters.BytesIndexed, -file.Value.SizeBytes);
                        break;
                }
                string path = record switch
                {
                    RootRecord root => root.Value.Alias,
                    DirectoryRecord directory => directory.Value.RelativePath,
                    FileRecord file => file.Value.RelativePath,
                    _ => "",
                };
                Record(
                    errors,
                    "remote-path-collision",
                    path,
                    $"Entry was skipped because its remote identity collides ({ex.GetType().Name}).");
                progress.Report();
            }
        }
    }

    private static bool ShouldSkipByAttributes(
        string path,
        FileAttributes attributes,
        bool isDirectory)
    {
        if ((attributes & (FileAttributes.Hidden
                           | FileAttributes.System
                           | FileAttributes.ReparsePoint)) != 0)
            return true;
        return !OperatingSystem.IsWindows()
               && Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal);
    }

    private static bool IsFiltered(Regex[] filters, string remotePath)
    {
        foreach (var filter in filters)
        {
            // A timeout intentionally escapes and fails the staging generation.
            if (filter.IsMatch(remotePath))
                return true;
        }
        return false;
    }

    private static bool TryCreateRemotePathKey(
        string remotePath,
        string relativePath,
        ScanCounters counters,
        ConcurrentQueue<ShareScanError> errors,
        out RemotePathKey key)
    {
        try
        {
            key = RemotePathKey.Create(remotePath);
            return true;
        }
        catch (ArgumentException)
        {
            Interlocked.Increment(ref counters.EntriesSkipped);
            Record(
                errors,
                "unsupported-path",
                relativePath,
                "Entry remote path is invalid or exceeds the protocol request bound.");
            key = null!;
            return false;
        }
    }

    private static string CombineRemote(string alias, string relative)
        => relative.Length == 0
            ? alias
            : $"{alias}\\{ToRemoteSeparators(relative)}";

    private static string ToRemoteSeparators(string path)
        => path.Replace(Path.DirectorySeparatorChar, '\\')
            .Replace(Path.AltDirectorySeparatorChar, '\\');

    private static bool IsExpectedIo(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or SharedFileOpenException
            or NotSupportedException;

    private static void RecordIo(
        ScanCounters counters,
        ConcurrentQueue<ShareScanError> errors,
        string relative,
        Exception ex)
    {
        Interlocked.Increment(ref counters.IoFailures);
        Record(
            errors,
            "io",
            relative,
            $"Entry could not be read ({ex.GetType().Name}).");
    }

    private static void Record(
        ConcurrentQueue<ShareScanError> errors,
        string code,
        string relative,
        string message)
    {
        if (errors.Count >= MaximumErrorSamples)
            return;
        const int maximumRelativeCharacters = 512;
        string display = ToRemoteSeparators(relative);
        if (display.Length > maximumRelativeCharacters)
            display = $"{display[..(maximumRelativeCharacters - 1)]}…";
        errors.Enqueue(new ShareScanError(code, display, message));
    }

    private static StringComparer LocalPathComparer
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record FileCandidate(
        ShareCatalogRoot Root,
        ShareCatalogDirectory Directory,
        string RelativePath,
        string RemotePath,
        RemotePathKey RemotePathKey);

    private sealed class DirectoryFrame(
        string relativePath,
        ShareCatalogDirectory directory,
        IEnumerator<string> entries) : IDisposable
    {
        public string RelativePath { get; } = relativePath;
        public ShareCatalogDirectory Directory { get; } = directory;
        public IEnumerator<string> Entries { get; } = entries;
        public void Dispose() => Entries.Dispose();
    }

    private abstract record CatalogRecord;
    private sealed record RootRecord(ShareCatalogRoot Value) : CatalogRecord;
    private sealed record DirectoryRecord(ShareCatalogDirectory Value) : CatalogRecord;
    private sealed record FileRecord(ShareCatalogFile Value) : CatalogRecord;

    private sealed class ScanCounters
    {
        public long NextFileId;
        public long DirectoriesVisited;
        public long FilesIndexed;
        public long BytesIndexed;
        public long FilesFiltered;
        public long DirectoriesExcluded;
        public long EntriesSkipped;
        public long MetadataFailures;
        public long IoFailures;
    }

    private sealed class ScanProgressReporter(
        ScanCounters counters,
        ConcurrentQueue<ShareScanError> errors,
        Action<ShareScanProgress>? callback)
    {
        private static readonly long MinimumIntervalTicks =
            (long)(System.Diagnostics.Stopwatch.Frequency * 0.25);
        private long lastReportedTicks;

        public void Report(bool force = false)
        {
            if (callback is null)
                return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long previous = Volatile.Read(ref lastReportedTicks);
            if (!force && now - previous < MinimumIntervalTicks)
                return;
            if (!force
                && Interlocked.CompareExchange(ref lastReportedTicks, now, previous) != previous)
            {
                return;
            }
            if (force)
                Volatile.Write(ref lastReportedTicks, now);

            callback(new ShareScanProgress(
                Interlocked.Read(ref counters.DirectoriesVisited),
                Interlocked.Read(ref counters.FilesIndexed),
                Interlocked.Read(ref counters.BytesIndexed),
                errors.Count));
        }
    }

    private sealed class ReadOnlyFileAbstraction(
        string name,
        Stream stream) : TagLib.File.IFileAbstraction
    {
        public string Name { get; } = name;
        public Stream ReadStream => stream;
        public Stream WriteStream => throw new NotSupportedException();
        public void CloseStream(Stream _) { }
    }
}
