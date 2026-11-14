using Sockseek.Core.Models;
using System.Collections.Frozen;

namespace Sockseek.Core.Jobs;

public abstract record RemoteDirectorySource
{
    private RemoteDirectorySource() { }

    public sealed record PeerDirectory : RemoteDirectorySource
    {
        public PeerDirectory(PeerDirectoryIdentity directory, IEnumerable<string>? excludedFilenames = null)
        {
            ArgumentNullException.ThrowIfNull(directory);
            directory.RequireAdvertisedFolder();
            Directory = directory;
            ExcludedFilenames = (excludedFilenames ?? []).ToFrozenSet(StringComparer.Ordinal);
        }

        public PeerDirectoryIdentity Directory { get; }
        public IReadOnlySet<string> ExcludedFilenames { get; }
    }

    public sealed record Resolved : RemoteDirectorySource
    {
        public Resolved(DirectoryTransferPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            Plan = plan;
        }

        public DirectoryTransferPlan Plan { get; }
    }
}

/// <summary>An ordinary remote-directory download with exactly one explicit source case.</summary>
public sealed class RemoteDirectoryJob : DirectoryDownloadJob
{
    public RemoteDirectoryJob(RemoteDirectorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        switch (source)
        {
            case RemoteDirectorySource.PeerDirectory peer:
                ItemName = Path.GetFileName(peer.Directory.FolderPath.Replace('\\', Path.DirectorySeparatorChar));
                break;
            case RemoteDirectorySource.Resolved resolved:
                ItemName = resolved.Plan.DisplayRoot.Length == 0 ? "Selected files" : resolved.Plan.DisplayRoot;
                BeginDirectoryAttempt(resolved.Plan);
                break;
        }
    }

    public RemoteDirectorySource Source { get; }
    public PeerDirectorySnapshot? ResolvedDirectory { get; internal set; }

    protected override bool DefaultCanBeSkipped => false;

    public override string ToString(bool noInfo) => ItemName ?? Source.ToString();
}
