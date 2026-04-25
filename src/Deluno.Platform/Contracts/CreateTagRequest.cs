namespace Deluno.Platform.Contracts;

public sealed record CreateTagRequest(
    string Name,
    string? Color,
    string? Description);
