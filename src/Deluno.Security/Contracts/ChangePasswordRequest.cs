namespace Deluno.Security.Contracts;

public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string? NewPassword);
