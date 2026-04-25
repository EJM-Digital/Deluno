namespace Deluno.Platform.Contracts;

public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string? NewPassword);
