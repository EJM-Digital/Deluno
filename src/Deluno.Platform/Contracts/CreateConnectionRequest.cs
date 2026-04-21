namespace Deluno.Platform.Contracts;

public sealed record CreateConnectionRequest(
    string? Name,
    string? ConnectionKind,
    string? Role,
    string? EndpointUrl,
    bool IsEnabled);
