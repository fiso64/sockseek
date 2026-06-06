namespace Sockseek.Core;

public sealed record PathVariableContext(string? ConfigDir = null)
{
    public static PathVariableContext Empty { get; } = new();
}
