namespace Deluno.Contracts;

/// <summary>
/// #1075: the one name this installation goes by. It is always the hostname of
/// the machine Deluno runs on (in Docker, the container's hostname, which is
/// what Environment.MachineName reports there too). There is no setting for it.
/// </summary>
public static class DelunoHostName
{
    public static string Current
    {
        get
        {
            try
            {
                var name = Environment.MachineName;
                return string.IsNullOrWhiteSpace(name) ? "Deluno" : name;
            }
            catch
            {
                return "Deluno";
            }
        }
    }

    /// <summary>
    /// #1101: in Docker the hostname defaults to the container's random id (12
    /// lowercase hex characters), which tells nobody which machine it is. True
    /// only inside a container and only for that exact shape, so System can say
    /// how to give the container a name.
    /// </summary>
    public static bool LooksLikeContainerId(string? name, bool runningInContainer)
    {
        if (!runningInContainer || name is null || name.Length != 12)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static bool CurrentLooksLikeContainerId =>
        LooksLikeContainerId(
            Current,
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase));
}
