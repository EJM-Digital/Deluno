using Deluno.Security.Data;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoSecurityModule(this IServiceCollection services)
    {
        // #1280: the framework throws on a request it cannot bind, instead of answering an empty 400, so
        // UseDelunoBadRequestAnswers can say what was wrong.
        services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddSingleton<ISecurityRepository, SqliteSecurityRepository>();
        // #939: Allowed Hosts.
        services.AddSingleton<IAllowedHostsRepository, SqliteAllowedHostsRepository>();
        services.AddSingleton<IAllowedHostsSettingsCache, AllowedHostsSettingsCache>();
        services.AddSingleton<IForwardedHostTracker, ForwardedHostTracker>();
        services.AddSingleton<IAllowedHostsCandidateProvider, AllowedHostsCandidateProvider>();
        // #993: Skip sign-in for devices on the local network.
        services.AddSingleton<ILocalNetworkAuthRepository, SqliteLocalNetworkAuthRepository>();
        services.AddSingleton<ILocalNetworkAuthSettingsCache, LocalNetworkAuthSettingsCache>();
        services.AddSingleton<ITrustedProxyClientAddressResolver, TrustedProxyClientAddressResolver>();
        // Real logging to Activity is registered by the host (Deluno.Worker),
        // which can see both this module and Deluno.Jobs; this default keeps
        // the feature usable for any composition that does not.
        services.TryAddSingleton<ILocalNetworkAuthChangeNotifier, NoOpLocalNetworkAuthChangeNotifier>();
        services.AddHttpContextAccessor();
        services.AddAuthentication(DelunoAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DelunoAuthenticationHandler>(
                DelunoAuthenticationHandler.SchemeName,
                _ => { });
        services.AddSingleton<IAuthorizationHandler, DelunoScopeAuthorizationHandler>();
        // So a 403 says which scope was wanted rather than arriving empty.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, DelunoAuthorizationResultHandler>();
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
