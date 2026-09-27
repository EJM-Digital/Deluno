namespace Deluno.Connections.Contracts;

public sealed record CreateProxyRequest(
    string? Name,
    string? ProxyType,
    string? Address,
    string? Username,
    string? Password,
    bool IsEnabled = true);
