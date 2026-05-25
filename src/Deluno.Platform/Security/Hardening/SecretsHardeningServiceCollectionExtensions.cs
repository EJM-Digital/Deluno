using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Security.Hardening;

/// <summary>
/// DI registration for the hardened ISecretProtector pipeline. Replaces
/// the legacy single-line <c>AddSingleton&lt;ISecretProtector, DataProtectionSecretProtector&gt;()</c>
/// with a factory-driven selection (Windows DPAPI / FileBacked AES /
/// DataProtection fallback) that surfaces backend info via
/// <see cref="ISecretsBackendInfoProvider"/>.
/// </summary>
public static class SecretsHardeningServiceCollectionExtensions
{
    /// <summary>Internal: holds the single composite output of the factory.</summary>
    internal sealed record SecretsHardeningBundle(ISecretProtector Protector, SecretsBackendInfo Info);

    /// <summary>
    /// Registers <see cref="ISecretProtector"/> + <see cref="ISecretsBackendInfoProvider"/>
    /// with the selected backend. <paramref name="masterKeyFilePath"/>
    /// is where the AES master key lives when the FileBacked backend is
    /// in use (typically <c>&lt;dataRoot&gt;/secrets/master.key</c>).
    /// </summary>
    public static IServiceCollection AddDelunoSecretsHardening(
        this IServiceCollection services,
        string masterKeyFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyFilePath);

        // Build once per container; both ISecretProtector and
        // ISecretsBackendInfoProvider resolve to the same underlying
        // factory output. Avoids re-running ResolveOrCreateKey twice
        // (which would change the "source" string on a first-run install
        // and could trigger duplicate "created" warnings).
        services.AddSingleton(sp =>
        {
            var factory = new SecretProtectorFactory(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IDataProtectionProvider>(),
                masterKeyFilePath,
                sp.GetRequiredService<ILogger<SecretProtectorFactory>>());
            var (protector, info) = factory.Build();
            return new SecretsHardeningBundle(protector, info);
        });

        services.AddSingleton<ISecretProtector>(sp => sp.GetRequiredService<SecretsHardeningBundle>().Protector);
        services.AddSingleton<ISecretsBackendInfoProvider>(sp =>
            new SecretsBackendInfoProvider(sp.GetRequiredService<SecretsHardeningBundle>().Info));
        services.AddSingleton(sp => new SecretValueMigrator(sp.GetRequiredService<ISecretProtector>()));

        return services;
    }
}
