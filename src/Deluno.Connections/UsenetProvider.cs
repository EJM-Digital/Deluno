using System.Globalization;

namespace Deluno.Connections.Usenet;

/// <summary>
/// The Usenet provider (news server) a person pays for, entered once in Deluno and written to every
/// Usenet client it manages (#1142). SABnzbd and NZBGet cannot download anything without one.
/// </summary>
public sealed record UsenetProviderRequest(
    string Host,
    int Port,
    bool UseSsl,
    string? Username,
    string? Password,
    int Connections);

/// <summary>What happened for one Usenet client when the provider was written.</summary>
public sealed record UsenetProviderClientResult(string ClientId, string ClientName, bool Saved, string Message);

/// <summary>Whether one Usenet client has a working news server.</summary>
public sealed record UsenetProviderClientStatus(string ClientId, string ClientName, bool HasProvider, bool Known);

public static class UsenetProviderText
{
    /// <summary>The wording a Usenet client shows in place of "Healthy" while it has no news server.</summary>
    public const string NoProvider = "No Usenet provider set up. Add yours in Deluno, under Find & Download, Download clients, and it is written to every Usenet client.";

    /// <summary>The server NZBGet ships in its stock config. It is a placeholder, not a provider.</summary>
    public const string NzbGetPlaceholderHost = "my.newsserver.com";

    /// <summary>The name Deluno gives the server it writes, so a second save updates it instead of adding another.</summary>
    public const string ServerName = "Deluno Usenet provider";

    public static bool IsPlaceholder(string? host)
        => string.IsNullOrWhiteSpace(host)
           || string.Equals(host.Trim(), NzbGetPlaceholderHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>The problem with a request, in plain words, or null when it can be written.</summary>
    public static string? Validate(UsenetProviderRequest request)
    {
        if (IsPlaceholder(request.Host))
        {
            return "Enter the news server address your Usenet provider gave you, for example news.example.com.";
        }

        if (request.Host.Contains("://", StringComparison.Ordinal) || request.Host.Contains('/', StringComparison.Ordinal))
        {
            return "Enter only the server address, without http:// or a path.";
        }

        if (request.Port is < 1 or > 65535)
        {
            return "The port must be a number from 1 to 65535. Providers use 563 for a secure connection and 119 for a plain one.";
        }

        return request.Connections is < 1 or > 100
            ? "Connections must be a number from 1 to 100. Your provider's plan says how many it allows."
            : null;
    }
}

/// <summary>
/// NZBGet keeps its news servers as numbered options (Server1.Host, Server1.Port ...) in one flat list,
/// and its saveconfig call replaces the whole list. This works out the list to send back.
/// </summary>
public static class NzbGetServerPlan
{
    private static readonly System.Text.RegularExpressions.Regex ServerOption =
        new(@"^Server(\d+)\.(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The values NZBGet's <c>config</c> call adds to the settings it holds (#1257): where it runs and which version it is.
    /// They are not options of nzbget.conf, and a <c>saveconfig</c> that carries them writes four lines NZBGet rejects
    /// ("Invalid option"), after which it pauses all downloads over errors in configuration.
    /// </summary>
    public static readonly IReadOnlyList<string> RuntimeOnlyOptions = ["ConfigFile", "AppBin", "AppDir", "Version"];

    /// <summary>The options without NZBGet's runtime-only values. A file the bug already damaged is repaired by this, not preserved.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> WithoutRuntimeOnly(IReadOnlyList<KeyValuePair<string, string>> options)
        => options.Where(option => !RuntimeOnlyOptions.Contains(option.Key, StringComparer.OrdinalIgnoreCase)).ToArray();

    /// <summary>True when at least one server is switched on and has a real address.</summary>
    public static bool HasWorkingServer(IReadOnlyList<KeyValuePair<string, string>> options)
        => Servers(options).Any(server =>
            string.Equals(server.Value.GetValueOrDefault("Active"), "yes", StringComparison.OrdinalIgnoreCase)
            && !UsenetProviderText.IsPlaceholder(server.Value.GetValueOrDefault("Host")));

    /// <summary>True when a server for <paramref name="host"/> is switched on (the provider that was just written has taken effect).</summary>
    public static bool HasWorkingServer(IReadOnlyList<KeyValuePair<string, string>> options, string host)
        => Servers(options).Any(server =>
            string.Equals(server.Value.GetValueOrDefault("Active")?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            && string.Equals(server.Value.GetValueOrDefault("Host")?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The full option list with the provider written into one server slot and every placeholder server switched off.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Apply(IReadOnlyList<KeyValuePair<string, string>> options, UsenetProviderRequest request)
    {
        options = WithoutRuntimeOnly(options);
        var servers = Servers(options);
        var slot = servers
                .Where(server => string.Equals(server.Value.GetValueOrDefault("Name"), UsenetProviderText.ServerName, StringComparison.OrdinalIgnoreCase))
                .Select(server => (int?)server.Key)
                .FirstOrDefault()
            ?? servers
                .Where(server => UsenetProviderText.IsPlaceholder(server.Value.GetValueOrDefault("Host")))
                .Select(server => (int?)server.Key)
                .FirstOrDefault()
            ?? (servers.Count == 0 ? 1 : servers.Keys.Max() + 1);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"Server{slot}.Active"] = "yes",
            [$"Server{slot}.Name"] = UsenetProviderText.ServerName,
            [$"Server{slot}.Level"] = "0",
            [$"Server{slot}.Host"] = request.Host.Trim(),
            [$"Server{slot}.Port"] = request.Port.ToString(CultureInfo.InvariantCulture),
            [$"Server{slot}.Username"] = request.Username?.Trim() ?? string.Empty,
            [$"Server{slot}.Password"] = request.Password ?? string.Empty,
            [$"Server{slot}.Encryption"] = request.UseSsl ? "yes" : "no",
            [$"Server{slot}.Connections"] = request.Connections.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var other in servers.Where(server => server.Key != slot && UsenetProviderText.IsPlaceholder(server.Value.GetValueOrDefault("Host"))))
        {
            values[$"Server{other.Key}.Active"] = "no";
        }

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<KeyValuePair<string, string>>();
        foreach (var option in options)
        {
            if (values.TryGetValue(option.Key, out var replaced))
            {
                result.Add(new(option.Key, replaced));
                written.Add(option.Key);
            }
            else
            {
                result.Add(option);
            }
        }

        result.AddRange(values.Where(pair => !written.Contains(pair.Key)).Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));
        return result;
    }

    private static SortedDictionary<int, Dictionary<string, string>> Servers(IReadOnlyList<KeyValuePair<string, string>> options)
    {
        var servers = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var option in options)
        {
            var match = ServerOption.Match(option.Key);
            if (!match.Success)
            {
                continue;
            }

            var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!servers.TryGetValue(number, out var server))
            {
                servers[number] = server = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            server[match.Groups[2].Value] = option.Value;
        }

        return servers;
    }
}

/// <summary>
/// The one reading of "does this Usenet client have a news server?" that the connection test, the
/// Download clients list and telemetry all share (#1142), so they cannot disagree.
/// </summary>
public static class UsenetProviderCheck
{
    /// <summary>
    /// SABnzbd's <c>get_config&amp;section=servers</c> answer: true when a server is switched on with a real address,
    /// false when there is none (SABnzbd with no server answers <c>{"config":{}}</c>, so a missing or empty list means no provider),
    /// null when the answer is not SABnzbd's config at all and so tells us nothing.
    /// </summary>
    public static bool? SabnzbdHasProvider(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("config", out var config)
            || config.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (!config.TryGetProperty("servers", out var servers) || servers.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }

        return servers.EnumerateArray().Any(SabnzbdServerIsWorking);
    }

    public static bool SabnzbdServerIsWorking(System.Text.Json.JsonElement server)
    {
        static string Text(System.Text.Json.JsonElement element, string name)
            => element.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;

        var enabled = Text(server, "enable");
        return (enabled is "1" or "true" or "True") && !UsenetProviderText.IsPlaceholder(Text(server, "host"));
    }

    /// <summary>NZBGet's <c>config</c> answer ({"result":[{"Name":..,"Value":..}]}): same meaning as <see cref="SabnzbdHasProvider"/>.</summary>
    public static bool? NzbGetHasProvider(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        var options = result.EnumerateArray()
            .Where(option => option.ValueKind == System.Text.Json.JsonValueKind.Object && option.TryGetProperty("Name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(option => new KeyValuePair<string, string>(
                option.GetProperty("Name").GetString()!,
                option.TryGetProperty("Value", out var value) ? value.ToString() : string.Empty))
            .ToArray();
        return NzbGetServerPlan.HasWorkingServer(options);
    }
}
