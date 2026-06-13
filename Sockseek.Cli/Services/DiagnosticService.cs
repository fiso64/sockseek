using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;

namespace Sockseek.Cli;
    public class DiagnosticService
    {
        private readonly SoulseekClientManager _clientManager;

        public DiagnosticService(SoulseekClientManager clientManager)
        {
            _clientManager = clientManager;
        }

        public async Task PerformNoInputActions(PrintOption printOption, string? indexFilePath, CancellationToken ct)
        {
            if (printOption.HasFlag(PrintOption.Index))
            {
                if (string.IsNullOrEmpty(indexFilePath))
                {
                    SockseekLog.Error("Error: No index file path provided");
                    return;
                }

                var fullPath = Utils.GetFullPath(Utils.ExpandVariables(indexFilePath));
                if (!System.IO.File.Exists(fullPath))
                {
                    SockseekLog.Error($"Error: Index file {fullPath} does not exist");
                    return;
                }

                var index = new M3uEditor(fullPath, new JobList(), M3uOption.Index, true);
                var data = index.GetPreviousRunData();

                if (printOption.HasFlag(PrintOption.IndexFailed))
                    data = data.Where(e => e.State == JobStateOld.Failed).ToList();

                JsonPrinter.PrintIndexJson(data);
            }
        }
    }
