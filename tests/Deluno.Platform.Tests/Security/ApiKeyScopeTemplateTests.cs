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
        Assert.Equal("all", ApiKeyScopeTemplates.Resolve("all").Scopes);
        Assert.Equal("all", ApiKeyScopeTemplates.Resolve("*").Scopes);
        Assert.Equal("read, write, queue", ApiKeyScopeTemplates.Resolve("read, write, queue").Scopes);
        Assert.False(ApiKeyScopeTemplates.Resolve("read, health").IsGranted);
    }

    /// <summary>
    /// The one that got out. A key created without a scope was given every
    /// scope: validation treated "nothing requested" as valid, and the
    /// repository filled the blank with <c>all</c>. Asking for the narrowest
    /// template Deluno advertises returned a full-access key and a 200.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void Saying_nothing_does_not_grant_everything(string? scopes)
    {
        var resolution = ApiKeyScopeTemplates.Resolve(scopes);

        Assert.False(resolution.IsGranted);
        Assert.Null(resolution.Scopes);

        // The message has to be actionable, or the refusal just moves the
        // problem: it names the templates and the scopes it would accept.
        Assert.Contains("dashboard-read", resolution.Error);
        Assert.Contains("read", resolution.Error);
    }

    /// <summary>
    /// The catalogue publishes template ids. If the create endpoint ignores
    /// them, sending the id Deluno just advertised is an unrecognised field,
    /// and an unrecognised field is an absent one.
    /// </summary>
    [Fact]
    public void A_template_id_is_understood_rather_than_ignored()
    {
        Assert.Equal("read", ApiKeyScopeTemplates.Resolve("dashboard-read").Scopes);
        Assert.Equal("read, write, queue", ApiKeyScopeTemplates.Resolve("automation").Scopes);
        Assert.Equal("read, write, queue, imports", ApiKeyScopeTemplates.Resolve("native-mobile").Scopes);
        Assert.Equal("all", ApiKeyScopeTemplates.Resolve("full-local").Scopes);
    }

    /// <summary>
    /// Every template has to resolve, or the catalogue advertises something the
    /// create endpoint refuses.
    /// </summary>
    [Fact]
    public void Every_advertised_template_can_actually_be_asked_for()
    {
        Assert.All(ApiKeyScopeTemplates.All, template =>
        {
            var resolution = ApiKeyScopeTemplates.Resolve(template.Id);
            Assert.True(resolution.IsGranted, $"{template.Id} is advertised but refused: {resolution.Error}");
            Assert.Equal(string.Join(", ", template.Scopes), resolution.Scopes);
        });
    }

    /// <summary>
    /// A template beside loose scopes is ambiguous about which wins. Guessing
    /// would guess wide, which is the failure this whole file is about.
    /// </summary>
    [Fact]
    public void A_template_mixed_with_scopes_is_refused_rather_than_guessed_at()
    {
        var resolution = ApiKeyScopeTemplates.Resolve("dashboard-read, system");

        Assert.False(resolution.IsGranted);
        Assert.Contains("dashboard-read", resolution.Error);
    }
}
