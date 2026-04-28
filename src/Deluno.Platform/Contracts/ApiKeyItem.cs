namespace Deluno.Platform.Contracts;

public sealed record ApiKeyItem(
    string Id,
    string Name,
    string Prefix,
    string Scopes,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
