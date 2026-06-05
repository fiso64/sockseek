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
        public int?    RequiredState     { get; set; }
        public bool    UseLocking             { get; set; }
    }

    private struct ProcessResult
    {
        public int     ExitCode { get; set; }
        public string? Stdout   { get; set; }
        public string? Stderr   { get; set; }
    }

    // Execute on-complete actions for a job.
    // song is null when called for an album-level completion (no individual song).
    public static async Task ExecuteAsync(Job job, SongJob? song, JobContext ctx)
    {
        if (!job.Config.HasOnComplete || job.Config.Output.OnComplete == null)
            return;

        bool isAlbumOnComplete = job is AlbumJob;

        // Build a FileManagerContext for variable substitution.
        string extractorName = job.Config.Extraction.InputType.ToString();
        string inputSource   = job.Config.Extraction.Input ?? "";
        string outputDir     = job.Config.Output.ParentDir ?? "";

        FileManagerContext fmCtx;
        if (song != null)
            fmCtx = FileManagerContext.FromSongJob(song, job) with { ExtractorName = extractorName, InputSource = inputSource, OutputDir = outputDir };
        else if (job is AlbumJob albumJob && albumJob.ResolvedTarget != null)
        {
            // Use the first audio file in the chosen folder as representative context.
            var rep = albumJob.ResolvedTarget.Files.FirstOrDefault(f => !f.IsNotAudio);
            fmCtx = rep != null
                ? FileManagerContext.FromSongJob(rep, job) with { ExtractorName = extractorName, InputSource = inputSource, OutputDir = outputDir }
                : new FileManagerContext { Job = job, ExtractorName = extractorName, InputSource = inputSource, OutputDir = outputDir, Query = new SongQuery(), DownloadPath = albumJob.DownloadPath };
        }
        else
        {
            string? dp = (job as AlbumJob)?.DownloadPath;
            fmCtx = new FileManagerContext { Job = job, ExtractorName = extractorName, InputSource = inputSource, OutputDir = outputDir, Query = new SongQuery(), DownloadPath = dp };
        }

        // Derive JobState for RequiredState matching.
        JobState currentState = song != null
            ? song.State
            : job.State;

        bool needUpdateIndex    = false;
        ProcessResult? firstCommandResult = null;
        ProcessResult? prevCommandResult  = null;

        for (int i = 0; i < job.Config.Output.OnComplete.Count; i++)
        {
            string rawCommand = job.Config.Output.OnComplete[i];
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            CommandConfig config = ParseCommandFlags(rawCommand);

            if (!ShouldExecuteCommand(config, currentState, isAlbumOnComplete))
                continue;

            string preparedCommand = PrepareCommandString(config.Command, fmCtx, prevCommandResult, firstCommandResult);
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

                SockseekLog.Jobs.Debug($"{OnCompleteLogPrefix(job, song)} on-complete [{i + 1}/{job.Config.Output.OnComplete.Count}]: executing FileName='{startInfo.FileName}', Arguments='{startInfo.Arguments}', UseShellExecute={startInfo.UseShellExecute}, CreateNoWindow={startInfo.CreateNoWindow}, RedirectOutput={startInfo.RedirectStandardOutput}");
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
                    if (char.IsDigit(flag) && int.TryParse(flag.ToString(), out int state))
                    {
                        config.RequiredState = state;
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

    private static bool ShouldExecuteCommand(CommandConfig config, JobState currentState, bool isAlbum)
    {
        if (config.OnlyTrackOnComplete && isAlbum)  return false;
        if (config.OnlyAlbumOnComplete && !isAlbum) return false;
        if (config.RequiredState.HasValue && (int)currentState != config.RequiredState.Value) return false;
        return true;
    }

    private static string PrepareCommandString(string commandTemplate, FileManagerContext ctx, ProcessResult? prevResult, ProcessResult? firstResult)
    {
        TagLib.File? audio = null;
        if (FileManager.HasTagVariables(commandTemplate))
        {
            try
            {
                if (!string.IsNullOrEmpty(ctx.DownloadPath) && System.IO.File.Exists(ctx.DownloadPath))
                    audio = TagLib.File.Create(ctx.DownloadPath);
                else
                    SockseekLog.Warn($"Cannot load tags for variable replacement: DownloadPath is null or file does not exist ('{ctx.DownloadPath}')");
            }
            catch (Exception ex)
            {
                SockseekLog.Warn($"Failed to load audio tags for variable replacement from '{ctx.DownloadPath}': {ex.Message}");
            }
        }

        try
        {
            string command = FileManager.ReplaceVariables(commandTemplate, ctx, audio);

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

    private static async Task<ProcessResult?> ExecuteProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                SockseekLog.Error($"Failed to start process: FileName='{startInfo.FileName}', Arguments='{startInfo.Arguments}'");
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
            SockseekLog.Error($"Error executing process: FileName='{startInfo.FileName}', Arguments='{startInfo.Arguments}'. Exception: {ex}");
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
            if (int.TryParse(parts[0], out int newStateInt))
            {
                var newState = (JobState)newStateInt;

                if (song != null)
                {
                    if (song.State != newState)
                    {
                        SockseekLog.Jobs.Debug($"{logPrefix} updating song state from {song.State} to {newState} based on stdout: {song}");
                        if (newState == JobState.Failed)
                            song.Fail(FailureReason.Other, "Failed via on-complete stdout");
                        else if (newState is JobState.Skipped or JobState.AlreadyExists or JobState.NotFoundLastTime)
                            song.SetSkipped(newState);
                        else
                            song.UpdateState(newState);
                        needsUpdate = true;
                    }

                    if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        string newPath = parts[1].Trim();
                        if (song.DownloadPath != newPath)
                        {
                            SockseekLog.Jobs.Debug($"{logPrefix} updating song path from '{song.DownloadPath}' to '{newPath}' based on stdout: {song}");
                            song.DownloadPath = newPath;
                            needsUpdate = true;
                        }
                    }
                }
            }
            else
            {
                SockseekLog.Jobs.Warn($"{logPrefix} could not parse new state from stdout. Stdout: '{result.Stdout}'");
            }
        }

        if (result.ExitCode != 0)
            SockseekLog.Jobs.Debug($"{logPrefix} command finished with non-zero exit code {result.ExitCode}. Stdout: '{result.Stdout ?? "N/A"}', Stderr: '{result.Stderr ?? "N/A"}'");

        return needsUpdate;
    }
}
