using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sockseek.Api;

public static class SockseekApiJson
{
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        ConfigureSerializerOptions(options);
        return options;
    }

    public static void ConfigureSerializerOptions(JsonSerializerOptions options)
    {
        if (!options.TypeInfoResolverChain.Contains(SockseekApiJsonContext.Default))
            options.TypeInfoResolverChain.Insert(0, SockseekApiJsonContext.Default);
    }
}
