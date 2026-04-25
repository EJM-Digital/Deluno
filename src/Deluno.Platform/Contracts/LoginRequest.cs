namespace Deluno.Platform.Contracts;

public sealed record LoginRequest(
    string? Username,
    string? Password);
