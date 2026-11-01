using System.Text.Json.Serialization;

namespace Sockseek.Core.Jobs;

/// <summary>
/// Source-owned descriptions carried with a job and retained with reviewed work.
/// Consumers read these values without interpreting provider URLs or credentials.
/// </summary>
public sealed record JobSourceInfo(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UseResultName = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviewTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NestedPreviewTitle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviewDetail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CollectionLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CollectionItemName = null)
{
    public string? CollectionDescription(int count) => CollectionLabel is null ? null
        : CollectionItemName is null ? CollectionLabel
        : $"{CollectionLabel} · {count} {CollectionItemName}{(count == 1 ? "" : "s")}";
}
