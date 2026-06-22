using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using System.Text;

namespace Sockseek.Cli;

public class InteractiveModeManager
{
    private readonly Job      job;
    private readonly JobList queue;
    private readonly Func<AlbumFolder, Task<RetrievedFolder>> retrieveFolderCallback;

    private readonly List<(AlbumFolder Folder, int Index)> original;
    private List<(AlbumFolder Folder, int Index)> filterList;

    // Keep the search candidate slots stable while `cd` moves each slot's current
    // folder through cached snapshots. This prevents folder navigation from making
    // the original parent/child snapshots disappear.
    private readonly Dictionary<string, AlbumFolder> folderSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> currentFolderKeys = new();

    private readonly bool            canRetrieve;
    private readonly HashSet<string> retrievedFolders;
    private string?  filterStr;
    private int      savedPos;

    public enum RunAction
    {
        Accept,
        SkipCurrent,
        SkipRemainingNewPrompts,
        ExitInteractiveMode,
    }

    public record RunResult(
        RunAction   Action,
        int         Index,
        AlbumFolder? Folder,
        bool         RetrieveCurrentFolder,
        string?      FilterStr);

    public record RetrievedFolder(AlbumFolder Folder, int NewFilesFoundCount);

    public InteractiveModeManager(
        Job job,
        JobList     queue,
        List<AlbumFolder> folders,
        bool            canRetrieve,
        HashSet<string> retrievedFolders,
        Func<AlbumFolder, Task<RetrievedFolder>> retrieveFolderCallback,
        string? filterStr = null)
    {
        this.job                    = job;
        this.queue                  = queue;
        this.canRetrieve            = canRetrieve;
        this.retrievedFolders       = retrievedFolders;
        this.filterStr              = filterStr;
        this.retrieveFolderCallback = retrieveFolderCallback;

        original   = folders.Select((f, i) => (Folder: f, Index: i)).ToList();
        filterList = original;
        foreach (var (folder, index) in original)
        {
            StoreFolderSnapshot(folder);
            currentFolderKeys[index] = FolderKey(folder);
        }

        if (filterStr != null)
        {
            filterList = original.Where(e => FolderMatchesFilter(CurrentFolder(e), filterStr)).ToList();
            if (filterList.Count == 0)
            {
                Console.WriteLine($"No matches for query: {filterStr}");
                this.filterStr = null;
                filterList     = original;
            }
        }
    }

    public async Task<RunResult> Run()
    {
        ConsoleInputManager.GlobalCancelEnabled = false;
        try
        {
            int aidx = 0;
            string retrieveAll1 = canRetrieve ? "| [r]           " : "";
            string retrieveAll2 = canRetrieve ? "| Load All Files" : "";
            string? statusLine = null;
            Console.WriteLine();
        Printing.WriteLine($" [Up/p] | [Down/n] | [Enter] {retrieveAll1} | [s/q/Esc] | [Q/S]     | [h]", ConsoleColor.Cyan, force: true);
        Printing.WriteLine($" Prev   | Next     | Accept  {retrieveAll2} | Skip      | Skip Rest | More Help", ConsoleColor.Cyan, force: true);

        Console.WriteLine();
        savedPos = GetCursorTopOrDefault();

        while (true)
        {
            var entry    = filterList[aidx];
            var index    = entry.Index;
            var folder   = CurrentFolder(entry);
            var username = folder.Username;

            if (filterStr != null)
            {
                Printing.Write($"Filter: ", ConsoleColor.White, force: true);
                Printing.Write($"{filterStr}\n", ConsoleColor.Cyan, force: true);
                Console.WriteLine();
            }

            Console.ResetColor();
            Printing.WriteLine($"[{aidx + 1} / {filterList.Count}]", ConsoleColor.DarkGray, force: true);
            Printing.PrintAlbum(folder, indices: true, force: true);
            Console.WriteLine();
            if (statusLine != null)
            {
                Printing.WriteLine(statusLine, ConsoleColor.Green, force: true);
                Console.WriteLine();
                statusLine = null;
            }

        Loop:
            string userInputStr = (await InteractiveModeInput()).Trim();
            string commandInput = userInputStr;
            string command      = commandInput.ToLowerInvariant();
            string options      = "";
            string subfolder    = "";
            bool navigatingFolder = false;

            var commandsWithArgs = new string[] { "d:", "f:", "cd" };
            foreach (var cmd in commandsWithArgs)
            {
                if (command.StartsWith(cmd))
                {
                    options = commandInput[cmd.Length..].Trim();
                    command = command[..cmd.Length].TrimEnd(':');
                    break;
                }
            }

            switch (command)
            {
                case "p":
                    ClearOutput(savedPos);
                    aidx = (aidx + filterList.Count - 1) % filterList.Count;
                    break;

                case "n":
                    ClearOutput(savedPos);
                    aidx = (aidx + 1) % filterList.Count;
                    break;

                case "c":
                    if (ConsoleInputManager.OnCancelRequested != null)
                    {
                        await ConsoleInputManager.OnCancelRequested();
                        ClearOutput(savedPos);
                    }
                    goto Loop;

                case "s" when commandInput == "S":
                    Printing.WriteLine($"Skipped all remaining new album prompts.", ConsoleColor.Yellow, force: true);
                    return new RunResult(RunAction.SkipRemainingNewPrompts, -1, null, false, filterStr);

                case "s":
                    Printing.WriteLine($"Skipped: {job.ToString(noInfo: true)}", ConsoleColor.Yellow, force: true);
                    return new RunResult(RunAction.SkipCurrent, -1, null, false, filterStr);

                case "q" when commandInput == "Q":
                    Printing.WriteLine($"Skipped all remaining new album prompts.", ConsoleColor.Yellow, force: true);
                    return new RunResult(RunAction.SkipRemainingNewPrompts, -1, null, false, filterStr);

                case "q":
                    Printing.WriteLine($"Skipped: {job.ToString(noInfo: true)}", ConsoleColor.Yellow, force: true);
                    return new RunResult(RunAction.SkipCurrent, -1, null, false, filterStr);

                case "y":
                    Printing.WriteLine($"Downloading: {folder.FolderPath}", ConsoleColor.Green, force: true);
                    Printing.WriteLine("Exiting interactive mode", ConsoleColor.Gray, force: true);
                    return new RunResult(RunAction.ExitInteractiveMode, index, folder, true, filterStr);

                case "r":
                    if (!canRetrieve) goto Loop;
                    subfolder = folder.FolderPath;
                    goto case "complete_folder";

                case "cd":
                    string currentFolder = folder.FolderPath;
                    if (options == "..")
                    {
                        if (!canRetrieve) goto Loop;
                        var parentFolder = NormalizeRemoteFolderPath(Utils.GetDirectoryNameSlsk(currentFolder));
                        if (string.IsNullOrEmpty(parentFolder))
                        {
                            Console.WriteLine("This is the top directory");
                            goto Loop;
                        }
                        subfolder = parentFolder;
                        navigatingFolder = true;
                        goto case "complete_folder";
                    }
                    else
                    {
                        var subdir     = CombineRemoteFolderPath(currentFolder, options);
                        var childFiles = folder.Files
                            .Where(af => IsInFolderPath(af.ResolvedTarget!.Filename, subdir))
                            .ToList();

                        if (childFiles.Count == 0)
                        {
                            Console.WriteLine("No such directory");
                            goto Loop;
                        }

                        subfolder = subdir;
                        var childFolder = GetFolderSnapshot(username, subfolder)
                            ?? new AlbumFolder(username, subfolder, childFiles)
                            {
                                IsFullyRetrieved = folder.IsFullyRetrieved,
                            };
                        childFolder = SetCurrentFolder(index, childFolder);

                        statusLine = $"Changed folder: {childFolder.FolderPath}";
                        ClearOutput(savedPos);
                        break;
                    }

                case "complete_folder":
                    if (canRetrieve)
                    {
                        string folderKey = username + '\\' + subfolder;
                        var targetFolder = string.Equals(folder.FolderPath, subfolder, StringComparison.OrdinalIgnoreCase)
                            ? folder
                            : GetFolderSnapshot(username, subfolder)
                            ?? new AlbumFolder(username, subfolder, folder.Files
                                .Where(af => IsInFolderPath(af.ResolvedTarget!.Filename, subfolder))
                                .ToList());

                        if (!targetFolder.IsFullyRetrieved)
                        {
                            var retrieved = await retrieveFolderCallback(targetFolder);
                            var retrievedFolder = SetCurrentFolder(index, retrieved.Folder);
                            retrievedFolders.Add(folderKey);
                            statusLine = navigatingFolder
                                ? $"Changed folder: {retrievedFolder.FolderPath}"
                                : retrieved.NewFilesFoundCount == 0
                                ? "Retrieved folder: no new files found."
                                : $"Retrieved folder: found {retrieved.NewFilesFoundCount} more {(retrieved.NewFilesFoundCount == 1 ? "file" : "files")}.";

                            ClearOutput(savedPos);
                            break;
                        }
                        else
                        {
                            targetFolder = SetCurrentFolder(index, targetFolder);
                            retrievedFolders.Add(folderKey);
                            statusLine = navigatingFolder
                                ? $"Changed folder: {targetFolder.FolderPath}"
                                : "Already retrieved this folder.";
                            ClearOutput(savedPos);
                            break;
                        }
                    }
                    goto Loop;
                
                case "d":
                    if (options.Length == 0)
                    {
                        Printing.WriteLine($"Downloading: {folder.FolderPath}", ConsoleColor.Green, force: true);
                        return new RunResult(RunAction.Accept, index, folder, true, filterStr);
                    }
                    if (TryBuildSelectedFolder(folder, options, out var trimmedFolder, out var error))
                    {
                        Printing.WriteLine($"Downloading: {folder.FolderPath} ({trimmedFolder.Files.Count} selected files)", ConsoleColor.Green, force: true);
                        return new RunResult(RunAction.Accept, index, trimmedFolder, false, filterStr);
                    }
                    Console.WriteLine($"Error: {error}");
                    goto Loop;

                case "f":
                    if (string.IsNullOrWhiteSpace(options))
                    {
                        var prompted = ReadFilterPrompt();
                        if (prompted == null)
                        {
                            ClearOutput(savedPos);
                            goto Loop;
                        }

                        options = prompted.Trim();
                    }

                    aidx = 0;
                    if (string.IsNullOrWhiteSpace(options))
                    {
                        filterList = original;
                        filterStr  = null;
                        ClearOutput(savedPos);
                    }
                    else
                    {
                        var filtered = original.Where(e => FolderMatchesFilter(CurrentFolder(e), options)).ToList();
                        if (filtered.Count == 0)
                        {
                            Console.WriteLine($"No matches for query: {options}");
                            filterStr  = null;
                            filterList = original;
                            goto Loop;
                        }
                        else
                        {
                            filterStr  = options;
                            filterList = filtered;
                            ClearOutput(savedPos);
                        }
                    }
                    break;

                case "h":
                    Console.ForegroundColor = ConsoleColor.Green;
                    Help.PrintHelp("shortcuts");
                    Console.ResetColor();
                    goto Loop;

                case "":
                    Printing.WriteLine($"Downloading: {folder.FolderPath}", ConsoleColor.Green, force: true);
                    return new RunResult(RunAction.Accept, index, folder, true, filterStr);

                default:
                    Console.WriteLine($"Error: Invalid input {userInputStr}");
                    goto Loop;
            }
        }
        }
        finally
        {
            ConsoleInputManager.GlobalCancelEnabled = true;
        }
    }

    private AlbumFolder CurrentFolder((AlbumFolder Folder, int Index) entry)
    {
        if (currentFolderKeys.TryGetValue(entry.Index, out var key)
            && folderSnapshots.TryGetValue(key, out var folder))
        {
            return folder;
        }

        StoreFolderSnapshot(entry.Folder);
        currentFolderKeys[entry.Index] = FolderKey(entry.Folder);
        return entry.Folder;
    }

    private AlbumFolder? GetFolderSnapshot(string username, string folderPath)
        => folderSnapshots.TryGetValue(FolderKey(username, folderPath), out var folder)
            ? folder
            : null;

    private AlbumFolder SetCurrentFolder(int originalIndex, AlbumFolder folder)
    {
        folder = NormalizeFolderSnapshot(folder);
        StoreFolderSnapshot(folder);
        currentFolderKeys[originalIndex] = FolderKey(folder);
        return folder;
    }

    private void StoreFolderSnapshot(AlbumFolder folder)
    {
        folder = NormalizeFolderSnapshot(folder);
        folderSnapshots[FolderKey(folder)] = folder;
    }

    private static string FolderKey(AlbumFolder folder)
        => FolderKey(folder.Username, folder.FolderPath);

    private static string FolderKey(string username, string folderPath)
        => username + "\\" + NormalizeRemoteFolderPath(folderPath);

    private static bool FolderMatchesFilter(AlbumFolder folder, string filter)
    {
        return folder.Files.Any(af => af.ResolvedTarget!.Filename.ContainsIgnoreCase(filter));
    }

    private static bool IsInFolderPath(string filename, string folderPath)
    {
        filename = NormalizeRemoteFolderPath(filename);
        folderPath = NormalizeRemoteFolderPath(folderPath).TrimEnd('\\');
        return filename.StartsWith(folderPath + "\\", StringComparison.OrdinalIgnoreCase)
            || filename.Equals(folderPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineRemoteFolderPath(string parent, string child)
        => NormalizeRemoteFolderPath(parent.TrimEnd('\\') + "\\" + child.Trim('\\', '/'));

    private static string NormalizeRemoteFolderPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        path = path.Replace('/', '\\').TrimEnd('\\').Trim();
        while (path.Contains(@"\\", StringComparison.Ordinal))
            path = path.Replace(@"\\", @"\", StringComparison.Ordinal);
        return path;
    }

    private static AlbumFolder NormalizeFolderSnapshot(AlbumFolder folder)
    {
        var folderPath = NormalizeRemoteFolderPath(folder.FolderPath);
        if (string.Equals(folderPath, folder.FolderPath, StringComparison.Ordinal))
            return folder;

        return new AlbumFolder(folder.Username, folderPath, folder.Files)
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };
    }

    private async Task<string> InteractiveModeInput()
    {
        var buffer    = new StringBuilder();
        var firstKey  = true;
        var cursorPos = 0;

        while (true)
        {
            var key = await ConsoleInputManager.ReadKeyAsync();

            if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.Escape)
            {
                if (buffer.Length > 0)
                {
                    TrySetCursorPosition(0, GetCursorTopOrDefault());
                    Console.Write(new string(' ', buffer.Length + 1));
                    TrySetCursorPosition(0, GetCursorTopOrDefault());
                }
                if (key.Key == ConsoleKey.DownArrow)  return "n";
                else if (key.Key == ConsoleKey.UpArrow) return "p";
                else return "q";
            }

            if (firstKey && "fpnyqrsh".Contains(char.ToLowerInvariant(key.KeyChar)))
            {
                if (char.ToLowerInvariant(key.KeyChar) == 'f')
                    ConsoleInputManager.PrepareDirectPromptInput();

                return key.KeyChar.ToString();
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }
            else if (key.Key == ConsoleKey.LeftArrow && cursorPos > 0)
            {
                cursorPos--;
                TrySetCursorPosition(GetCursorLeftOrDefault() - 1, GetCursorTopOrDefault());
            }
            else if (key.Key == ConsoleKey.RightArrow && cursorPos < buffer.Length)
            {
                cursorPos++;
                TrySetCursorPosition(GetCursorLeftOrDefault() + 1, GetCursorTopOrDefault());
            }
            else if (key.Key == ConsoleKey.Backspace && cursorPos > 0)
            {
                buffer.Remove(cursorPos - 1, 1);
                cursorPos--;
                var rest = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                Console.Write("\b" + rest + " ");
                TrySetCursorPosition(GetCursorLeftOrDefault() - (rest.Length + 1), GetCursorTopOrDefault());
            }
            else if (key.Key == ConsoleKey.Delete && cursorPos < buffer.Length)
            {
                buffer.Remove(cursorPos, 1);
                var rest = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                Console.Write(rest + " ");
                TrySetCursorPosition(GetCursorLeftOrDefault() - (rest.Length + 1), GetCursorTopOrDefault());
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Insert(cursorPos, key.KeyChar);
                cursorPos++;
                var rest = buffer.ToString(cursorPos - 1, buffer.Length - (cursorPos - 1));
                Console.Write(rest);
                TrySetCursorPosition(GetCursorLeftOrDefault() - (rest.Length - 1), GetCursorTopOrDefault());
            }

            firstKey = false;
        }
    }

    private static int GetCursorLeftOrDefault()
    {
        try { return Console.CursorLeft; }
        catch { return 0; }
    }

    private static void TrySetCursorPosition(int left, int top)
    {
        try { Console.SetCursorPosition(Math.Max(0, left), Math.Max(0, top)); }
        catch { }
    }

    private static string? ReadFilterPrompt()
        => ConsoleInputManager.ReadPromptInput("Filter: ");

    internal static bool TryBuildSelectedFolder(
        AlbumFolder folder,
        string options,
        out AlbumFolder selectedFolder,
        out string error)
    {
        selectedFolder = folder;
        error = "";

        try
        {
            var indices = options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(option => ParseSelectionRange(option, folder.Files.Count))
                .Distinct()
                .ToArray();

            if (indices.Length == 0 || indices.Any(i => i < 1 || i > folder.Files.Count))
            {
                error = "Invalid range";
                return false;
            }

            var selectedFiles = indices.Select(i => folder.Files[i - 1]).ToList();
            selectedFolder = new AlbumFolder(folder.Username, folder.FolderPath, selectedFiles)
            {
                IsFullyRetrieved = folder.IsFullyRetrieved,
            };
            return true;
        }
        catch
        {
            error = "Invalid range";
            return false;
        }
    }

    private static IEnumerable<int> ParseSelectionRange(string option, int fileCount)
    {
        if (!option.Contains('-'))
            return [int.Parse(option)];

        var parts = option.Split('-');
        if (parts.Length != 2)
            throw new FormatException("Invalid range");

        int start = string.IsNullOrEmpty(parts[0]) ? 1 : int.Parse(parts[0]);
        int end = string.IsNullOrEmpty(parts[1]) ? fileCount : int.Parse(parts[1]);
        if (end < start)
            throw new FormatException("Invalid range");

        return Enumerable.Range(start, end - start + 1);
    }

    private static int GetCursorTopOrDefault()
    {
        try
        {
            return Console.CursorTop;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static bool TryGetCursorTop(out int top)
    {
        try
        {
            top = Console.CursorTop;
            return true;
        }
        catch (IOException)
        {
            top = 0;
            return false;
        }
        catch (InvalidOperationException)
        {
            top = 0;
            return false;
        }
    }

    private static void ClearCurrentLine()
    {
        if (!TryGetCursorTop(out int line)) return;

        try
        {
            Console.SetCursorPosition(0, line);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, line);
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ClearOutput(int toPos)
    {
        if (Console.IsOutputRedirected || !TryGetCursorTop(out int pos)) return;

        try
        {
            while (pos > toPos && pos > 0)
            {
                Console.SetCursorPosition(0, pos - 1);
                ClearCurrentLine();
                pos--;
            }
            Console.SetCursorPosition(0, toPos);
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
