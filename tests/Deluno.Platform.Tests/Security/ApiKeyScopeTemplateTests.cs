using Deluno.Security;

namespace Deluno.Platform.Tests.Security;

public sealed class ApiKeyScopeTemplateTests
{
    [Fact]
    public void Templates_only_use_scopes_enforced_by_the_security_module()
    {
        var allowed = DelunoAuthorizationPolicies.AllScopes
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(ApiKeyScopeTemplates.All, template =>
            Assert.All(template.Scopes, scope =>
                Assert.True(
                    scope is "all" or "*" || allowed.Contains(scope),
                    $"{template.Id} advertises unsupported scope {scope}.")));
    }

    [Fact]
    public void Unknown_scopes_are_rejected_but_full_local_alias_is_allowed()
    {
        Assert.Empty(ApiKeyScopeTemplates.Validate("all"));
        Assert.Empty(ApiKeyScopeTemplates.Validate("*"));
        Assert.Empty(ApiKeyScopeTemplates.Validate("read, write, queue"));
        Assert.NotEmpty(ApiKeyScopeTemplates.Validate("read, health"));
    }
}
