namespace Deluno.Tray;

/// <summary>
/// The one place a command line is turned into text for a log (#1523). The value of any argument that carries a secret
/// (the service account's password, a token, an API key) is written as <c>***</c>, in both <c>--password value</c> and
/// <c>--password=value</c> forms, so no log ever holds it.
/// </summary>
internal static class CommandLineRedaction
{
    private static readonly string[] SecretFlags =
    [
        "--password", "--passwd", "--pass", "--pwd", "--token", "--access-token", "--api-key", "--apikey", "--secret", "--key"
    ];

    internal const string Mask = "***";

    private static bool IsSecretFlag(string name)
        => SecretFlags.Any(flag => string.Equals(flag, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The arguments as one log-safe line.</summary>
    internal static string ForLog(IEnumerable<string> args)
    {
        var parts = new List<string>();
        var maskNext = false;
        foreach (var arg in args)
        {
            if (maskNext)
            {
                maskNext = false;
                parts.Add(Mask);
                continue;
            }

            var equals = arg.IndexOf('=');
            if (equals > 0 && IsSecretFlag(arg[..equals]))
            {
                parts.Add(arg[..equals] + "=" + Mask);
            }
            else
            {
                parts.Add(arg);
                maskNext = IsSecretFlag(arg);
            }
        }

        return string.Join(' ', parts);
    }
}
