using System.Text;
using Sockseek.Api;
using Sockseek.Core.UserProfiles;

namespace Sockseek.Cli;

internal sealed class InteractiveShareBrowser
{
    private const int PageSize = ServerProtocol.DefaultNestedPageSize;
    private const int PageCacheCapacity = 3;

    private readonly SockseekApiClient api;
    private readonly UserBrowseDto browse;
    private readonly SubmissionOptionsDto options;
    private readonly ShareSelectionCart cart = new();
    private readonly List<Location> locations = [];
    private readonly List<PageCursor> pageHistory = [];
    private readonly Dictionary<PageCacheKey, CachedPage> pageCache = [];
    private readonly LinkedList<PageCacheKey> cacheOrder = [];
    private string? filter;
    private int pageIndex;
    private int rowIndex;
    private string? message;

    public InteractiveShareBrowser(
        SockseekApiClient api,
        UserBrowseDto browse,
        SubmissionOptionsDto options)
    {
        this.api = api;
        this.browse = browse;
        this.options = options;
        locations.Add(new Location(null, browse.Username, ""));
        pageHistory.Add(PageCursor.First);
    }

    public async Task<StartUserShareDownloadsResponseDto?> RunAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            CachedPage page = await CurrentPageAsync(cancellationToken).ConfigureAwait(false);
            rowIndex = Math.Clamp(rowIndex, 0, Math.Max(0, page.Rows.Count - 1));
            Render(page);
            ConsoleKeyInfo key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (rowIndex > 0) rowIndex--;
                    else await PreviousPageAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.DownArrow:
                    if (rowIndex + 1 < page.Rows.Count) rowIndex++;
                    else await NextPageAsync(page, cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.PageUp:
                    await PreviousPageAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.PageDown:
                    await NextPageAsync(page, cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.RightArrow:
                    if (CurrentRow(page) is BrowserRow.Directory directory)
                        Enter(directory.Value);
                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.LeftArrow:
                    Leave();
                    break;
                case ConsoleKey.Spacebar:
                    Toggle(CurrentRow(page));
                    break;
                case ConsoleKey.Oem2 when key.KeyChar == '/':
                    await EditFilterAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.D:
                {
                    StartUserShareDownloadsResponseDto? response = await ReviewAndSubmitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (response is not null)
                        return response;
                    break;
                }
                case ConsoleKey.H:
                case ConsoleKey.F1:
                case ConsoleKey.Oem2 when key.KeyChar == '?':
                    message = "Arrows navigate · Enter opens · Backspace returns · Space selects · / filters · D downloads · Q quits";
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return null;
            }
        }
    }

    private async Task<CachedPage> CurrentPageAsync(CancellationToken cancellationToken)
    {
        Location location = locations[^1];
        PageCursor cursor = pageHistory[pageIndex];
        var key = new PageCacheKey(location.DirectoryId, filter, cursor);
        if (pageCache.TryGetValue(key, out CachedPage? cached))
        {
            Touch(key);
            return cached;
        }

        var rows = new List<BrowserRow>(PageSize);
        PageCursor? next = null;
        if (cursor.Stage == PageStage.Directories)
        {
            PageDto<BrowseDirectoryEntryDto> directories = await api.GetUserShareDirectoriesAsync(
                browse.BrowseId,
                location.DirectoryId,
                filter,
                recursive: false,
                cursor.DirectoryCursor,
                PageSize,
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(directories.Items.Select(item => (BrowserRow)new BrowserRow.Directory(item)));
            if (directories.NextCursor is not null)
            {
                next = new PageCursor(PageStage.Directories, directories.NextCursor, null);
            }
            else if (NeedsSeparateFilePage(rows.Count, directories.NextCursor, location.DirectoryId))
            {
                next = new PageCursor(PageStage.Files, null, null);
            }
            else if (rows.Count < PageSize && location.DirectoryId is { } directoryId)
            {
                PageDto<BrowseFileEntryDto> files = await api.GetUserShareFilesAsync(
                    browse.BrowseId,
                    directoryId,
                    filter,
                    cursor.FileCursor,
                    rows.Count == 0 ? ServerProtocol.DefaultPageSize : PageSize - rows.Count,
                    cancellationToken).ConfigureAwait(false);
                rows.AddRange(files.Items.Select(item => (BrowserRow)new BrowserRow.File(item, location.DisplayPath)));
                if (files.NextCursor is not null)
                    next = new PageCursor(PageStage.Files, null, files.NextCursor);
            }
        }
        else if (location.DirectoryId is { } directoryId)
        {
            PageDto<BrowseFileEntryDto> files = await api.GetUserShareFilesAsync(
                browse.BrowseId,
                directoryId,
                filter,
                cursor.FileCursor,
                ServerProtocol.DefaultPageSize,
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(files.Items.Select(item => (BrowserRow)new BrowserRow.File(item, location.DisplayPath)));
            if (files.NextCursor is not null)
                next = new PageCursor(PageStage.Files, null, files.NextCursor);
        }

        var loaded = new CachedPage(rows, next);
        AddToCache(key, loaded);
        return loaded;
    }

    private async Task NextPageAsync(CachedPage current, CancellationToken cancellationToken)
    {
        if (current.Next is null)
        {
            message = "End of this directory.";
            return;
        }
        if (pageIndex + 1 == pageHistory.Count)
            pageHistory.Add(current.Next.Value);
        pageIndex++;
        rowIndex = 0;
        _ = await CurrentPageAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PreviousPageAsync(CancellationToken cancellationToken)
    {
        if (pageIndex == 0)
        {
            message = "Start of this directory.";
            return;
        }
        pageIndex--;
        rowIndex = 0;
        _ = await CurrentPageAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Enter(BrowseDirectoryEntryDto directory)
    {
        locations.Add(new Location(directory.DirectoryId, directory.Name, directory.DisplayPath));
        ResetPages();
    }

    private void Leave()
    {
        if (locations.Count == 1)
        {
            message = "Already at the share root.";
            return;
        }
        locations.RemoveAt(locations.Count - 1);
        ResetPages();
    }

    private void Toggle(BrowserRow? row)
    {
        if (row is null) return;
        message = row switch
        {
            BrowserRow.Directory directory => cart.ToggleDirectory(directory.Value),
            BrowserRow.File file => cart.ToggleFile(file.Value, file.DisplayPath),
            _ => null,
        };
    }

    private async Task EditFilterAsync(CancellationToken cancellationToken)
    {
        Console.Write("\nFilter (empty clears): ");
        var value = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                message = "Filter unchanged.";
                return;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }

        string entered = value.ToString();
        filter = string.IsNullOrWhiteSpace(entered) ? null : entered.Trim();
        pageCache.Clear();
        cacheOrder.Clear();
        ResetPages();
    }

    internal static bool NeedsSeparateFilePage(
        int directoryRowCount,
        string? directoryNextCursor,
        long? directoryId)
        => directoryId is not null
           && directoryNextCursor is null
           && directoryRowCount == PageSize;

    private async Task<StartUserShareDownloadsResponseDto?> ReviewAndSubmitAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UserShareSelectionDto> selections = cart.ToSelections();
        if (selections.Count == 0)
        {
            message = "Nothing is selected.";
            return null;
        }

        try
        {
            Console.Clear();
            Console.WriteLine("Download selected shares?");
            Console.WriteLine();
            Console.WriteLine(
                $"{cart.TotalFileCount:N0} files ({UserCommandRunner.FormatBytes(cart.TotalBytes)}) from "
                + $"{cart.DirectoryCount:N0} folder roots and {cart.FileCount:N0} standalone files");
            if (cart.LockedBranchesSkipped > 0)
                Console.WriteLine($"Locked branches skipped: {cart.LockedBranchesSkipped:N0}");
            Console.WriteLine();
            Console.Write("Submit? [y/N] ");
            ConsoleKeyInfo answer = await ReadKeyAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            if (answer.Key != ConsoleKey.Y)
            {
                message = "Submission cancelled; the selection is unchanged.";
                return null;
            }
            return await api.StartUserShareDownloadsAsync(
                browse.BrowseId,
                new StartUserShareDownloadsRequestDto(Guid.NewGuid(), selections, options),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SockseekApiRequestException ex)
        {
            message = "Download was not submitted: " + Safe(ex.Message);
            return null;
        }
    }

    private void Render(CachedPage page)
    {
        Console.Clear();
        int width = TerminalWidth();
        string breadcrumb = Clip(
            string.Join(" / ", locations.Select(location => Safe(location.Name))),
            width);
        Console.WriteLine(breadcrumb);
        Console.WriteLine(new string('─', Math.Min(100, Math.Max(20, width))));
        if (filter is not null) Console.WriteLine($"Filter: {Safe(filter)}");
        if (page.Rows.Count == 0) Console.WriteLine("  (empty)");
        for (int index = 0; index < page.Rows.Count; index++)
        {
            BrowserRow row = page.Rows[index];
            string cursor = index == rowIndex ? ">" : " ";
            string marker = cart.Marker(row);
            string kind = row is BrowserRow.Directory ? "D" : "F";
            Console.WriteLine(
                $"{cursor}{marker} {kind} {Clip(Safe(row.Name), 44),-44} {Safe(Describe(row))}");
        }
        Console.WriteLine();
        Console.WriteLine(
            $"Selected: {cart.DirectoryCount:N0} folders, {cart.FileCount:N0} files"
            + $" · page {pageIndex + 1}"
            + (filter is null ? "" : " · filtered"));
        Console.WriteLine("↑↓ navigate · Enter open · Backspace up · Space select · / filter · D download · ? help · Q quit");
        if (!string.IsNullOrEmpty(message))
        {
            Console.WriteLine(Safe(message));
            message = null;
        }
    }

    private BrowserRow? CurrentRow(CachedPage page)
        => page.Rows.Count == 0 ? null : page.Rows[rowIndex];

    private void ResetPages()
    {
        pageHistory.Clear();
        pageHistory.Add(PageCursor.First);
        pageIndex = 0;
        rowIndex = 0;
    }

    private void AddToCache(PageCacheKey key, CachedPage page)
    {
        pageCache[key] = page;
        cacheOrder.AddLast(key);
        while (cacheOrder.Count > PageCacheCapacity)
        {
            PageCacheKey oldest = cacheOrder.First!.Value;
            cacheOrder.RemoveFirst();
            pageCache.Remove(oldest);
        }
    }

    private void Touch(PageCacheKey key)
    {
        LinkedListNode<PageCacheKey>? node = cacheOrder.Find(key);
        if (node is null) return;
        cacheOrder.Remove(node);
        cacheOrder.AddLast(node);
    }

    private static string Safe(string? value)
        => UserProfileText.NormalizeDescription(value).Replace('\n', ' ');

    internal static string Describe(BrowserRow row)
        => row switch
        {
            BrowserRow.Directory directory =>
                $"{directory.Value.RecursiveFileCount:N0} files · "
                + UserCommandRunner.FormatBytes(directory.Value.RecursiveFileBytes)
                + (directory.Value.LockedDescendantCount > 0
                    ? $" · {directory.Value.LockedDescendantCount:N0} locked"
                    : "")
                + (directory.Value.Visibility == ShareVisibility.Locked ? " · locked" : ""),
            BrowserRow.File file =>
                UserCommandRunner.FormatBytes(file.Value.File.Size)
                + $" · {file.Value.File.Extension ?? "file"}"
                + (file.Value.Visibility == ShareVisibility.Locked ? " · locked" : ""),
            _ => "",
        };

    private static string Clip(string value, int width)
    {
        if (value.Length <= width)
            return value;
        if (width <= 1)
            return "…";
        int length = width - 1;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length] + "…";
    }

    private static int TerminalWidth()
    {
        try { return Math.Max(20, Console.WindowWidth - 1); }
        catch (IOException) { return 79; }
    }

    private static async Task<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (!Console.KeyAvailable)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        return Console.ReadKey(intercept: true);
    }

    private sealed record Location(long? DirectoryId, string Name, string DisplayPath);
    private sealed record CachedPage(IReadOnlyList<BrowserRow> Rows, PageCursor? Next);
    private readonly record struct PageCacheKey(long? DirectoryId, string? Filter, PageCursor Cursor);
    private readonly record struct PageCursor(PageStage Stage, string? DirectoryCursor, string? FileCursor)
    {
        public static PageCursor First => new(PageStage.Directories, null, null);
    }
    private enum PageStage { Directories, Files }

    internal abstract record BrowserRow
    {
        public abstract string Name { get; }
        public abstract long Size { get; }
        public abstract ShareVisibility Visibility { get; }

        public sealed record Directory(BrowseDirectoryEntryDto Value) : BrowserRow
        {
            public override string Name => Value.Name;
            public override long Size => Value.RecursiveFileBytes;
            public override ShareVisibility Visibility => Value.Visibility;
        }

        public sealed record File(BrowseFileEntryDto Value, string DisplayPath) : BrowserRow
        {
            public override string Name => Value.File.Name;
            public override long Size => Value.File.Size;
            public override ShareVisibility Visibility => Value.Visibility;
        }
    }
}

/// <summary>Compact antichain state independent of rendered/paged rows.</summary>
internal sealed class ShareSelectionCart
{
    private readonly Dictionary<long, SelectedDirectory> directories = [];
    private readonly Dictionary<long, SelectedFile> files = [];

    public int DirectoryCount => directories.Count;
    public int FileCount => files.Count;
    public long TotalFileCount => SaturatingSum(
        directories.Values.Select(directory => directory.FileCount).Append(files.Count));
    public long TotalBytes => SaturatingSum(
        directories.Values.Select(directory => directory.Bytes)
            .Concat(files.Values.Select(file => file.Bytes)));
    public long LockedBranchesSkipped => SaturatingSum(
        directories.Values.Select(directory => directory.LockedBranches));

    public string? ToggleDirectory(BrowseDirectoryEntryDto directory)
    {
        if (directory.Visibility == ShareVisibility.Locked)
            return "Locked folders cannot be selected.";
        if (directories.Remove(directory.DirectoryId))
            return "Folder removed from the selection.";
        if (CoveringDirectory(directory.DisplayPath) is not null)
            return "This folder is covered by a selected ancestor; deselect that ancestor first.";

        directories[directory.DirectoryId] = new SelectedDirectory(
            directory.DisplayPath,
            directory.RecursiveFileCount,
            directory.RecursiveFileBytes,
            directory.LockedDescendantCount);
        foreach (long id in directories
                     .Where(pair => pair.Key != directory.DirectoryId
                         && Below(pair.Value.DisplayPath, directory.DisplayPath))
                     .Select(pair => pair.Key).ToArray())
            directories.Remove(id);
        foreach (long id in files
                     .Where(pair => Below(pair.Value.DisplayPath, directory.DisplayPath))
                     .Select(pair => pair.Key).ToArray())
            files.Remove(id);
        return "Folder subtree selected.";
    }

    public string? ToggleFile(BrowseFileEntryDto file, string directoryDisplayPath)
    {
        if (file.Visibility == ShareVisibility.Locked)
            return "Locked files cannot be selected.";
        if (files.Remove(file.FileId))
            return "File removed from the selection.";
        string path = directoryDisplayPath + "\\" + file.File.Name;
        if (CoveringDirectory(path) is not null)
            return "This file is covered by a selected folder; deselect that folder first.";
        files[file.FileId] = new SelectedFile(path, file.File.Size);
        return "File selected.";
    }

    public IReadOnlyList<UserShareSelectionDto> ToSelections()
        => directories.Keys.Order().Select(id => (UserShareSelectionDto)new UserShareDirectorySelectionDto(id))
            .Concat(files.Keys.Order().Select(id => (UserShareSelectionDto)new UserShareFileSelectionDto(id)))
            .ToArray();

    public string Marker(InteractiveShareBrowser.BrowserRow row)
        => row switch
        {
            InteractiveShareBrowser.BrowserRow.Directory directory
                when directories.ContainsKey(directory.Value.DirectoryId) => "[x]",
            InteractiveShareBrowser.BrowserRow.File file
                when files.ContainsKey(file.Value.FileId) => "[x]",
            InteractiveShareBrowser.BrowserRow.Directory directory
                when CoveringDirectory(directory.Value.DisplayPath) is not null => "[~]",
            InteractiveShareBrowser.BrowserRow.File file
                when CoveringDirectory(file.DisplayPath + "\\" + file.Value.File.Name) is not null => "[~]",
            _ => "[ ]",
        };

    private string? CoveringDirectory(string path)
        => directories.Values
            .Select(directory => directory.DisplayPath)
            .FirstOrDefault(directory => SameOrBelow(path, directory));

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values)
        {
            if (value >= long.MaxValue - total)
                return long.MaxValue;
            total += value;
        }
        return total;
    }

    private static bool Below(string candidate, string ancestor)
        => candidate.Length > ancestor.Length
            && candidate.StartsWith(ancestor, StringComparison.Ordinal)
            && candidate[ancestor.Length] == '\\';

    private static bool SameOrBelow(string candidate, string ancestor)
        => string.Equals(candidate, ancestor, StringComparison.Ordinal) || Below(candidate, ancestor);

    private sealed record SelectedDirectory(
        string DisplayPath,
        long FileCount,
        long Bytes,
        long LockedBranches);

    private sealed record SelectedFile(string DisplayPath, long Bytes);
}
