namespace Deluno.Security.Contracts;

/// <summary>
/// A documented least-privilege scope set for a supported external caller.
/// The catalogue is intentionally server-owned so recipes and the API page do
/// not drift away from the scopes enforced by <see cref="DelunoScopeRequirement"/>.
/// </summary>
public sealed record ApiKeyScopeTemplate(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Capabilities);
