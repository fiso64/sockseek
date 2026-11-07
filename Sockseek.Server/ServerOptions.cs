using Sockseek.Core.Settings;
using Sockseek.Persistence.Write;
using Sockseek.Api;
using Soulseek;

namespace Sockseek.Server;

public sealed class ServerOptions
{
    public string Name { get; set; } = "Sockseek";
    public Authentication.AuthenticationOptions Authentication { get; set; } = new();
    /// <summary>Maximum time allowed for graceful connection and service shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public EngineSettings Engine { get; set; } = new();
    /// <summary>
    /// Optional full operator replacement. Prefer sparse profiles/launch patches;
    /// when supplied, every value in this object is treated as intentional.
    /// </summary>
    public DownloadSettings? DefaultDownload { get; set; }
    public DownloadSettingsPatchDto? OperatorDownloadSettings { get; set; }
    public DownloadSettingsPatchDto? LaunchDownloadSettings { get; set; }
    public IReadOnlyList<string> LaunchProfileNames { get; set; } = [];
    public ProfileCatalog Profiles { get; set; } = ProfileCatalog.Empty;
    public string? ConfigDir { get; set; }
    public Func<EngineSettings, ISoulseekClient>? ClientFactory { get; set; }
    public ServerPersistenceOptions Persistence { get; set; } = new();
}

public sealed class ServerPersistenceOptions
{
    public bool Enabled { get; set; }
    public string? DataDirectory { get; set; }
    public int CommandQueueCapacity { get; set; } = 2_048;
    public int BackgroundCommandQueueCapacity { get; set; } = 2_048;
    public int ProgressEntityCapacity { get; set; } = 512;
    public int SearchMutationQueueCapacity { get; set; } = 256;
    public int SearchResultFlushCount { get; set; } = PersistenceWriterOptions.DefaultSearchResultFlushCount;
    public TimeSpan SearchResultFlushInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan TransferProgressFlushInterval { get; set; } = TimeSpan.FromSeconds(3);
    public bool RetentionEnabled { get; set; } = true;
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan? CompletedJobHistoryAge { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan? UnsuccessfulJobHistoryAge { get; set; } = TimeSpan.FromDays(180);
    public int? MaximumRetainedJobs { get; set; } = 100_000;
    public TimeSpan? SearchResultAge { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan? TransferHistoryAge { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan? PrivateMessageHistoryAge { get; set; }
    public TimeSpan? RoomMessageHistoryAge { get; set; } = TimeSpan.FromDays(30);
    public int RetentionBatchSize { get; set; } = 500;
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
