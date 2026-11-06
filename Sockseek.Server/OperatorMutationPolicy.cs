namespace Sockseek.Server;

/// <summary>
/// Stable metadata and filter seam for daemon mutations that require operator
/// authority. v4 currently has one trust domain and therefore permits the
/// request; the authentication roadmap can replace the filter decision without
/// rediscovering or rewriting each mutation endpoint.
/// </summary>
public static class OperatorMutationPolicy
{
    public const string Name = "Sockseek.Operator";

    public static RouteHandlerBuilder RequireOperator(
        this RouteHandlerBuilder builder)
        => builder
            .WithMetadata(new OperatorMutationMetadata(Name))
            .AddEndpointFilter<OperatorMutationFilter>();

    public static RouteHandlerBuilder RequireOperatorMutation(
        this RouteHandlerBuilder builder)
        => builder.RequireOperator();
}

public sealed record OperatorMutationMetadata(string PolicyName);

public interface IOperatorMutationAuthorizer
{
    /// <summary>
    /// Returns null when the request may proceed, or a stable HTTP rejection
    /// result before the endpoint performs target lookup or mutation.
    /// </summary>
    ValueTask<IResult?> GetRejectionAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class CurrentTrustDomainOperatorAuthorizer
    : IOperatorMutationAuthorizer
{
    public ValueTask<IResult?> GetRejectionAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IResult?>(null);
}

internal sealed class OperatorMutationFilter(
    IOperatorMutationAuthorizer authorizer) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        IResult? rejection = await authorizer.GetRejectionAsync(
            context.HttpContext,
            context.HttpContext.RequestAborted);
        return rejection ?? await next(context);
    }
}
