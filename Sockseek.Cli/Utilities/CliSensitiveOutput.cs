using Sockseek.Core.Extractors;

namespace Sockseek.Cli;

internal sealed class CliSensitiveOutput(CliOutputController output) : ISensitiveOutput
{
    public void WriteLine(string value)
        => output.WriteOutput(new CliOutputEvent.RawLine(value));
}
