using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class OperatorMutationPolicyTests
{
    [TestMethod]
    public async Task OperatorOnlyResourcesCarryOneNamedPolicy()
    {
        await using var app = ServerHost.Build(
            [],
            new ServerOptions(),
            "http://127.0.0.1:0");
        var endpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(source => source.Endpoints);

        string[] expected =
        [
            "/api/sharing/scans",
            "/api/sharing/scans/{scanId:guid}/cancel",
            "/api/transfers/{transferId:guid}/cancel",
            "/api/transfers/cancel",
            "/api/transfers/{transferId:guid}/archive",
            "/api/transfers/archive",
        ];
        foreach (string pattern in expected)
        {
            RouteEndpoint endpoint = endpoints
                .OfType<RouteEndpoint>()
                .Single(candidate =>
                    candidate.RoutePattern.RawText == pattern
                    && candidate.Metadata
                        .GetMetadata<HttpMethodMetadata>()!
                        .HttpMethods.Contains("POST"));
            OperatorMutationMetadata? metadata =
                endpoint.Metadata.GetMetadata<OperatorMutationMetadata>();

            Assert.IsNotNull(metadata, $"Missing operator policy on {pattern}.");
            Assert.AreEqual(OperatorMutationPolicy.Name, metadata.PolicyName);
        }

        var chatEndpoints = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith("/api/chat", StringComparison.Ordinal) == true
                || endpoint.RoutePattern.RawText?.StartsWith("/api/notifications", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.IsTrue(chatEndpoints.Length > 0);
        foreach (RouteEndpoint endpoint in chatEndpoints)
        {
            OperatorMutationMetadata? metadata =
                endpoint.Metadata.GetMetadata<OperatorMutationMetadata>();
            Assert.IsNotNull(
                metadata,
                $"Missing operator policy on {endpoint.RoutePattern.RawText}.");
            Assert.AreEqual(OperatorMutationPolicy.Name, metadata.PolicyName);
        }

        var userBrowseEndpoints = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith("/api/user-browses", StringComparison.Ordinal) == true
                || endpoint.RoutePattern.RawText?.StartsWith("/api/users/{username}/", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.IsTrue(userBrowseEndpoints.Length > 0);
        foreach (RouteEndpoint endpoint in userBrowseEndpoints)
        {
            OperatorMutationMetadata? metadata = endpoint.Metadata
                .GetMetadata<OperatorMutationMetadata>();
            Assert.IsNotNull(metadata, $"Missing operator policy on {endpoint.RoutePattern.RawText}.");
            Assert.AreEqual(OperatorMutationPolicy.Name, metadata.PolicyName);
        }
    }
}
