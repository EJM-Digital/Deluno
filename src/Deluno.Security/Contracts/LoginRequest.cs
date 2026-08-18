namespace Deluno.Security.Contracts;

public sealed record LoginRequest(
    string? Username,
    string? Password);
