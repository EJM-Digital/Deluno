using System.Security.Claims;
using System.Text.Json;
using Deluno.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Security;

/// <summary>What a script is told when its key may not use a route (#1329).</summary>
public sealed class ForbiddenAnswerNamesTheScopeLowGapTests
{
    private static async Task<(int Status, JsonElement Body)> RefusedAsync(params string[] grantedScopes)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/movies";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            grantedScopes.Select(scope => new Claim(DelunoAuthenticationHandler.ScopeClaimType, scope)),
            "test"));
        var policy = new AuthorizationPolicyBuilder().AddRequirements(new DelunoScopeRequirement("read")).Build();

        await new DelunoAuthorizationResultHandler().HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body));
    }

    [Fact]
    public async Task A_credential_with_no_scopes_is_told_it_carries_none_and_which_one_it_needs()
    {
        var (status, body) = await RefusedAsync();

        Assert.Equal(403, status);
        Assert.Equal("This credential carries no scopes, so it cannot use this route. It needs 'read'.", body.GetProperty("error").GetString());
        Assert.Equal(0, body.GetProperty("granted").GetArrayLength());
    }

    [Fact]
    public async Task A_credential_with_other_scopes_is_told_which_one_it_lacks()
    {
        var (status, body) = await RefusedAsync("queue");

        Assert.Equal(403, status);
        Assert.Equal("This credential does not have the 'read' scope.", body.GetProperty("error").GetString());
        Assert.Equal("queue", Assert.Single(body.GetProperty("granted").EnumerateArray()).GetString());
    }
}
