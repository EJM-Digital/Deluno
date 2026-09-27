namespace Deluno.Connections.Contracts;

public sealed record CreateDownloadClientRequest(
    string? Name,
    string? Protocol,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    string? EndpointUrl,
    string? MoviesCategory,
    string? TvCategory,
    string? CategoryTemplate,
    int? Priority,
    bool IsEnabled,
    bool UseSsl = false,
    string? UrlBase = null,
    bool AddPaused = false,
    /// <summary>Comma-separated tag names. A client with no tags is used for any title; a tagged client is used only for a title sharing one of its tags (#808).</summary>
    string? Tags = null,
    /// <summary>Remove a download from this client once Deluno has imported it successfully (#806). Off by default.</summary>
    bool RemoveCompleted = false,
    /// <summary>Remove a download from this client once Deluno records it as failed (#806). Off by default.</summary>
    bool RemoveFailed = false);
