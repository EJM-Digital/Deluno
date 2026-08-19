namespace Deluno.Security;

/// <summary>
/// Named rate-limit policies. The names are shared between the host, which
/// configures the limiter, and the endpoint definitions, which attach it — so
/// a typo cannot silently leave an endpoint unprotected.
/// </summary>
public static class DelunoRateLimitPolicies
{
    /// <summary>Unauthenticated credential submission.</summary>
    public const string Login = "deluno-login";
}
