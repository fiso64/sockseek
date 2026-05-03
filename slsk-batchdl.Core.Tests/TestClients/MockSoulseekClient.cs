using System.Collections.Concurrent;
using Soulseek;

namespace Tests.ClientTests
{
    public partial class MockSoulseekClient : ISoulseekClient
    {
        public IReadOnlyCollection<Transfer> Downloads => throw new NotImplementedException();

        public SoulseekClientStates State => SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn;

        public SoulseekClientOptions Options => throw new NotImplementedException();

        private List<Soulseek.SearchResponse> index;
        private readonly bool slowMode;
        private readonly int searchDelayMs;
        private readonly HashSet<string> failingUsers;

        public int SearchesCancelledMidDelay { get; private set; }
        public int DownloadCallCount;
        public bool IsDisposed { get; private set; }

        public MockSoulseekClient(List<Soulseek.SearchResponse> index, bool slowMode = false, int searchDelayMs = 0, IEnumerable<string>? failingUsers = null)
        {
            this.index         = index;
            this.slowMode      = slowMode;
            this.searchDelayMs = searchDelayMs;
            this.failingUsers  = new HashSet<string>(failingUsers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static MockSoulseekClient FromLocalPaths(bool useTags, bool slowMode, params string[] localPaths)
        {
            if (useTags)
                Logger.Info($"Reading tags from mock files dir, this may take a while. Use --mock-files-no-read-tags if tags are not needed.");

            var files = localPaths.SelectMany(path =>
                System.IO.Directory.Exists(path)
                    ? System.IO.Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    : new[] { path });

            var fileList = files
                .Select((path, i) =>
                {

                    var attributes = new List<Soulseek.FileAttribute>();

                    if (Utils.IsMusicFile(path))
                    {
                        if (useTags)
                        {
                            try
                            {
                                using var file = TagLib.File.Create(path);
                                if (file.Properties != null)
                                {
                                    attributes.Add(new Soulseek.FileAttribute(FileAttributeType.BitRate, file.Properties.AudioBitrate));
                                    attributes.Add(new Soulseek.FileAttribute(FileAttributeType.Length, (int)file.Properties.Duration.TotalSeconds));
                                    attributes.Add(new Soulseek.FileAttribute(FileAttributeType.VariableBitRate, file.Properties.BitsPerSample > 0 ? 1 : 0));

                                    if (file.Properties.AudioSampleRate > 0)
                                        attributes.Add(new Soulseek.FileAttribute(FileAttributeType.SampleRate, file.Properties.AudioSampleRate));

                                    if (file.Properties.BitsPerSample > 0)
                                        attributes.Add(new Soulseek.FileAttribute(FileAttributeType.BitDepth, file.Properties.BitsPerSample));
                                }
                            }
                            catch (Exception ex) { Logger.Warn($"Failed to read tags for '{path}': {ex.Message}"); }
                        }
                        else
                        {
                            // Generate deterministic length from filename
                            var filename = Path.GetFileName(path);
                            int hash = 0;
                            foreach (char c in filename) hash = (hash * 31) + c;
                            var length = Math.Abs(hash % 1000) + 1; // 1-1000 seconds
                            attributes.Add(new Soulseek.FileAttribute(FileAttributeType.Length, length));
                        }
                    }

                    return new Soulseek.File(
                        i + 1,
                        path.Replace('/', '\\'),
                        new FileInfo(path).Length,
                        Path.GetExtension(path),
                        attributeList: attributes
                    );
                })
                .ToList();

            var index = new List<SearchResponse>() {
                new SearchResponse(
                    username: "local",
                    token: 1,
                    hasFreeUploadSlot: true,
                    uploadSpeed: 100,
                    queueLength: 0,
                    fileList: fileList
                )
            };

            return new MockSoulseekClient(index, slowMode);
        }


        public Task ConnectAsync(string username, string password, CancellationToken? cancellationToken = null)
        {
            return ConnectAsync("", 0, username, password, cancellationToken);
        }

        public Task ConnectAsync(string address, int port, string username, string password, CancellationToken? cancellationToken = null)
        {
            return Task.CompletedTask;
        }

        public Task SetSharedCountsAsync(int directories, int files, CancellationToken? cancellationToken = null)
        {
            return Task.CompletedTask;
        }

        public Task<BrowseResponse> BrowseAsync(string username, BrowseOptions? options = null, CancellationToken? cancellationToken = null)
        {
            var user = index.FirstOrDefault(x => x.Username == username);

            if (user == null)
            {
                throw new UserNotFoundException($"User {username} not found");
            }

            var directories = user.Files
                .GroupBy(x => Utils.GetDirectoryNameSlsk(x.Filename))
                .Select(g => new Soulseek.Directory(
                    g.Key.Replace('/', '\\'), // Soulseek ALWAYS returns paths with separator \, regardless of OS.
                    g.Select(f => new Soulseek.File(
                        f.Code,
                        f.Filename.Replace('/', '\\'),
                        f.Size,
                        f.Extension,
                        f.Attributes
                    )).ToList()
                ));

            return Task.FromResult(new BrowseResponse(directories));
        }

        public Task<(Search Search, IReadOnlyCollection<SearchResponse> Responses)> SearchAsync(SearchQuery query, SearchScope? scope = null, int? token = null, SearchOptions? options = null, CancellationToken? cancellationToken = null)
        {
            return SearchAsyncInternal(query, null, scope, token, options, cancellationToken);
        }

        public Task<Search> SearchAsync(SearchQuery query, Action<SearchResponse> responseHandler, SearchScope? scope = null, int? token = null, SearchOptions? options = null, CancellationToken? cancellationToken = null)
        {
            return SearchAsyncInternal(query, responseHandler, scope, token, options, cancellationToken).ContinueWith(t => t.Result.Search);
        }

        private async Task<(Search Search, IReadOnlyCollection<SearchResponse> Responses)> SearchAsyncInternal(SearchQuery query, Action<SearchResponse>? responseHandler, SearchScope? scope = null, int? token = null, SearchOptions? options = null, CancellationToken? cancellationToken = null)
        {
            options ??= new SearchOptions();
            var searchToken = token ?? Random.Shared.Next();
            var responses = new List<SearchResponse>();
            var totalFileCount = 0;
            var totalLockedFileCount = 0;
            var ct = cancellationToken ?? CancellationToken.None;
            bool firstResponse = true;

            foreach (var user in index)
            {
                ct.ThrowIfCancellationRequested();

                var matchingFiles = new List<Soulseek.File>();

                foreach (var file in user.Files)
                {
                    var path = file.Filename.ToLower();
                    bool matches = query.Terms.All(term => path.Contains(term.ToLower()));

                    if (matches && (options.FileFilter?.Invoke(file) ?? true))
                    {
                        matchingFiles.Add(file);
                    }
                }

                if (matchingFiles.Count > 0)
                {
                    var response = new SearchResponse(
                        username: user.Username,
                        token: searchToken,
                        hasFreeUploadSlot: user.HasFreeUploadSlot,
                        uploadSpeed: user.UploadSpeed,
                        queueLength: user.QueueLength,
                        fileList: matchingFiles
                    );

                    if (!options.FilterResponses ||
                        (matchingFiles.Count >= options.MinimumResponseFileCount &&
                        response.QueueLength <= options.MaximumPeerQueueLength &&
                        response.UploadSpeed >= options.MinimumPeerUploadSpeed &&
                        (options.ResponseFilter?.Invoke(response) ?? true)))
                    {
                        responses.Add(response);
                        totalFileCount += response.FileCount;
                        totalLockedFileCount += response.LockedFileCount;
                        options.ResponseReceived?.Invoke((null, response));
                        responseHandler?.Invoke(response);

                        // After firing the first response, simulate the search still running.
                        // This lets fast-search tests race the provisional download against the delay.
                        if (firstResponse && searchDelayMs > 0)
                        {
                            firstResponse = false;
                            try { await Task.Delay(searchDelayMs, ct); }
                            catch (OperationCanceledException)
                            {
                                SearchesCancelledMidDelay++;
                                break;
                            }
                        }
                    }

                    if (responses.Count >= options.ResponseLimit)
                        break;
                }
            }

            var search = new Search(
                query: query,
                token: searchToken,
                state: SearchStates.Completed,
                responseCount: responses.Count,
                fileCount: totalFileCount,
                lockedFileCount: totalLockedFileCount,
                scope: new SearchScope(SearchScopeType.Network)
            );

            return (search, (IReadOnlyCollection<SearchResponse>)responses);
        }

        // One semaphore per username — each peer allows only one concurrent download,
        // but files from different peers can transfer in parallel (matching real Soulseek behaviour).
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _userSemaphores = new();

        SemaphoreSlim GetUserSemaphore(string username) =>
            _userSemaphores.GetOrAdd(username, _ => new SemaphoreSlim(1, 1));

        public async Task<Transfer> DownloadAsync(string username, string remoteFilename, string localFilename, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            async Task<Stream> StreamFactory()
            {
                var directory = Path.GetDirectoryName(localFilename);
                if (!string.IsNullOrEmpty(directory))
                    System.IO.Directory.CreateDirectory(directory);
                return System.IO.File.Create(localFilename);
            }

            return await DownloadAsyncInternal(username, remoteFilename, StreamFactory, size, startOffset, token, options, cancellationToken);
        }

        public Task<Transfer> DownloadAsync(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            return DownloadAsyncInternal(username, remoteFilename, outputStreamFactory, size, startOffset, token, options, cancellationToken);
        }

        private async Task<Transfer> DownloadAsyncInternal(string username, string remoteFilename, Func<Task<Stream>> outputStreamFactory, long? size = null, long startOffset = 0, int? token = null, TransferOptions? options = null, CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref DownloadCallCount);

            if (failingUsers.Contains(username))
                throw new SoulseekClientException($"Simulated download failure for user {username}");

            var transferToken = token ?? Random.Shared.Next();
            long fileSize;
            string? sourceFilePath = null;

            if (username == "local")
            {
                // For local user, try to find the actual file in the filesystem
                sourceFilePath = Path.GetFullPath(Utils.GetAsPathSlsk(remoteFilename));
                if (!System.IO.File.Exists(sourceFilePath))
                {
                    throw new FileNotFoundException($"Local file {sourceFilePath} not found");
                }
                fileSize = (long)(size == null || size == -1 ? new FileInfo(sourceFilePath).Length : size);
            }
            else
            {
                var user = index.FirstOrDefault(x => x.Username == username);
                if (user == null)
                {
                    throw new UserNotFoundException($"User {username} not found");
                }

                // Find the file in the directories
                Soulseek.File? foundFile = user.Files.FirstOrDefault(x => x.Filename.Equals(remoteFilename, StringComparison.OrdinalIgnoreCase));
                if (foundFile == null)
                {
                    throw new FileNotFoundException($"File {remoteFilename} not found for user {username}");
                }
                fileSize = size ?? foundFile.Size;
            }

            var transfer = new Transfer(
                direction: TransferDirection.Download,
                username: username,
                filename: remoteFilename,
                token: transferToken,
                state: TransferStates.Queued,
                size: fileSize,
                startOffset: startOffset
            );

            // Simulate the download process asynchronously
            await Task.Run(async () =>
            {
                try
                {
                    var ct = cancellationToken.GetValueOrDefault(CancellationToken.None);

                    Transfer MakeTransfer(TransferStates state, long bytes, double speed = 0, DateTime? startTime = null, DateTime? endTime = null) =>
                        new Transfer(TransferDirection.Download, username, remoteFilename, transferToken,
                            state, fileSize, startOffset, bytes, speed, startTime, endTime);

                    void FireState(TransferStates state, long bytes = 0, double speed = 0, DateTime? t0 = null)
                    {
                        transfer = MakeTransfer(state, bytes, speed, t0);
                        options?.StateChanged?.Invoke((state, transfer));
                    }

                    void FireProgress(long bytes, long prev, double speed, DateTime t0)
                    {
                        transfer = MakeTransfer(TransferStates.InProgress, bytes, speed, t0);
                        options?.ProgressUpdated?.Invoke((prev, transfer));
                    }

                    // Always fire Queued (R) before acquiring the per-user slot —
                    // this mirrors real Soulseek where the peer queues your request
                    // while serving another file to you.
                    FireState(TransferStates.Queued | TransferStates.Remotely);

                    var userSem = GetUserSemaphore(username);
                    await userSem.WaitAsync(ct);
                    try
                    {

                    // Initialising — peer has accepted the transfer
                    if (slowMode)
                    {
                        await Task.Delay(Random.Shared.Next(50, 150), ct);
                    }
                    FireState(TransferStates.Initializing);

                    using var outputStream = await outputStreamFactory();
                    var startTime = DateTime.UtcNow;
                    var bytesTransferred = startOffset;
                    const int chunkSize = 16384;

                    FireState(TransferStates.InProgress, bytesTransferred, 0, startTime);

                    if (slowMode)
                    {
                        // Spread file transfer over 0.5–1.5 s regardless of actual file size.
                        int totalMs = Random.Shared.Next(500, 1500);
                        int steps   = 20;
                        int stepMs  = totalMs / steps;

                        if (startOffset > 0 && sourceFilePath != null)
                        {
                            // Seek real file but don't stream it byte-by-byte in slow mode.
                        }

                        for (int step = 1; step <= steps; step++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(stepMs, ct);

                            bytesTransferred = startOffset + (long)(fileSize - startOffset) * step / steps;
                            var stepElapsed = DateTime.UtcNow - startTime;
                            var speed       = stepElapsed.TotalSeconds > 0 ? bytesTransferred / stepElapsed.TotalSeconds : 0;
                            long prev       = startOffset + (long)(fileSize - startOffset) * (step - 1) / steps;
                            FireProgress(bytesTransferred, prev, speed, startTime);
                        }

                        // Write placeholder bytes so the file actually exists on disk.
                        if (sourceFilePath != null)
                        {
                            using var src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
                            await src.CopyToAsync(outputStream, ct);
                        }
                        else
                        {
                            var dummy = new byte[Math.Max(1, fileSize)];
                            await outputStream.WriteAsync(dummy, 0, dummy.Length, ct);
                        }
                        bytesTransferred = fileSize;
                    }
                    else if (sourceFilePath != null)
                    {
                        // Copy from local file immediately.
                        using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
                        if (startOffset > 0) sourceStream.Seek(startOffset, SeekOrigin.Begin);
                        var buffer = new byte[chunkSize];
                        int bytesRead;
                        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, chunkSize, ct)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            await outputStream.WriteAsync(buffer, 0, bytesRead, ct);
                            await outputStream.FlushAsync(ct);
                            long prev = bytesTransferred;
                            bytesTransferred += bytesRead;
                            var chunkElapsed = DateTime.UtcNow - startTime;
                            FireProgress(bytesTransferred, prev, bytesTransferred / Math.Max(chunkElapsed.TotalSeconds, 0.001), startTime);
                        }
                    }
                    else
                    {
                        // Generate fake data immediately.
                        if (startOffset > 0) outputStream.Seek(startOffset, SeekOrigin.Begin);
                        var buffer = new byte[chunkSize];
                        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 256);
                        while (bytesTransferred < fileSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            var currentChunk = (int)Math.Min(chunkSize, fileSize - bytesTransferred);
                            await outputStream.WriteAsync(buffer, 0, currentChunk, ct);
                            await outputStream.FlushAsync(ct);
                            long prev = bytesTransferred;
                            bytesTransferred += currentChunk;
                            var fakeElapsed = DateTime.UtcNow - startTime;
                            FireProgress(bytesTransferred, prev, bytesTransferred / Math.Max(fakeElapsed.TotalSeconds, 0.001), startTime);
                        }
                    }

                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    transfer = new Transfer(TransferDirection.Download, username, remoteFilename, transferToken,
                        TransferStates.Completed, fileSize, startOffset,
                        bytesTransferred, elapsed > 0 ? bytesTransferred / elapsed : 0, startTime, DateTime.UtcNow);
                    options?.StateChanged?.Invoke((TransferStates.Completed, transfer));

                    } // end userSem try
                    finally { userSem.Release(); }
                }
                catch (OperationCanceledException)
                {
                    transfer = new Transfer(TransferDirection.Download, username, remoteFilename, transferToken,
                        TransferStates.Cancelled, fileSize, startOffset);
                    options?.StateChanged?.Invoke((TransferStates.Cancelled, transfer));
                    throw;
                }
                catch (Exception ex)
                {
                    transfer = new Transfer(TransferDirection.Download, username, remoteFilename, transferToken,
                        TransferStates.Errored, fileSize, startOffset, exception: ex);
                    options?.StateChanged?.Invoke((TransferStates.Errored, transfer));
                    throw;
                }
            });

            return transfer;
        }

    }
}
