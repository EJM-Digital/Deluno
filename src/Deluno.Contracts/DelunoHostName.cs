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
}
