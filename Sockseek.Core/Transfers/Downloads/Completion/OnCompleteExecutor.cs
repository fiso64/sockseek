using System.Diagnostics;
using System.Text;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using Sockseek.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core.Services;

public static class OnCompleteExecutor
{
    private const int MaxCapturedCommandOutputChars = 64 * 1024;
    private static readonly SemaphoreSlim _lockingSemaphore = new(1, 1);

    private enum CommandScope
    {
        Any,
        Track,
        Album,
    }

    private enum CommandWhen
    {
        Default,
        Any,
        Success,
        Failure,
        Skipped,
        AlreadyExists,
        NotFoundLastTime,
        Cancelled,
        PartialSuccess,
    }

    private struct CommandConfig
    {
        public string Command { get; set; }
        public bool UseShellExecute { get; set; }
        public bool CreateNoWindow { get; set; }
        public CommandScope Scope { get; set; }
        public CommandWhen When { get; set; }
        public bool UseOutputToUpdateIndex { get; set; }
        public bool UseLocking { get; set; }
    }

    private struct ProcessResult
    {
        public int ExitCode { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public int StdoutCharsRead { get; set; }
        public int StderrCharsRead { get; set; }
        public bool StdoutTruncated { get; set; }
        public bool StderrTruncated { get; set; }
    }

    private readonly record struct CapturedProcessOutput(string? Text, int CharsRead, bool Truncated);

    private readonly record struct OnCompleteContext(FileManagerContext Variables, string? TagSourcePath);
    private readonly record struct CommandProcessingResult(JobOutcome Outcome, bool NeedsIndexUpdate);
    private readonly record struct UpdateIndexStateResult(JobOutcome? Outcome, bool AllowsPathUpdate);
    private readonly record struct Reporter(
        Job Job,
        DownloadEvents? Events,
        ILogger Logger)
    {
        public void Message(LogLevel level, string message)
            => Events?.RaiseJobMessage(Job, level, null, message);

        public void Decision(string decision, int? count = null)
            => DownloadLogMessages.JobDecision(Logger, Job.Id, decision, count);

        public void Failure(Exception exception)
            => DownloadLogMessages.ComponentFailed(
                Logger,
                exception,
                "on-complete",
                Job.Id);
    }

    public static void ValidateCommand(string rawCommand)
        => _ = ParseCommand(rawCommand);

    public static void ValidateCommands(IEnumerable<string>? commands)
    {
        if (commands == null)
            return;

        foreach (var command in commands)
            ValidateCommand(command);
    }

    // Execute on-complete actions for a job.
    // song is null when called for an album-level completion (no individual song).
    public static async Task<JobOutcome> ExecuteAsync(
        Job job,
        SongJob? song,
        JobContext ctx,
        JobOutcome outcome,
        DownloadEvents? events = null,
        ILogger? logger = null)
    {
        if (!job.Config.HasOnComplete || job.Config.Output.OnComplete == null)
            return outcome;

        bool isAlbumOnComplete = IsAlbumOnComplete(job, song);
        var reporter = new Reporter(
            OnCompleteLogJob(job, song),
            events,
            logger ?? NullLogger.Instance);

        // Build a FileManagerContext for variable substitution.
        string extractorName = job.Config.Extraction.InputType.ToString();
        string inputSource = job.Config.Extraction.Input ?? "";
        string outputDir = job.Config.Output.ParentDir ?? "";
        string configDir = job.Config.RuntimePathContext.ConfigDir ?? "";

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

        var currentOutcome = outcome;
        bool needUpdateIndex = false;
        ProcessResult? firstCommandResult = null;
        ProcessResult? prevCommandResult = null;

        for (int i = 0; i < job.Config.Output.OnComplete.Count; i++)
        {
            string rawCommand = job.Config.Output.OnComplete[i];
            if (string.IsNullOrWhiteSpace(rawCommand))
                continue;

            CommandConfig config = ParseCommand(rawCommand);

            onCompleteContext = onCompleteContext with
            {
                Variables = ApplyOutcomeToContext(onCompleteContext.Variables, currentOutcome),
            };
            if (!ShouldExecuteCommand(config, currentOutcome, isTrack: song != null, isAlbum: isAlbumOnComplete))
                continue;

            string preparedCommand = PrepareCommandString(
                config.Command,
                onCompleteContext,
                prevCommandResult,
                firstCommandResult,
                reporter);
            if (string.IsNullOrWhiteSpace(preparedCommand))
            {
                reporter.Message(
                    LogLevel.Warning,
                    $"skipping on-complete action {i + 1} because the prepared command is empty after variable replacement");
                continue;
            }

            (string fileName, string argString) = ParseFileNameAndArguments(preparedCommand);
            ProcessStartInfo startInfo = ConfigureProcessStartInfo(fileName, argString, config);

            ProcessResult? currentResult = null;
            bool acquiredLock = false;

            try
            {
                if (config.UseLocking)
                {
                    reporter.Decision("waiting-for-on-complete-lock", i + 1);
                    await _lockingSemaphore.WaitAsync();
                    acquiredLock = true;
                }

                reporter.Decision("executing-on-complete", i + 1);
                currentResult = await ExecuteProcessAsync(startInfo, reporter);
            }
            finally
            {
                if (acquiredLock)
                    _lockingSemaphore.Release();
            }

            if (currentResult == null)
            {
                reporter.Message(
                    LogLevel.Error,
                    $"execution failed for on-complete command {i + 1}; stopping further actions for this item");
                return currentOutcome;
            }

            var processedResult = ProcessCommandResultCore(
                currentResult.Value,
                config,
                song,
                job,
                currentOutcome,
                reporter);
            currentOutcome = processedResult.Outcome;
            if (processedResult.NeedsIndexUpdate)
                needUpdateIndex = true;

            prevCommandResult = currentResult;
            if (i == 0) firstCommandResult = currentResult;
        }

        if (needUpdateIndex)
        {
            reporter.Decision("on-complete-updated-index", null);
        }

        return currentOutcome;
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

            if (ShouldExecuteCommand(ParseCommand(rawCommand), outcome, isTrack: song != null, isAlbum: isAlbumOnComplete))
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
    {
        string? downloadPath = job switch
        {
            FileDownloadJob file => file.DownloadPath,
            DirectoryDownloadJob directory => directory.DownloadPath,
            _ => null,
        };
        return new(new FileManagerContext
        {
            Job = job,
            Query = job.QueryTrack ?? new SongQuery(),
            PeerTarget = (job as RemoteFileJob)?.Target,
            DownloadPath = downloadPath,
            TerminalOutcome = job.TerminalOutcome,
            SkipReason = job.SkipReason,
            FailureReason = job.FailureReason,
            IsNotAudio = job is not FileDownloadJob
                || !Utils.IsMusicFile((job as RemoteFileJob)?.Target.Filename ?? downloadPath ?? ""),
            LineNumber = job.LineNumber,
            ItemNumber = job.ItemNumber,
        }, TagSourcePath: null);
    }

    private static OnCompleteContext BuildAlbumOnCompleteContext(AlbumJob albumJob)
    {
        // Album-level on-complete uses the album as the event context, but
        // reads tag variables from the first audio file as its representative.
        var representativeFile = albumJob.TrackJobs.FirstOrDefault(f => !f.IsNotAudio);

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
            Candidate = representativeFile?.ResolvedTarget ?? representativeFile?.Candidates?.FirstOrDefault(),
            DownloadPath = albumJob.DownloadPath,
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
            TerminalOutcome = outcome.TerminalOutcome,
            SkipReason = outcome.SkipReason,
            FailureReason = outcome.FailureReason,
        };
    }

    private static Job OnCompleteLogJob(Job job, SongJob? song)
        => song ?? job;

    private static CommandConfig ParseCommand(string rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
            throw InvalidOnCompleteCommand(rawCommand, "Command is empty.");

        var delimiterIndex = FindCommandDelimiter(rawCommand);
        if (delimiterIndex < 0)
            throw InvalidOnCompleteCommand(rawCommand, "Missing `--` command delimiter.");

        var optionText = rawCommand[..delimiterIndex].Trim();
        var command = rawCommand[(delimiterIndex + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(command))
            throw InvalidOnCompleteCommand(rawCommand, "Command after `--` is empty.");

        var config = new CommandConfig { Command = command };
        foreach (var option in SplitOptionTokens(optionText))
            ApplyCommandOption(ref config, option, rawCommand);

        return config;
    }

    private static int FindCommandDelimiter(string rawCommand)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < rawCommand.Length - 1; i++)
        {
            var c = rawCommand[i];
            if (c == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (inSingleQuote || inDoubleQuote)
                continue;

            if (rawCommand[i] != '-' || rawCommand[i + 1] != '-')
                continue;

            var beforeOk = i == 0 || char.IsWhiteSpace(rawCommand[i - 1]);
            var afterIndex = i + 2;
            var afterOk = afterIndex == rawCommand.Length || char.IsWhiteSpace(rawCommand[afterIndex]);
            if (beforeOk && afterOk)
                return i;
        }

        return -1;
    }

    private static string[] SplitOptionTokens(string optionText)
        => string.IsNullOrWhiteSpace(optionText)
            ? []
            : optionText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ApplyCommandOption(ref CommandConfig config, string option, string rawCommand)
    {
        switch (option)
        {
            case "hidden":
                config.CreateNoWindow = true;
                return;
            case "shell":
                config.UseShellExecute = true;
                return;
            case "lock":
                config.UseLocking = true;
                return;
            case "update-index":
                config.UseOutputToUpdateIndex = true;
                return;
        }

        if (option.StartsWith("scope=", StringComparison.OrdinalIgnoreCase))
        {
            SetScope(ref config, option["scope=".Length..], rawCommand);
            return;
        }

        if (option.StartsWith("when=", StringComparison.OrdinalIgnoreCase))
        {
            SetWhen(ref config, option["when=".Length..], rawCommand);
            return;
        }

        throw InvalidOnCompleteCommand(rawCommand, $"Unknown option `{option}`.");
    }

    private static void SetScope(ref CommandConfig config, string value, string rawCommand)
    {
        if (config.Scope != CommandScope.Any)
            throw InvalidOnCompleteCommand(rawCommand, "`scope` was specified more than once.");

        config.Scope = value.ToLowerInvariant() switch
        {
            "track" => CommandScope.Track,
            "album" => CommandScope.Album,
            _ => throw InvalidOnCompleteCommand(rawCommand, $"Unknown scope `{value}`. Use `scope=track` or `scope=album`.")
        };
    }

    private static void SetWhen(ref CommandConfig config, string value, string rawCommand)
    {
        if (config.When != CommandWhen.Default)
            throw InvalidOnCompleteCommand(rawCommand, "`when` was specified more than once.");

        config.When = value.ToLowerInvariant() switch
        {
            "any" => CommandWhen.Any,
            "completed" => CommandWhen.Default,
            "success" or "succeeded" => CommandWhen.Success,
            "failure" or "failed" => CommandWhen.Failure,
            "skipped" => CommandWhen.Skipped,
            "already-exists" or "alreadyexists" => CommandWhen.AlreadyExists,
            "not-found-last-time" or "not-found" or "notfound" => CommandWhen.NotFoundLastTime,
            "cancelled" or "canceled" => CommandWhen.Cancelled,
            // PartialSuccess is currently produced by container-style jobs, but on-complete
            // actions are only invoked for track completions and AlbumJob-level completions.
            // Do not document it as usable unless container-level on-complete execution is added.
            "partial" or "partial-success" => CommandWhen.PartialSuccess,
            _ => throw InvalidOnCompleteCommand(rawCommand, $"Unknown when value `{value}`.")
        };
    }

    private static ArgumentException InvalidOnCompleteCommand(string rawCommand, string reason)
    {
        var legacyHint = LooksLikeLegacyPrefixSyntax(rawCommand)
            ? " Legacy one-letter prefixes are no longer supported in 3.0."
            : "";
        return new ArgumentException(
            $"Input error: Invalid on-complete command. {reason}{legacyHint} Use `--` to separate Sockseek options from the command, for example: `on-complete = when=success scope=album hidden -- cmd /d /c notify.cmd \"{{path}}\"`.");
    }

    private static bool LooksLikeLegacyPrefixSyntax(string rawCommand)
    {
        var command = rawCommand.TrimStart();
        var consumedAny = false;
        while (command.Length > 2 && command[1] == ':')
        {
            var flag = command[0];
            if (!char.IsDigit(flag) && flag is not ('s' or 't' or 'a' or 'h' or 'u' or 'l' or 'r'))
                return consumedAny;

            consumedAny = true;
            command = command[2..];
        }

        return consumedAny;
    }

    private static bool ShouldExecuteCommand(CommandConfig config, JobOutcome outcome, bool isTrack, bool isAlbum)
    {
        if (!outcome.IsTerminal) return false;
        if (config.Scope == CommandScope.Track && !isTrack) return false;
        if (config.Scope == CommandScope.Album && !isAlbum) return false;

        return config.When switch
        {
            CommandWhen.Default => outcome.TerminalOutcome != JobTerminalOutcome.Skipped,
            CommandWhen.Any => true,
            CommandWhen.Success => outcome.TerminalOutcome == JobTerminalOutcome.Succeeded,
            CommandWhen.Failure => outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.PartialSuccess,
            CommandWhen.Skipped => outcome.TerminalOutcome == JobTerminalOutcome.Skipped,
            CommandWhen.AlreadyExists => outcome.TerminalOutcome == JobTerminalOutcome.Skipped
                && outcome.SkipReason == JobSkipReason.AlreadyExists,
            CommandWhen.NotFoundLastTime => outcome.TerminalOutcome == JobTerminalOutcome.Skipped
                && outcome.SkipReason == JobSkipReason.NotFoundLastTime,
            CommandWhen.Cancelled => outcome.TerminalOutcome == JobTerminalOutcome.Cancelled,
            CommandWhen.PartialSuccess => outcome.TerminalOutcome == JobTerminalOutcome.PartialSuccess,
            _ => false,
        };
    }

    private static string PrepareCommandString(
        string commandTemplate,
        OnCompleteContext ctx,
        ProcessResult? prevResult,
        ProcessResult? firstResult,
        Reporter reporter)
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
                    reporter.Message(
                        LogLevel.Warning,
                        "cannot load audio tags for on-complete variable replacement because the tag source is unavailable");
            }
            catch (Exception ex)
            {
                reporter.Message(
                    LogLevel.Warning,
                    $"failed to load audio tags for on-complete variable replacement ({ex.GetType().Name})");
            }
        }

        try
        {
            string command = FileManager.ReplaceVariables(commandTemplate, ctx.Variables, audio);

            command = command
                .Replace("{exitcode}", prevResult?.ExitCode.ToString() ?? "-1")
                .Replace("{first-exitcode}", firstResult?.ExitCode.ToString() ?? "-1")
                .Replace("{stdout}", string.IsNullOrWhiteSpace(prevResult?.Stdout) ? "null" : prevResult.Value.Stdout)
                .Replace("{stderr}", string.IsNullOrWhiteSpace(prevResult?.Stderr) ? "null" : prevResult.Value.Stderr)
                .Replace("{first-stdout}", string.IsNullOrWhiteSpace(firstResult?.Stdout) ? "null" : firstResult.Value.Stdout)
                .Replace("{first-stderr}", string.IsNullOrWhiteSpace(firstResult?.Stderr) ? "null" : firstResult.Value.Stderr);

            return command.Trim();
        }
        finally
        {
            audio?.Dispose();
        }
    }

    private static (string FileName, string ArgumentsString) ParseFileNameAndArguments(string preparedCommand)
    {
        preparedCommand = preparedCommand.Trim();
        if (string.IsNullOrEmpty(preparedCommand)) return ("", "");

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
                fileName = preparedCommand.Substring(0, firstSpaceIndex);
                arguments = preparedCommand.Substring(firstSpaceIndex + 1).TrimStart();
            }
            else
            {
                fileName = preparedCommand;
            }
        }

        return (fileName, arguments);
    }

    private static ProcessStartInfo ConfigureProcessStartInfo(string fileName, string argString, CommandConfig config)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argString,
            UseShellExecute = config.UseShellExecute,
            CreateNoWindow = config.CreateNoWindow,
        };

        if (!config.UseShellExecute || config.UseOutputToUpdateIndex)
        {
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }

        return startInfo;
    }

    private static async Task<ProcessResult?> ExecuteProcessAsync(
        ProcessStartInfo startInfo,
        Reporter reporter)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                reporter.Message(LogLevel.Error, "failed to start on-complete process");
                return null;
            }

            Task<CapturedProcessOutput>? readStdoutTask = startInfo.RedirectStandardOutput ? CaptureProcessOutputAsync(process.StandardOutput) : null;
            Task<CapturedProcessOutput>? readStderrTask = startInfo.RedirectStandardError ? CaptureProcessOutputAsync(process.StandardError) : null;

            await process.WaitForExitAsync();

            var stdout = readStdoutTask != null ? await readStdoutTask : default;
            var stderr = readStderrTask != null ? await readStderrTask : default;

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                Stdout = CleanCapturedOutput(stdout.Text),
                Stderr = CleanCapturedOutput(stderr.Text),
                StdoutCharsRead = stdout.CharsRead,
                StderrCharsRead = stderr.CharsRead,
                StdoutTruncated = stdout.Truncated,
                StderrTruncated = stderr.Truncated,
            };
        }
        catch (Exception ex)
        {
            reporter.Failure(ex);
            return null;
        }
    }

    private static async Task<CapturedProcessOutput> CaptureProcessOutputAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(MaxCapturedCommandOutputChars, 4096));
        var buffer = new char[4096];
        var charsRead = 0;

        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            charsRead += read;
            var remaining = MaxCapturedCommandOutputChars - builder.Length;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(read, remaining));
        }

        return new CapturedProcessOutput(
            builder.Length > 0 ? builder.ToString() : null,
            charsRead,
            charsRead > MaxCapturedCommandOutputChars);
    }

    private static string? CleanCapturedOutput(string? output)
        => string.IsNullOrWhiteSpace(output) ? null : output.Trim().Trim('"');

    private static CommandProcessingResult ProcessCommandResult(
        ProcessResult result,
        CommandConfig config,
        SongJob? song,
        Job job,
        JobOutcome currentOutcome)
        => ProcessCommandResultCore(
            result,
            config,
            song,
            job,
            currentOutcome,
            new Reporter(OnCompleteLogJob(job, song), null, NullLogger.Instance));

    private static CommandProcessingResult ProcessCommandResultCore(
        ProcessResult result,
        CommandConfig config,
        SongJob? song,
        Job job,
        JobOutcome currentOutcome,
        Reporter reporter)
    {
        bool needsUpdate = false;
        var nextOutcome = currentOutcome;

        if (config.UseOutputToUpdateIndex && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            if (result.StdoutTruncated)
            {
                reporter.Message(
                    LogLevel.Warning,
                    "ignored on-complete stdout for index update because command output exceeded the capture limit");
                return new CommandProcessingResult(nextOutcome, needsUpdate);
            }

            string[] parts = result.Stdout.Split(';', 2);
            string stateText = parts[0].Trim();
            string? newPath = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? parts[1].Trim()
                : null;
            var indexTarget = GetUpdateIndexTarget(job, song);

            var stateResult = TryGetUpdateIndexStateOutcome(
                stateText,
                indexTarget,
                nextOutcome,
                newPath,
                reporter);
            if (stateResult.Outcome is { } stateOutcome)
            {
                nextOutcome = stateOutcome;
                needsUpdate = true;
            }

            if (stateResult.AllowsPathUpdate && newPath != null && indexTarget != null)
            {
                var currentPath = nextOutcome.DownloadPath ?? GetDownloadPath(indexTarget);
                if (currentPath != newPath)
                {
                    reporter.Decision("on-complete-index-path-updated");
                    nextOutcome = WithDownloadPath(nextOutcome, newPath);
                    needsUpdate = true;
                }
            }
            else if (stateResult.AllowsPathUpdate && newPath != null && indexTarget == null)
            {
                reporter.Message(
                    LogLevel.Warning,
                    "ignored on-complete stdout path update because index paths can only be updated for track- or album-level completions");
            }
        }

        if (result.ExitCode != 0)
            reporter.Message(
                LogLevel.Warning,
                $"on-complete command exited with code {result.ExitCode}");

        return new CommandProcessingResult(nextOutcome, needsUpdate);
    }

    private static Job? GetUpdateIndexTarget(Job job, SongJob? song)
        => song ?? (job is AlbumJob ? job : null);

    private static string? GetDownloadPath(Job job)
        => job switch
        {
            SongJob song => song.DownloadPath,
            AlbumJob album => album.DownloadPath,
            _ => null,
        };

    private static UpdateIndexStateResult TryGetUpdateIndexStateOutcome(
        string stateText,
        Job? indexTarget,
        JobOutcome currentOutcome,
        string? newPath,
        Reporter reporter)
    {
        if (string.IsNullOrWhiteSpace(stateText)
            || stateText.Equals("ignored", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("ignore", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("unchanged", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("no-change", StringComparison.OrdinalIgnoreCase))
            return new UpdateIndexStateResult(null, AllowsPathUpdate: true);

        if (indexTarget == null)
        {
            reporter.Message(
                LogLevel.Warning,
                "ignored on-complete stdout state update because index state can only be updated for track- or album-level completions");
            return new UpdateIndexStateResult(null, AllowsPathUpdate: false);
        }

        switch (stateText.ToLowerInvariant())
        {
            case "success":
            case "succeeded":
            case "done":
            case "downloaded":
                var path = newPath ?? currentOutcome.DownloadPath ?? GetDownloadPath(indexTarget);
                var successOutcome = indexTarget is SongJob song
                    ? JobOutcome.Done(path, currentOutcome.ChosenCandidate ?? song.ResolvedTarget, currentOutcome.DownloadSource != SongDownloadSource.None ? currentOutcome.DownloadSource : song.DownloadSource)
                    : JobOutcome.Done(path);
                return new UpdateIndexStateResult(successOutcome, AllowsPathUpdate: true);

            case "failure":
            case "failed":
                var failureReason = currentOutcome.FailureReason is JobFailureReason.None or JobFailureReason.Cancelled
                    ? indexTarget.FailureReason is JobFailureReason.None or JobFailureReason.Cancelled
                        ? JobFailureReason.Other
                        : indexTarget.FailureReason
                    : currentOutcome.FailureReason;
                if (newPath != null)
                    reporter.Message(
                        LogLevel.Warning,
                        "ignored path after failed on-complete index state because failed index entries clear their stored path");
                return new UpdateIndexStateResult(
                    JobOutcome.Failed(failureReason, currentOutcome.FailureMessage, currentOutcome.FailureDetail, clearDownloadPath: true),
                    AllowsPathUpdate: false);

            default:
                reporter.Message(
                    LogLevel.Warning,
                    $"ignored unknown on-complete index state '{stateText}'; use success, failed, or ignored");
                return new UpdateIndexStateResult(null, AllowsPathUpdate: false);
        }
    }

    private static JobOutcome WithDownloadPath(JobOutcome outcome, string path)
    {
        return outcome.TerminalOutcome switch
        {
            JobTerminalOutcome.Succeeded => JobOutcome.Done(path, outcome.ChosenCandidate, outcome.DownloadSource),
            JobTerminalOutcome.Failed => JobOutcome.Failed(
                FailureReasonForFailedIndexUpdate(outcome.FailureReason),
                outcome.FailureMessage,
                outcome.FailureDetail,
                path),
            JobTerminalOutcome.Cancelled => JobOutcome.Cancelled(
                outcome.CancellationSource == JobCancellationSource.None
                    ? JobCancellationSource.InternalEngine
                    : outcome.CancellationSource,
                outcome.FailureMessage,
                outcome.FailureDetail,
                path),
            JobTerminalOutcome.Skipped => JobOutcome.Skipped(outcome.SkipReason, outcome.FailureReason, path),
            JobTerminalOutcome.PartialSuccess => JobOutcome.PartialSuccess(outcome.FailureMessage, outcome.CancellationSource, path),
            _ => outcome,
        };
    }

    private static JobFailureReason FailureReasonForFailedIndexUpdate(JobFailureReason reason)
        => reason is JobFailureReason.None or JobFailureReason.Cancelled
            ? JobFailureReason.Other
            : reason;

}
