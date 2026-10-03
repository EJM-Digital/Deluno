using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Security.Hardening;

/// <summary>
/// DI registration for the hardened ISecretProtector pipeline. Replaces
/// the legacy single-line <c>AddSingleton&lt;ISecretProtector, DataProtectionSecretProtector&gt;()</c>
/// with a factory-driven selection (Windows DPAPI / FileBacked AES /
/// DataProtection fallback).
/// </summary>
public static class SecretsHardeningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISecretProtector"/>
    /// with the selected backend. <paramref name="masterKeyFilePath"/>
    /// is where the AES master key lives when the FileBacked backend is
    /// in use (typically <c>&lt;dataRoot&gt;/secrets/master.key</c>).
    /// </summary>
    public static IServiceCollection AddDelunoSecretsHardening(
        this IServiceCollection services,
        string masterKeyFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyFilePath);

        services.AddSingleton<ISecretProtector>(sp =>
        {
            var factory = new SecretProtectorFactory(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IDataProtectionProvider>(),
                masterKeyFilePath,
                sp.GetRequiredService<ILogger<SecretProtectorFactory>>());
            return factory.Build().Protector;
        });
        services.AddSingleton(sp => new SecretValueMigrator(sp.GetRequiredService<ISecretProtector>()));

        return services;
    }
}
