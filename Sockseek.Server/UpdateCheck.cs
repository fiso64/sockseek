using System.Text.Json;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Core;

namespace Sockseek.Server;

/// <summary>
/// Asks GitHub once a day whether a newer release exists and publishes the
/// answer in the daemon state. It only informs: installs differ too much
/// (packages, containers, archives) for the daemon to update itself.
/// </summary>
public sealed class UpdateCheck(
    IOptions<ServerOptions> options,
    EngineStateStore state,
    DaemonRuntime runtime,
    ILogger<UpdateCheck> logger) : BackgroundService
{
    private const string ReleasesUrl = "https://api.github.com/repos/fiso64/sockseek/releases?per_page=100";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Alpha versions are builds from source, not releases (Directory.Build.props):
        // every published release would look like an update to them.
        if (!ReleaseVersion.TryParse(ProductVersion.Release, out var current) || current.Prerelease.StartsWith("alpha", StringComparison.Ordinal))
            return;

        // Turning the check on asks at once; turning it off withdraws the answer.
        var reloaded = new SemaphoreSlim(0);
        void Wake() => reloaded.Release();
        runtime.HostOptionsReloaded += Wake;
        try
        {
            bool? applied = null;
            long checkedAt = 0;
            string? announced = null;
            while (true)
            {
                bool enabled = options.Value.CheckForUpdates;
                TimeSpan due = Interval - TimeProvider.System.GetElapsedTime(checkedAt);
                if (!enabled)
                {
                    if (applied != false) state.UpdateAvailableUpdate(null);
                }
                else if (applied != true || due <= TimeSpan.Zero)
                {
                    checkedAt = TimeProvider.System.GetTimestamp();
                    due = Interval;
                    try
                    {
                        var update = await FetchAsync(current, stoppingToken).ConfigureAwait(false);
                        state.UpdateAvailableUpdate(update);
                        if (update != null && update.Version != announced)
                            logger.LogInformation("Sockseek {Version} is available (running {Current}): {Url}", update.Version, ProductVersion.Release, update.Url);
                        announced = update?.Version ?? announced;
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Being offline is ordinary; the last known answer stays until the next check.
                        logger.LogDebug(exception, "Update check failed");
                    }
                }
                applied = enabled;
                await reloaded.WaitAsync(enabled ? due : Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            runtime.HostOptionsReloaded -= Wake;
        }
    }

    private static async Task<AvailableUpdateDto?> FetchAsync(ReleaseVersion current, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects requests without a user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Sockseek");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var stream = await http.GetStreamAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
        using var releases = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Newest(current, releases.RootElement);
    }

    /// <summary>
    /// The highest release above <paramref name="current"/> in GitHub's release
    /// list. A prerelease is only offered to someone already running one.
    /// </summary>
    internal static AvailableUpdateDto? Newest(ReleaseVersion current, JsonElement releases)
    {
        (ReleaseVersion Version, string Url)? newest = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()
                || !ReleaseVersion.TryParse(release.GetProperty("tag_name").GetString(), out var version)
                || (version.Prerelease.Length != 0 && current.Prerelease.Length == 0)
                || version.CompareTo(newest?.Version ?? current) <= 0)
                continue;
            newest = (version, release.GetProperty("html_url").GetString()!);
        }
        return newest is { } found ? new AvailableUpdateDto(found.Version.ToString(), found.Url) : null;
    }
}

/// <summary>A release tag such as <c>v4.0.0-beta.2</c>, ordered by Semantic Versioning precedence.</summary>
internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (text == null) return false;
        // Build metadata does not take part in precedence.
        string[] parts = text.TrimStart('v').Split('+', 2)[0].Split('-', 2);
        string[] numbers = parts[0].Split('.');
        if (numbers.Length != 3
            || !int.TryParse(numbers[0], out int major) || !int.TryParse(numbers[1], out int minor) || !int.TryParse(numbers[2], out int patch))
            return false;
        version = new(major, minor, patch, parts.Length == 2 ? parts[1] : "");
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int order = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (order != 0 || Prerelease == other.Prerelease) return order;
        // A release follows its own prereleases.
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;

        string[] mine = Prerelease.Split('.'), theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            bool mineNumeric = int.TryParse(mine[i], out int a), theirsNumeric = int.TryParse(theirs[i], out int b);
            order = mineNumeric && theirsNumeric ? a.CompareTo(b)
                : mineNumeric ? -1
                : theirsNumeric ? 1
                : string.CompareOrdinal(mine[i], theirs[i]);
            if (order != 0) return order;
        }
        return mine.Length.CompareTo(theirs.Length);
    }

    public override string ToString() => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
