namespace Deluno.Platform.Contracts;

public sealed record LoginResponse(
    string AccessToken,
    UserItem User);
