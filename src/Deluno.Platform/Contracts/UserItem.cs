namespace Deluno.Platform.Contracts;

public sealed record UserItem(
    string Id,
    string Username,
    string DisplayName,
    string AvatarInitials,
    DateTimeOffset CreatedUtc);
