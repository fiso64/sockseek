using Sockseek.Api;

namespace Sockseek.Server.Planning;

internal static class SelectionExpressionValidator
{
    internal static void Validate(RefSelectionExpressionDto selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Refs);
        if (selection.Mode is not (RefSelectionMode.Only or RefSelectionMode.AllExcept))
            throw new ArgumentException("Selection mode is invalid.");
        if (selection.Refs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Selection refs cannot be empty.");
    }
}
