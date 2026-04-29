namespace Deluno.Platform.Contracts;

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset ExpiresUtc,
    UserItem User);
