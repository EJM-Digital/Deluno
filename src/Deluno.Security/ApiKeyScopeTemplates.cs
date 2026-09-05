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

    /// <summary>
    /// Turns what the caller asked for into the scopes the key will actually
    /// carry, or into an error saying why it will not be created.
    ///
    /// <para>Silence used to be the widest possible answer. A request that named
    /// no scope passed validation, and the repository then filled the blank with
    /// <c>all</c>, so "the caller did not say what this key may do" was answered
    /// with "then it may do everything". Asking for the narrowest template
    /// Deluno advertises returned a key with every scope and a 200, because the
    /// create endpoint took a scope list and the catalogue published template
    /// ids, and an unrecognised field is an absent field.</para>
    ///
    /// <para>For a permissions field the safe answer to silence is to refuse.
    /// A template id is now understood rather than ignored, so the id the
    /// catalogue just published is a thing you can send.</para>
    /// </summary>
    public static ApiKeyScopeResolution Resolve(string? scopes)
    {
        var requested = (scopes ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => scope.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length == 0)
        {
            return ApiKeyScopeResolution.Rejected(
                "Say what this key may do. Use a template " +
                $"({string.Join(", ", All.Select(template => template.Id))}) " +
                $"or a scope list ({string.Join(", ", DelunoAuthorizationPolicies.AllScopes)}). " +
                "\"all\" grants everything and has to be asked for by name.");
        }

        // One template, named. Mixing a template with loose scopes is ambiguous
        // about which wins, so it is not accepted rather than guessed at.
        if (requested.Length == 1 &&
            All.FirstOrDefault(template =>
                string.Equals(template.Id, requested[0], StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return ApiKeyScopeResolution.Granted(string.Join(", ", named.Scopes));
        }

        var templateNamedAlongsideScopes = requested
            .Where(scope => All.Any(template => string.Equals(template.Id, scope, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (templateNamedAlongsideScopes.Length > 0)
        {
            return ApiKeyScopeResolution.Rejected(
                $"Use a template on its own, not alongside scopes: {string.Join(", ", templateNamedAlongsideScopes)}.");
        }

        if (requested.Any(scope => scope is "all" or "*"))
        {
            return ApiKeyScopeResolution.Granted("all");
        }

        var unsupported = requested
            .Where(scope => !AllowedScopes.Contains(scope))
            .ToArray();
        if (unsupported.Length > 0)
        {
            return ApiKeyScopeResolution.Rejected(
                $"Unsupported scope(s): {string.Join(", ", unsupported)}. Use: {string.Join(", ", DelunoAuthorizationPolicies.AllScopes)}.");
        }

        return ApiKeyScopeResolution.Granted(string.Join(", ", requested));
    }
}

/// <summary>
/// What an API key will be allowed to do, or why it will not be created.
/// </summary>
public sealed record ApiKeyScopeResolution(string? Scopes, string? Error)
{
    public bool IsGranted => Error is null;

    public static ApiKeyScopeResolution Granted(string scopes) => new(scopes, null);

    public static ApiKeyScopeResolution Rejected(string error) => new(null, error);

    public Dictionary<string, string[]> AsValidationErrors()
        => new(StringComparer.OrdinalIgnoreCase) { ["scopes"] = [Error!] };
}
