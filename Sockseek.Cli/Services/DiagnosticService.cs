using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Cli;
    public class DiagnosticService
    {
        private readonly SoulseekClientManager _clientManager;
        private readonly CliOutputController? output;

        internal DiagnosticService(
            SoulseekClientManager clientManager,
            CliOutputController? output = null)
        {
            _clientManager = clientManager;
            this.output = output;
        }

        public async Task PerformNoInputActions(PrintOption printOption, string? indexFilePath, CancellationToken ct)
        {
            if (printOption.HasFlag(PrintOption.Index))
            {
                if (string.IsNullOrEmpty(indexFilePath))
                {
                    Error("Error: No index file path provided");
                    return;
                }

                var fullPath = Utils.GetFullPath(Utils.ExpandVariables(indexFilePath));
                if (!System.IO.File.Exists(fullPath))
                {
                    Error($"Error: Index file {fullPath} does not exist");
                    return;
                }

                var index = new DownloadOutputFile(fullPath, new JobList(), OutputFileKind.Index, true, readOnly: true);
                var data = index.GetPreviousRunData();

                if (printOption.HasFlag(PrintOption.IndexFailed))
                    data = data.Where(e => e.State == JobStateOld.Failed).ToList();

                JsonPrinter.PrintIndexJson(data);
            }
        }

        private void Error(string message)
            => CliProcessOutput.Write(
                output,
                Microsoft.Extensions.Logging.LogLevel.Error,
                message,
                presentation: CliProcessLogPresentation.Plain);
    }
