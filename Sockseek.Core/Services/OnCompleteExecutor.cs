using System.Diagnostics;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public static class OnCompleteExecutor
{
    private static readonly SemaphoreSlim _lockingSemaphore = new(1, 1);

    private struct CommandConfig
    {
        public string  Command                { get; set; }
        public bool    UseShellExecute        { get; set; }
        public bool    CreateNoWindow         { get; set; }
        public bool    OnlyTrackOnComplete    { get; set; }
        public bool    OnlyAlbumOnComplete    { get; set; }
        public bool    ReadOutput             { get; set; }
        public bool    UseOutputToUpdateIndex { get; set; }
        public int?    RequiredResultCode     { get; set; }
        public bool    UseLocking             { get; set; }
    }

    private struct ProcessResult
    {
        public int     ExitCode { get; set; }
        public string? Stdout   { get; set; }
        public string? Stderr   { get; set; }
    }

    private readonly record struct OnCompleteContext(FileManagerContext Variables, string? TagSourcePath);

    // Execute on-complete actions for a job.
    // song is null when called for an album-level completion (no individual song).
    public static async Task ExecuteAsync(Job job, SongJob? song, JobContext ctx, JobOutcome outcome)
    {
        if (!job.Config.HasOnComplete || job.Config.Output.OnComplete == null)
            return;

        bool isAlbumOnComplete = IsAlbumOnComplete(job, song);

        // Build a FileManagerContext for variable substitution.
        string extractorName = job.Config.Extraction.InputType.ToString();
        string inputSource   = job.Config.Extraction.Input ?? "";
        string outputDir     = job.Config.Output.ParentDir ?? "";
        string configDir     = job.Config.RuntimePathContext.ConfigDir ?? "";

        var onCompleteContext = song != null
            ? BuildSongOnCompleteContext(song, job)
            : job is AlbumJob albumJob
                ? BuildAlbumOnCompleteContext(albumJob)
                : BuildJobOnCompleteContext(job);

        onCompleteContext = onCompleteContext with
        {
            Variables = onCompleteContext.Variables with
            {
                ExtractorName = extractorName,
                InputSource = inputSource,
                OutputDir = outputDir,
                ConfigDir = configDir,
            },
        };
        onCompleteContext = onCompleteContext with
        {
            Variables = ApplyOutcomeToContext(onCompleteContext.Variables, outcome),
        };

        bool needUpdateIndex    = false;
        ProcessResult? firstCommandResult = null;
        ProcessResult? prevCommandResult  = null;

        for (int i = 0; i < job.Config.Output.OnComplete.Count; i++)
        {
            string rawCommand = job.Config.Output.OnComplete[i];
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            CommandConfig config = ParseCommandFlags(rawCommand);

            if (!ShouldExecuteCommand(config, outcome, isAlbumOnComplete))
                continue;

            string preparedCommand = PrepareCommandString(config.Command, onCompleteContext, prevCommandResult, firstCommandResult);
            if (string.IsNullOrWhiteSpace(preparedCommand))
            {
                SockseekLog.Jobs.Warn($"{OnCompleteLogPrefix(job, song)} skipping on-complete action {i + 1} because the prepared command is empty after variable replacement.");
                continue;
            }

            (string fileName, List<string> argList, string argString) = ParseFileNameAndArguments(preparedCommand);
            ProcessStartInfo startInfo = ConfigureProcessStartInfo(fileName, argList, argString, config);

            ProcessResult? currentResult = null;
            bool acquiredLock = false;

            try
            {
                if (config.UseLocking)
                {
                    SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} on-complete [{i + 1}/{job.Config.Output.OnComplete.Count}]: waiting for lock");
                    await _lockingSemaphore.WaitAsync();
                    acquiredLock = true;
                }

                SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} on-complete [{i + 1}/{job.Config.Output.OnComplete.Count}]: executing FileName='{startInfo.FileName}', {FormatProcessArgumentsForLog(startInfo)}, UseShellExecute={startInfo.UseShellExecute}, CreateNoWindow={startInfo.CreateNoWindow}, RedirectOutput={startInfo.RedirectStandardOutput}");
                currentResult = await ExecuteProcessAsync(startInfo);
            }
            finally
            {
                if (acquiredLock)
                    _lockingSemaphore.Release();
            }

            if (currentResult == null)
            {
                SockseekLog.Jobs.Error($"{OnCompleteLogPrefix(job, song)} execution failed for on-complete command {i + 1}. Stopping further on-complete actions for this item.");
                return;
            }

            prevCommandResult = currentResult;
            if (i == 0) firstCommandResult = currentResult;

            if (ProcessCommandResult(currentResult.Value, config, song, job, OnCompleteLogPrefix(job, song)))
                needUpdateIndex = true;
        }

        if (needUpdateIndex)
        {
            ctx.IndexEditor?.Update();
            ctx.PlaylistEditor?.Update();
            SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} index/playlist updated based on on-complete action output.");
        }
    }

    public static bool HasApplicableCommand(Job job, SongJob? song, JobOutcome outcome)
    {
        if (!job.Config.HasOnComplete || job.Config.Output.OnComplete == null)
            return false;

        bool isAlbumOnComplete = IsAlbumOnComplete(job, song);

        foreach (var rawCommand in job.Config.Output.OnComplete)
        {
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            if (ShouldExecuteCommand(ParseCommandFlags(rawCommand), outcome, isAlbumOnComplete))
                return true;
        }

        return false;
    }

    private static bool IsAlbumOnComplete(Job job, SongJob? song)
        => song == null && job is AlbumJob;

    private static OnCompleteContext BuildSongOnCompleteContext(SongJob song, Job parentJob)
    {
        var variables = FileManagerContext.FromSongJob(song, parentJob);
        return new OnCompleteContext(variables, song.DownloadPath);
    }

    private static OnCompleteContext BuildJobOnCompleteContext(Job job)
        => new(new FileManagerContext
        {
            Job = job,
            Query = job.QueryTrack ?? new SongQuery(),
            LifecycleState = job.LifecycleState,
            ActivityPhase = job.ActivityPhase,
            TerminalOutcome = job.TerminalOutcome,
            SkipReason = job.SkipReason,
            FailureReason = job.FailureReason,
            LineNumber = job.LineNumber,
            ItemNumber = job.ItemNumber,
        }, TagSourcePath: null);

    private static OnCompleteContext BuildAlbumOnCompleteContext(AlbumJob albumJob)
    {
        // Album-level on-complete uses the album as the event context, but
        // reads tag variables from the first audio file as its representative.
        var representativeFile = albumJob.ResolvedTarget?.Files.FirstOrDefault(f => !f.IsNotAudio);

        var variables = new FileManagerContext
        {
            Job = albumJob,
            Query = new SongQuery
            {
                Artist = albumJob.Query.Artist,
                Album = albumJob.Query.Album,
                Title = albumJob.Query.SearchHint,
                URI = albumJob.Query.URI,
                ArtistMaybeWrong = albumJob.Query.ArtistMaybeWrong,
            },
            Candidate = representativeFile?.ChosenCandidate ?? representativeFile?.Candidates?.FirstOrDefault(),
            DownloadPath = albumJob.DownloadPath,
            LifecycleState = albumJob.LifecycleState,
            ActivityPhase = albumJob.ActivityPhase,
            TerminalOutcome = albumJob.TerminalOutcome,
            SkipReason = albumJob.SkipReason,
            FailureReason = albumJob.FailureReason,
            IsNotAudio = false,
            LineNumber = albumJob.LineNumber,
            ItemNumber = albumJob.ItemNumber,
        };

        return new OnCompleteContext(variables, representativeFile?.DownloadPath);
    }

    private static FileManagerContext ApplyOutcomeToContext(FileManagerContext ctx, JobOutcome outcome)
    {
        if (!outcome.IsTerminal)
            return ctx;

        return ctx with
        {
            DownloadPath = outcome.DownloadPath ?? ctx.DownloadPath,
            LifecycleState = JobLifecycleState.Terminal,
            ActivityPhase = JobActivityPhase.None,
            TerminalOutcome = outcome.TerminalOutcome,
            SkipReason = outcome.SkipReason,
            FailureReason = outcome.FailureReason,
        };
    }

    private static string OnCompleteLogPrefix(Job job, SongJob? song)
    {
        if (song != null)
            return $"[{song.DisplayId}] SongJob:";

        return job switch
        {
            AlbumJob => $"[{job.DisplayId}] AlbumJob:",
            JobList => $"[{job.DisplayId}] Job List:",
            SearchJob => $"[{job.DisplayId}] SearchJob:",
            _ => $"[{job.DisplayId}] {job.GetType().Name}:",
        };
    }

    private static CommandConfig ParseCommandFlags(string rawCommand)
    {
        var config = new CommandConfig { Command = rawCommand };

        while (config.Command.Length > 2 && config.Command[1] == ':')
        {
            char   flag      = config.Command[0];
            string remaining = config.Command[2..];

            switch (flag)
            {
                case 's': config.UseShellExecute        = true; config.Command = remaining; break;
                case 't': config.OnlyTrackOnComplete    = true; config.Command = remaining; break;
                case 'a': config.OnlyAlbumOnComplete    = true; config.Command = remaining; break;
                case 'h': config.CreateNoWindow         = true; config.Command = remaining; break;
                case 'u': config.UseOutputToUpdateIndex = true; config.Command = remaining; break;
                case 'r': config.ReadOutput             = true; config.Command = remaining; break;
                case 'l': config.UseLocking             = true; config.Command = remaining; break;
                default:
                    if (char.IsDigit(flag))
                    {
                        config.RequiredResultCode = flag - '0';
                        config.Command = remaining;
                    }
                    else
                    {
                        return config;
                    }
                    break;
            }
        }
        return config;
    }

    private static bool ShouldExecuteCommand(CommandConfig config, JobOutcome outcome, bool isAlbum)
    {
        if (config.OnlyTrackOnComplete && isAlbum)  return false;
        if (config.OnlyAlbumOnComplete && !isAlbum) return false;
        if (!config.RequiredResultCode.HasValue && outcome.TerminalOutcome == JobTerminalOutcome.Skipped)
            return false;
        if (config.RequiredResultCode.HasValue && !RequiredResultMatches(config.RequiredResultCode.Value, outcome))
            return false;

        return true;
    }

    // Numeric on-complete prefixes predate the split state model. Keep their
    // user-facing meanings here instead of leaking JobStateOld beyond index compatibility.
    private static bool RequiredResultMatches(int code, JobOutcome outcome)
        => code switch
        {
            1 => outcome.TerminalOutcome == JobTerminalOutcome.Succeeded,
            2 => outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.PartialSuccess,
            3 => outcome.TerminalOutcome == JobTerminalOutcome.Skipped && outcome.SkipReason == JobSkipReason.AlreadyExists,
            4 => outcome.TerminalOutcome == JobTerminalOutcome.Skipped && outcome.SkipReason == JobSkipReason.NotFoundLastTime,
            5 => outcome.TerminalOutcome == JobTerminalOutcome.Skipped,
            _ => false,
        };

    private static string PrepareCommandString(string commandTemplate, OnCompleteContext ctx, ProcessResult? prevResult, ProcessResult? firstResult)
    {
        TagLib.File? audio = null;
        if (FileManager.HasTagVariables(commandTemplate))
        {
            try
            {
                var tagSourcePath = ctx.TagSourcePath ?? ctx.Variables.DownloadPath;
                if (!string.IsNullOrEmpty(tagSourcePath) && System.IO.File.Exists(tagSourcePath))
                    audio = TagLib.File.Create(tagSourcePath);
                else
                    SockseekLog.Warn($"Cannot load tags for variable replacement: tag source path is null or file does not exist ('{tagSourcePath}')");
            }
            catch (Exception ex)
            {
                SockseekLog.Warn($"Failed to load audio tags for variable replacement from '{ctx.TagSourcePath ?? ctx.Variables.DownloadPath}': {ex.Message}");
            }
        }

        try
        {
            string command = FileManager.ReplaceVariables(commandTemplate, ctx.Variables, audio);

            command = command
                .Replace("{exitcode}",       prevResult?.ExitCode.ToString()  ?? "-1")
                .Replace("{first-exitcode}", firstResult?.ExitCode.ToString() ?? "-1")
                .Replace("{stdout}",         string.IsNullOrWhiteSpace(prevResult?.Stdout)  ? "null" : prevResult.Value.Stdout)
                .Replace("{stderr}",         string.IsNullOrWhiteSpace(prevResult?.Stderr)  ? "null" : prevResult.Value.Stderr)
                .Replace("{first-stdout}",   string.IsNullOrWhiteSpace(firstResult?.Stdout) ? "null" : firstResult.Value.Stdout)
                .Replace("{first-stderr}",   string.IsNullOrWhiteSpace(firstResult?.Stderr) ? "null" : firstResult.Value.Stderr);

            return command.Trim();
        }
        finally
        {
            audio?.Dispose();
        }
    }

    private static (string FileName, List<string> ArgumentList, string ArgumentsString) ParseFileNameAndArguments(string preparedCommand)
    {
        preparedCommand = preparedCommand.Trim();
        if (string.IsNullOrEmpty(preparedCommand)) return ("", new List<string>(), "");

        string fileName;
        string arguments = "";

        if (preparedCommand.StartsWith('"'))
        {
            int endQuoteIndex = preparedCommand.IndexOf('"', 1);
            if (endQuoteIndex > 0)
            {
                fileName = preparedCommand.Substring(1, endQuoteIndex - 1);
                if (preparedCommand.Length > endQuoteIndex + 1)
                    arguments = preparedCommand.Substring(endQuoteIndex + 1).TrimStart();
            }
            else
            {
                fileName = preparedCommand.Trim('"');
            }
        }
        else
        {
            int firstSpaceIndex = preparedCommand.IndexOf(' ');
            if (firstSpaceIndex > 0)
            {
                fileName  = preparedCommand.Substring(0, firstSpaceIndex);
                arguments = preparedCommand.Substring(firstSpaceIndex + 1).TrimStart();
            }
            else
            {
                fileName = preparedCommand;
            }
        }

        var argList = SplitArguments(arguments);
        return (fileName, argList, arguments);
    }

    private static List<string> SplitArguments(string commandLine)
    {
        var args = new List<string>();
        var currentArg = new System.Text.StringBuilder();
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escapeNext = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (escapeNext)
            {
                currentArg.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '\\')
            {
                escapeNext = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (currentArg.Length > 0)
                {
                    args.Add(currentArg.ToString());
                    currentArg.Clear();
                }
                continue;
            }

            currentArg.Append(c);
        }

        if (currentArg.Length > 0)
        {
            args.Add(currentArg.ToString());
        }

        return args;
    }

    private static ProcessStartInfo ConfigureProcessStartInfo(string fileName, List<string> argList, string argString, CommandConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName        = fileName,
            UseShellExecute = config.UseShellExecute,
            CreateNoWindow  = config.CreateNoWindow,
        };

        if (config.UseOutputToUpdateIndex || config.ReadOutput)
        {
            startInfo.UseShellExecute          = false;
            startInfo.RedirectStandardOutput   = true;
            startInfo.RedirectStandardError    = true;
            startInfo.StandardOutputEncoding   = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding    = System.Text.Encoding.UTF8;
        }

        if (startInfo.UseShellExecute)
        {
            startInfo.Arguments = argString;
        }
        else
        {
            foreach (var arg in argList)
                startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static string FormatProcessArgumentsForLog(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute)
            return $"Arguments='{startInfo.Arguments}'";

        var args = startInfo.ArgumentList.Count == 0
            ? ""
            : string.Join(", ", startInfo.ArgumentList.Select(arg => $"'{arg.Replace("'", "\\'")}'"));
        return $"ArgumentList=[{args}]";
    }

    private static async Task<ProcessResult?> ExecuteProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                SockseekLog.Error($"Failed to start process: FileName='{startInfo.FileName}', {FormatProcessArgumentsForLog(startInfo)}");
                return null;
            }

            Task<string>? readStdoutTask = startInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : null;
            Task<string>? readStderrTask = startInfo.RedirectStandardError  ? process.StandardError.ReadToEndAsync()  : null;

            await process.WaitForExitAsync();

            string? stdout = readStdoutTask != null ? (await readStdoutTask).Trim().Trim('"') : null;
            string? stderr = readStderrTask != null ? (await readStderrTask).Trim().Trim('"') : null;

            return new ProcessResult { ExitCode = process.ExitCode, Stdout = stdout, Stderr = stderr };
        }
        catch (Exception ex)
        {
            SockseekLog.Error($"Error executing process: FileName='{startInfo.FileName}', {FormatProcessArgumentsForLog(startInfo)}. Exception: {ex}");
            return null;
        }
    }

    // Returns true if the index needs updating.
    private static bool ProcessCommandResult(ProcessResult result, CommandConfig config, SongJob? song, Job job, string logPrefix)
    {
        bool needsUpdate = false;

        if (config.UseOutputToUpdateIndex && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            string[] parts = result.Stdout.Split(';', 2);
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && song != null)
            {
                string newPath = parts[1].Trim();
                if (song.DownloadPath != newPath)
                {
                    SockseekLog.Jobs.Debug($"{logPrefix} updating song path from '{song.DownloadPath}' to '{newPath}' based on stdout: {song}");
                    song.DownloadPath = newPath;
                    needsUpdate = true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                SockseekLog.Jobs.Warn($"{logPrefix} ignored on-complete stdout for index update. In 3.0 stdout can update the path using '<ignored>;<path>', but cannot mutate job state. Stdout: '{result.Stdout}'");
            }
        }

        if (result.ExitCode != 0)
            SockseekLog.Jobs.Debug($"{logPrefix} command finished with non-zero exit code {result.ExitCode}. Stdout: '{result.Stdout ?? "N/A"}', Stderr: '{result.Stderr ?? "N/A"}'");

        return needsUpdate;
    }
}
