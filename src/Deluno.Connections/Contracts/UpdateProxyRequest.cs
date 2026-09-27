namespace Deluno.Connections.Contracts;

/// <summary>Patch-style request — null values mean "leave unchanged". Sent by PUT /api/indexer-proxies/{id}</summary>
public sealed record UpdateProxyRequest(
    string? Name,
    string? ProxyType,
    string? Address,
    string? Username,
    string? Password,
    bool? ClearPassword,
    bool? IsEnabled);
