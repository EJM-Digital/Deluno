namespace Deluno.Platform.Contracts;

public sealed record CreateApiKeyRequest(
    string? Name,
    string? Scopes);

public sealed record CreatedApiKeyResponse(
    ApiKeyItem Item,
    string ApiKey);
