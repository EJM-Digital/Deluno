using Deluno.Worker;
using Microsoft.AspNetCore.Http;

namespace Deluno.Worker.Tests.Gate;

/// <summary>The scopes the API gate asks of a key when a route names none, branch by branch (#1296).</summary>
public sealed class ApiScopesResolvedTests
{
    [Theory]
    [InlineData("/api/movies", "GET", "read")]
    [InlineData("/api/movies", "HEAD", "read")]
    [InlineData("/api/movies", "OPTIONS", "read")]
    [InlineData("/api/integrations/processors/handoffs", "GET", "imports")]
    [InlineData("/api/queue/abc", "DELETE", "queue")]
    [InlineData("/api/command", "POST", "write,queue,system")]
    [InlineData("/api/download-clients", "POST", "queue")]
    [InlineData("/api/download-dispatches/x", "POST", "queue")]
    [InlineData("/api/filesystem/import", "POST", "imports,queue")]
    [InlineData("/api/integrations/processors/events", "POST", "imports")]
    [InlineData("/api/integrations/external/trigger-refresh", "POST", "queue")]
    [InlineData("/api/integrations/external/health", "GET", "read")]
    [InlineData("/api/integrations/external/health", "HEAD", "read")]
    [InlineData("/api/integrations/processors/handoffs/abc/dismiss", "POST", "imports")]
    [InlineData("/api/integrations/processors/connections/abc", "DELETE", "imports")]
    [InlineData("/api/integrations/a-route-nobody-listed", "GET", "")]
    [InlineData("/api/integrations/external/health", "POST", "")]
    [InlineData("/api/backups", "POST", "system")]
    [InlineData("/api/movies", "POST", "write")]
    [InlineData("/api/movies/1", "PUT", "write")]
    public void Each_route_and_method_asks_for_the_scopes_the_docs_name(string path, string method, string expected)
        => Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), DelunoRequestPipeline.ResolveApiScopes(new PathString(path), method));
}
