namespace Deluno.Platform.Contracts;

public sealed record BootstrapUserRequest(
    string? Username,
    string? DisplayName,
    string? Password);
