namespace Deluno.Security;

public static class DelunoAuthorizationPolicies
{
    public const string Read = "deluno:read";
    public const string Write = "deluno:write";
    public const string Queue = "deluno:queue";
    public const string Imports = "deluno:imports";
    public const string System = "deluno:system";

    public static readonly string[] AllScopes = ["read", "write", "queue", "imports", "system"];
}

/// <summary>
/// Marks an intentionally public API endpoint. The endpoint-coverage test uses
/// this in addition to AllowAnonymous so publishing a route is explicit.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class DelunoPublicEndpointAttribute : Attribute;
