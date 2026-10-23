using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Deluno.Host;

/// <summary>Where the host listens: the machine itself, or every address.</summary>
public sealed record HostListening(bool EveryAddress, int Port)
{
    /// <summary>
    /// The one place the choice is made: every address only when other devices are allowed. Pure, so a test can
    /// check it without opening a socket on every address (which makes Windows ask about its firewall).
    /// </summary>
    public static HostListening For(bool allowLan, int port) => new(allowLan, port);

    public void Apply(KestrelServerOptions options)
    {
        if (EveryAddress)
        {
            options.ListenAnyIP(Port);
        }
        else
        {
            options.ListenLocalhost(Port);
        }
    }
}
