namespace Deluno.Platform.Contracts;

public sealed record UpdateTagRequest(
    string Name,
    string? Color,
    string? Description);
