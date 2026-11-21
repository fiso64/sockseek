using Sockseek.Core.Settings;

namespace Sockseek.Cli;

/// Settings consumed by the CLI when connecting to an existing daemon.
public class RemoteSettings
{
    [SettingOption("--remote", "--server-url")]
    public string? ServerUrl { get; set; }
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ServerUrl);
    [SettingOption("--detach")]
    public bool Detach { get; set; }
    [SettingOption("--remote-api-key")]
    public string? ApiKey { get; set; }
    [SettingOption("--remote-api-key-file")]
    public string? ApiKeyFile { get; set; }

    public HttpClient CreateHttpClient()
    {
        if (ApiKey is not null && ApiKeyFile is not null)
            throw new ArgumentException("Configure one remote API key or key file, not both.");
        var secret = ApiKeyFile is null ? ApiKey : Server.Authentication.AuthenticationOptions.ReadSecretFile(ApiKeyFile);
        return Api.SockseekApiClient.CreateHttpClient(ServerUrl
            ?? throw new ArgumentException("A remote server URL is required."), secret);
    }
}
