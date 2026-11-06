using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sockseek.Api;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

/// The wire DTOs stay independent of Core. Runtime application, validation and
/// differences use the same settings metadata as CLI/config/profile binding.
public static class DownloadSettingsPatchDtoMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlySet<string> ExplicitFields(DownloadSettingsPatchDto? patch)
        => ToCore(patch).ExplicitFields;

    public static SettingsPatch ToCore(DownloadSettingsPatchDto? patch)
        => Read(patch, typeof(DownloadSettings));

    public static void ApplyTo(DownloadSettings settings, DownloadSettingsPatchDto? patch)
        => ToCore(patch).ApplyTo(settings);

    public static void ApplyTo(SearchSettings settings, SearchSettingsPatchDto? patch)
        => Read(patch, typeof(SearchSettings)).ApplyTo(settings);

    public static DownloadSettingsPatchDto? FromDifference(DownloadSettings before, DownloadSettings after)
        => FromCore(SettingsPatch.Difference(before, after));

    public static DownloadSettingsPatchDto? Combine(DownloadSettingsPatchDto? first, DownloadSettingsPatchDto? second)
    {
        SettingsPatch a = ToCore(first), b = ToCore(second);
        return first == null ? second : second == null ? first
            : FromCore(new SettingsPatch([.. a.Operations, .. b.Operations]));
    }

    private static SettingsPatch Read(object? patch, Type root)
    {
        if (patch == null) return SettingsPatch.Empty;
        var operations = new List<SettingChange>();
        var leaves = SettingsSchema.For(root).Leaves.ToDictionary(member => member.Path, StringComparer.Ordinal);
        void Visit(JsonObject value, string prefix)
        {
            foreach (var (name, node) in value)
            {
                if (node == null) continue; // An omitted/nullable DTO field inherits.
                string member = char.ToUpperInvariant(name[0]) + name[1..];
                if (member.StartsWith("Clear", StringComparison.Ordinal))
                {
                    if (!node.GetValue<bool>()) continue;
                    string cleared = member[5..];
                    string key = char.ToLowerInvariant(cleared[0]) + cleared[1..];
                    if (value[key] != null)
                        throw new ArgumentException($"Cannot set and clear {prefix}{cleared} in one settings patch.");
                    operations.Add(new(prefix + cleared, null));
                    continue;
                }
                string path = prefix + member;
                if (leaves.TryGetValue(path, out var leaf))
                {
                    if (node is JsonObject collection && typeof(IEnumerable).IsAssignableFrom(leaf.ValueType)
                        && leaf.ValueType != typeof(string))
                    {
                        if (collection["replace"] is { } replace)
                            operations.Add(new(path, Element(replace)));
                        if (collection["append"] is { } append)
                            operations.Add(new(path, Element(append), path.EndsWith(".RequiredTrackTitles", StringComparison.Ordinal)
                                ? SettingOperation.AppendDistinct : SettingOperation.Append));
                    }
                    else operations.Add(new(path, Element(node)));
                }
                else if (node is JsonObject group) Visit(group, path + ".");
                else throw new ArgumentException($"Unknown setting '{path}'.");
            }
        }
        Visit((JsonObject)JsonSerializer.SerializeToNode(patch, patch.GetType(), Json)!, "");
        return new(operations);
    }

    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, Json);

    public static DownloadSettingsPatchDto? FromCore(SettingsPatch? patch)
    {
        if (patch?.HasOperations != true) return null;
        patch.Validate<DownloadSettings>();
        var document = new JsonObject();
        var leaves = SettingsSchema.For(typeof(DownloadSettings)).Leaves.ToDictionary(member => member.Path, StringComparer.Ordinal);
        IEnumerable<SettingChange> ExpandGroups()
        {
            foreach (var change in patch.Operations)
            {
                if (leaves.ContainsKey(change.Path))
                {
                    yield return change;
                    continue;
                }
                // Core permits replacing a whole settings group. The wire patch
                // must express every leaf, including explicit null resets.
                var settings = new DownloadSettings();
                new SettingsPatch([change]).ApplyTo(settings);
                foreach (var leaf in leaves.Values.Where(leaf => leaf.Path.StartsWith(change.Path + ".", StringComparison.Ordinal)))
                    yield return new(leaf.Path, leaf.Get(settings));
            }
        }
        foreach (var change in ExpandGroups())
        {
            string[] parts = change.Path.Split('.');
            var parent = document;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string key = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
                if (parent[key] is not JsonObject child) parent[key] = child = new();
                parent = child;
            }
            string member = parts[^1];
            string name = char.ToLowerInvariant(member[0]) + member[1..];
            JsonNode? node = JsonSerializer.SerializeToNode(change.Value, Json);
            bool collection = leaves.TryGetValue(change.Path, out var leaf)
                && typeof(IEnumerable).IsAssignableFrom(leaf.ValueType) && leaf.ValueType != typeof(string);
            if (change.Value == null)
            {
                parent.Remove(name);
                parent["clear" + member] = true;
            }
            else if (collection)
            {
                if (change.Operation == SettingOperation.Set) parent[name] = new JsonObject { ["replace"] = node };
                else
                {
                    if (parent[name] is not JsonObject edits) parent[name] = edits = new();
                    string key = edits["replace"] != null ? "replace" : "append";
                    if (edits[key] is not JsonArray target) edits[key] = target = new();
                    foreach (var item in (JsonArray)node!)
                        if (change.Operation != SettingOperation.AppendDistinct
                            || !target.Any(existing => JsonNode.DeepEquals(existing, item)))
                            target.Add(item?.DeepClone());
                }
            }
            else
            {
                parent.Remove("clear" + member);
                parent[name] = node;
            }
        }
        return document.Deserialize<DownloadSettingsPatchDto>(Json);
    }
}
