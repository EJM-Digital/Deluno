using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Platform.Security.Hardening;

/// <summary>
/// Endpoint extension exposing the active <see cref="SecretsBackendInfo"/>
/// at <c>GET /api/diagnostics/secrets-backend</c>. The UI surfaces this
/// in the diagnostics page so users can see whether their credentials are
/// hardened at rest and, if not, what to fix.
/// </summary>
public static class SecretsDiagnosticsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSecretsDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/diagnostics/secrets-backend",
            (ISecretsBackendInfoProvider provider) =>
            {
                var info = provider.Info;
                return Results.Ok(new
                {
                    backend = info.Backend.ToString(),
                    isHardened = info.IsHardened,
                    source = info.Source,
                    warnings = info.Warnings,
                });
            })
            .WithName("GetSecretsBackend")
            .WithTags("Diagnostics");

        return endpoints;
    }
}
