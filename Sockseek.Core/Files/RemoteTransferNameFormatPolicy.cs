using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

/// <summary>
/// Projects an inherited name format onto ordinary remote-transfer capabilities.
/// Explicit overrides are validated at the submission boundary; an unsupported
/// inherited format falls back to the ordinary filename/tree placement.
/// </summary>
public static class RemoteTransferNameFormatPolicy
{
    public static bool ApplyInherited(OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            NameFormatRenderer.ValidateVariables(
                output.NameFormat,
                NameFormatVariableProvider.Supported);
            return true;
        }
        catch (UnsupportedNameFormatVariableException)
        {
            output.NameFormat = "";
            return false;
        }
    }
}
