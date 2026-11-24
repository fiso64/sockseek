using Sockseek.Api;
using Sockseek.Core.Services;

namespace Sockseek.Server;

internal static class ApiErrors
{
    public static bool IsInvalidRequest(Exception exception)
        => exception is ArgumentException
            || exception.Message.StartsWith("Input error:", StringComparison.Ordinal);

    /// <summary>
    /// Answers whatever an endpoint throws with its feature's own error
    /// responses, so the handler itself only describes the successful path.
    /// </summary>
    public static RouteHandlerBuilder MapErrors(this RouteHandlerBuilder route, Func<Exception, IResult> failure)
        => route.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context).ConfigureAwait(false); }
            catch (Exception exception) { return failure(exception); }
        });

    public static IResult BadRequest(Exception exception)
        => Results.BadRequest(new ApiErrorDto(exception.Message,
            exception is UnsupportedNameFormatVariableException
                ? "invalid-name-format-variable" : "InvalidRequest"));
}
