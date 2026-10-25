using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Sockseek.Api;
using Sockseek.Cli;
using Sockseek.Core.Settings;

namespace Sockseek.Benchmarks;

/// Warm configuration costs. Measure process startup separately, without dotnet run/build.
[MemoryDiagnoser]
public class ConfigurationBenchmarks
{
    private static readonly string[] arguments =
    [
        "Artist - Title", "--strict-artist", "--format", "flac",
        "--search-timeout", "8000", "--skip-existing", "false",
        "--on-complete", "-- first", "--on-complete", "+ -- second",
    ];
    private readonly DownloadSettings settings = new();
    private readonly JsonSerializerOptions json = SockseekApiJson.CreateSerializerOptions();
    private SettingsPatch patch = null!;

    [GlobalSetup]
    public void Setup()
    {
        patch = ConfigManager.CreateCliDownloadSettingsPatch(arguments)!;
        patch.Validate<DownloadSettings>();
    }

    [Benchmark]
    public DownloadSettings Bind() => ConfigManager.BindCliDownloadTokens(arguments);

    [Benchmark]
    public SettingsPatch? CreateRemotePatch() => ConfigManager.CreateCliDownloadSettingsPatch(arguments);

    [Benchmark]
    public string SerializeRemotePatch() => JsonSerializer.Serialize(patch, json);

    [Benchmark]
    public DownloadSettings Clone() => SettingsCloner.Clone(settings);

    [Benchmark]
    public DownloadSettings ApplyProfile()
    {
        var result = new DownloadSettings();
        patch.ApplyTo(result);
        return result;
    }
}
