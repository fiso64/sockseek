using Sockseek.Api;
using Sockseek.Core.Services;

namespace Sockseek.Server;

internal static class ApiErrors
{
    public static bool IsInvalidRequest(Exception exception)
        => exception is ArgumentException
            || exception.Message.StartsWith("Input error:", StringComparison.Ordinal);

    public static IResult BadRequest(Exception exception)
        => Results.BadRequest(new ApiErrorDto(exception.Message,
            exception is UnsupportedNameFormatVariableException
                ? "invalid-name-format-variable" : "InvalidRequest"));
}
