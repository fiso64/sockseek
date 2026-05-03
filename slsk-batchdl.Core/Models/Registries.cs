using System.Collections.Concurrent;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Core.Models;
    public interface ISearchRegistry
    {
        ConcurrentDictionary<SongJob, SearchInfo> Searches { get; }
    }

    public interface IDownloadRegistry
    {
        ConcurrentDictionary<string, ActiveDownload> Downloads { get; }
        ConcurrentDictionary<string, SongJob> DownloadedFiles { get; }
    }

    public interface IUserStats
    {
        ConcurrentDictionary<string, int> UserSuccessCounts { get; }
    }

    public class SessionRegistry : ISearchRegistry, IDownloadRegistry, IUserStats
    {
        public ConcurrentDictionary<SongJob, SearchInfo> Searches { get; } = new();
        public ConcurrentDictionary<string, ActiveDownload> Downloads { get; } = new();
        public ConcurrentDictionary<string, SongJob> DownloadedFiles { get; } = new();
        public ConcurrentDictionary<string, int> UserSuccessCounts { get; } = new();
    }
