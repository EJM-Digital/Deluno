namespace Deluno.Security.Contracts;

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresUtc,
    UserItem User);
