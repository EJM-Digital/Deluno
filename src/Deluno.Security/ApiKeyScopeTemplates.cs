using Deluno.Security.Contracts;

namespace Deluno.Security;

public static class ApiKeyScopeTemplates
{
    public static readonly IReadOnlyList<ApiKeyScopeTemplate> All =
    [
        new(
            "dashboard-read",
            "Read-only dashboard",
            "Observe health, queue, activity, catalogue and import progress without changing Deluno state.",
            ["read"],
            ["health", "queue inspection", "activity", "catalogue reads", "import progress"]),
        new(
            "automation",
            "Personal automation",
            "Run searches and catalogue automation, inspect the resulting queue, and read outcomes.",
            ["read", "write", "queue"],
            ["catalogue add/import", "search requests", "queue inspection", "automation controls"]),
        new(
            "home-assistant",
            "Home Assistant",
            "Read Deluno sensors and invoke the safe search, pause and resume actions exposed by the integration.",
            ["read", "write", "queue"],
            ["readiness", "queue/import counts", "attention items", "search now", "automation pause/resume"]),
        new(
            "native-mobile",
            "Native mobile app",
            "Use the mobile control surface without granting installation, backup, update, or API-key administration access.",
            ["read", "write", "queue", "imports"],
            ["catalogue management", "search and queue actions", "existing-library import", "recovery actions"]),
        new(
            "full-local",
            "Full local API",
            "Reserved for a trusted local operator that needs every API scope. Prefer a narrower template for integrations.",
            ["all"],
            ["all authenticated API operations"])
    ];

    private static readonly IReadOnlySet<string> AllowedScopes =
        new HashSet<string>(DelunoAuthorizationPolicies.AllScopes, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string[]> Validate(string? scopes)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var requested = (scopes ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => scope.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length == 0 || requested.Any(scope => scope is "all" or "*"))
        {
            return errors;
        }

        var unsupported = requested
            .Where(scope => !AllowedScopes.Contains(scope))
            .ToArray();
        if (unsupported.Length > 0)
        {
            errors["scopes"] =
            [
                $"Unsupported scope(s): {string.Join(", ", unsupported)}. Use: {string.Join(", ", DelunoAuthorizationPolicies.AllScopes)}."
            ];
        }

        return errors;
    }
}
