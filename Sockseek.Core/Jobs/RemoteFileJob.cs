using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;

/// <summary>An ordinary remote-file download with an exact peer target.</summary>
public sealed class RemoteFileJob : FileDownloadJob
{
    public RemoteFileJob(PeerFileTarget target, RelativeOutputPath? outputPath = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        OutputPath = outputPath ?? RelativeOutputPath.FromRemoteFile(target);
        ItemName = Path.GetFileName(target.Filename.Replace('\\', Path.DirectorySeparatorChar));
        FileSize = target.Size;
    }

    public PeerFileTarget Target { get; }
    public RelativeOutputPath OutputPath { get; }

    protected override bool DefaultCanBeSkipped => true;

    public override string ToString(bool noInfo) => ItemName ?? Target.Filename;
}
