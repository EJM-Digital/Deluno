using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSecurityModule(this IServiceCollection services)
    {
        services.AddSingleton<ISecurityRepository, SqliteSecurityRepository>();
        services.AddHttpContextAccessor();
        services.AddAuthentication(DelunoAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DelunoAuthenticationHandler>(
                DelunoAuthenticationHandler.SchemeName,
                _ => { });
        services.AddSingleton<IAuthorizationHandler, DelunoScopeAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(DelunoAuthorizationPolicies.Read, policy => policy.AddRequirements(new DelunoScopeRequirement("read")))
            .AddPolicy(DelunoAuthorizationPolicies.Write, policy => policy.AddRequirements(new DelunoScopeRequirement("write")))
            .AddPolicy(DelunoAuthorizationPolicies.Queue, policy => policy.AddRequirements(new DelunoScopeRequirement("queue")))
            .AddPolicy(DelunoAuthorizationPolicies.Imports, policy => policy.AddRequirements(new DelunoScopeRequirement("imports", "queue")))
            .AddPolicy(DelunoAuthorizationPolicies.System, policy => policy.AddRequirements(new DelunoScopeRequirement("system")));
        return services;
    }

    /// <summary>
    /// Registers the hardened ISecretProtector pipeline. Call this from the
    /// host (which knows the data root). If neither this nor a manual
    /// <c>AddSingleton&lt;ISecretProtector&gt;</c> registration is in place,
    /// services that depend on <see cref="ISecretProtector"/> will fail to
    /// resolve.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="masterKeyFilePath">
    /// Filesystem path where the AES master key lives when the FileBacked
    /// backend is selected. Typically <c>&lt;dataRoot&gt;/secrets/master.key</c>.
    /// </param>
    public static IServiceCollection AddDelunoSecuritySecrets(
        this IServiceCollection services,
        string masterKeyFilePath)
        => services.AddDelunoSecretsHardening(masterKeyFilePath);
}
