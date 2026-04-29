using System.Text.Json.Serialization;

namespace Deluno.Platform.Contracts;

public sealed record UserItem(
    string Id,
    string Username,
    string DisplayName,
    string AvatarInitials,
    [property: JsonIgnore]
    string SecurityStamp,
    DateTimeOffset CreatedUtc);
