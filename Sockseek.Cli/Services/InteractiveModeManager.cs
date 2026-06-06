using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using System.Text;

namespace Sockseek.Cli;

public class InteractiveModeManager
{
    private readonly Job      job;
    private readonly JobList queue;
    private readonly Func<AlbumFolder, Task<int>> retrieveFolderCallback;

    private readonly List<(AlbumFolder Folder, int Index)> original;
    private List<(AlbumFolder Folder, int Index)> filterList;

    private readonly bool            canRetrieve;
    private readonly HashSet<string> retrievedFolders;
    private string?  filterStr;
    private int      savedPos;

    // ── return codes from Run() ───────────────────────────────────────────────
    // index == -1  → user pressed 's' (skip this album)
    // index == -2  → user pressed 'q' (quit program)
    // index >= 0   → accepted folder; folder is the (possibly trimmed) chosen AlbumFolder
    // exitInteractiveMode == true → engine should disable config.interactiveMode
    // TODO: Make explicit rather than -1, -2.

    public record RunResult(
        int         Index,
        AlbumFolder? Folder,
        bool         RetrieveCurrentFolder,
        bool         ExitInteractiveMode,
        string?      FilterStr);

    public InteractiveModeManager(
        Job job,
        JobList     queue,
        List<AlbumFolder> folders,
        bool            canRetrieve,
        HashSet<string> retrievedFolders,
        Func<AlbumFolder, Task<int>> retrieveFolderCallback,
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

        if (filterStr != null)
        {
            filterList = original.Where(e => e.Folder.Files.Any(af => af.ResolvedTarget!.Filename.ContainsIgnoreCase(filterStr))).ToList();
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
            Console.WriteLine();
        Printing.WriteLine($" [Up/p] | [Down/n] | [Enter] {retrieveAll1} | [s]  | [Esc/q] | [h]", ConsoleColor.Green, force: true);
        Printing.WriteLine($" Prev   | Next     | Accept  {retrieveAll2} | Skip | Quit    | Help", ConsoleColor.Green, force: true);

        Console.WriteLine();
        savedPos = Console.CursorTop;

        while (true)
        {
            var entry    = filterList[aidx];
            var folder   = entry.Folder;
            var index    = entry.Index;
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

        Loop:
            string userInputStr = (await InteractiveModeInput()).Trim();
            string command      = userInputStr.ToLowerInvariant();
            string options      = "";
            string subfolder    = "";

            var commandsWithArgs = new string[] { "d:", "f:", "cd" };
            foreach (var cmd in commandsWithArgs)
            {
                if (command.StartsWith(cmd))
                {
                    options = command[cmd.Length..].Trim();
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

                case "s":
                    Printing.WriteLine($"Skipped: {job.ToString(noInfo: true)}", ConsoleColor.Yellow, force: true);
                    return new RunResult(-1, null, false, false, null);

                case "q":
                    return new RunResult(-2, null, false, false, null);

                case "y":
                    Printing.WriteLine($"Downloading: {folder.FolderPath}", ConsoleColor.Green, force: true);
                    Printing.WriteLine("Exiting interactive mode", ConsoleColor.Gray, force: true);
                    // job.PrintLines(); // Removed as logging is handled centrally now
                    return new RunResult(index, folder, true, ExitInteractiveMode: true, filterStr);

                case "r":
                    if (!canRetrieve) goto Loop;
                    subfolder = folder.FolderPath;
                    goto case "complete_folder";

                case "cd":
                    string currentFolder = folder.FolderPath;
                    if (options == "..")
                    {
                        if (!canRetrieve) goto Loop;
                        var parentFolder = Utils.GetDirectoryNameSlsk(currentFolder);
                        if (string.IsNullOrEmpty(parentFolder))
                        {
                            Console.WriteLine("This is the top directory");
                            goto Loop;
                        }
                        subfolder = parentFolder;
                        goto case "complete_folder";
                    }
                    else
                    {
                        var subdir     = currentFolder + '\\' + options;
                        var hasMatches = folder.Files.Any(af => af.ResolvedTarget!.Filename.StartsWith(subdir, StringComparison.OrdinalIgnoreCase));

                        if (!hasMatches)
                        {
                            Console.WriteLine("No such directory");
                            goto Loop;
                        }

                        subfolder = subdir;
                        folder.Files.RemoveAll(af => !af.ResolvedTarget!.Filename.StartsWith(subdir, StringComparison.OrdinalIgnoreCase));

                        string folderKey = username + '\\' + currentFolder;
                        if (retrievedFolders.Remove(folderKey))
                        {
                            retrievedFolders.Add(username + '\\' + subdir);
                        }

                        break;
                    }

                case "complete_folder":
                    if (canRetrieve)
                    {
                        string folderKey = username + '\\' + subfolder;
                        if (!folder.IsFullyRetrieved)
                        {
                            int newFiles = await retrieveFolderCallback(folder);
                            retrievedFolders.Add(folderKey);
                            if (newFiles == 0)
                            {
                                goto Loop;
                            }
                            else
                            {
                                Console.WriteLine($"Found {newFiles} more files in the folder:");
                                ClearOutput(savedPos);
                                break;
                            }
                        }
                        else
                        {
                            Console.WriteLine("Already retrieved this folder.");
                            goto Loop;
                        }
                    }
                    goto Loop;
                
                case "d":
                    if (options.Length == 0)
                    {
                        Printing.WriteLine($"Downloading: {folder.FolderPath}", ConsoleColor.Green, force: true);
                        return new RunResult(index, folder, true, false, filterStr);
                    }
                    if (TryBuildSelectedFolder(folder, options, out var trimmedFolder, out var error))
                    {
                        Printing.WriteLine($"Downloading: {folder.FolderPath} ({trimmedFolder.Files.Count} selected files)", ConsoleColor.Green, force: true);
                        return new RunResult(index, trimmedFolder, false, false, filterStr);
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
                        var filtered = original.Where(e => e.Folder.Files.Any(af => af.ResolvedTarget!.Filename.ContainsIgnoreCase(options))).ToList();
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
                    return new RunResult(index, folder, true, false, filterStr);

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
                    Console.SetCursorPosition(0, Console.CursorTop);
                    Console.Write(new string(' ', buffer.Length + 1));
                    Console.SetCursorPosition(0, Console.CursorTop);
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
                Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
            }
            else if (key.Key == ConsoleKey.RightArrow && cursorPos < buffer.Length)
            {
                cursorPos++;
                Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
            }
            else if (key.Key == ConsoleKey.Backspace && cursorPos > 0)
            {
                buffer.Remove(cursorPos - 1, 1);
                cursorPos--;
                var rest = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                Console.Write("\b" + rest + " ");
                Console.SetCursorPosition(Console.CursorLeft - (rest.Length + 1), Console.CursorTop);
            }
            else if (key.Key == ConsoleKey.Delete && cursorPos < buffer.Length)
            {
                buffer.Remove(cursorPos, 1);
                var rest = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                Console.Write(rest + " ");
                Console.SetCursorPosition(Console.CursorLeft - (rest.Length + 1), Console.CursorTop);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Insert(cursorPos, key.KeyChar);
                cursorPos++;
                var rest = buffer.ToString(cursorPos - 1, buffer.Length - (cursorPos - 1));
                Console.Write(rest);
                Console.SetCursorPosition(Console.CursorLeft - (rest.Length - 1), Console.CursorTop);
            }

            firstKey = false;
        }
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
            selectedFolder = new AlbumFolder(folder.Username, folder.FolderPath, selectedFiles);
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

    private static void ClearCurrentLine()
    {
        int line = Console.CursorTop;
        Console.SetCursorPosition(0, Console.CursorTop);
        Console.Write(new string(' ', Console.BufferWidth));
        Console.SetCursorPosition(0, line);
    }

    private static void ClearOutput(int toPos)
    {
        if (Console.IsOutputRedirected) return;
        int pos = Console.CursorTop;
        while (pos > toPos && pos > 0)
        {
            Console.SetCursorPosition(0, pos - 1);
            ClearCurrentLine();
            pos--;
        }
        Console.SetCursorPosition(0, toPos);
    }
}
