using System.Text.RegularExpressions;

namespace Deluno.Integrations.Processors;

/// <summary>
/// Where to ask a processor about its hand-offs, worked out from where Deluno
/// already sends them (#511).
///
/// <para>A processor that can answer - Weir - receives hand-offs at
/// <c>…/intake/webhook/{source}</c> and answers questions beside it at
/// <c>…/intake/capabilities</c> and <c>…/intake/handoffs/{source}/{id}</c>.
/// Deriving the addresses rather than asking for them keeps the connection
/// form as it is; a submission URL of any other shape is a processor that
/// cannot be asked, and is treated the way it always was.</para>
/// </summary>
public sealed partial record ProcessorStatusEndpoints(Uri Capabilities, string HandoffPrefix)
{
    public Uri Handoff(string handoffId) => new($"{HandoffPrefix}/{Uri.EscapeDataString(handoffId)}");

    /// <summary>Where to tell a processor what became of the file it handed back (#578).</summary>
    public Uri HandoffOutcome(string handoffId) => new($"{Handoff(handoffId)}/outcome");

    public static bool TryDerive(string? submissionUrl, out ProcessorStatusEndpoints endpoints)
    {
        endpoints = null!;
        if (!Uri.TryCreate(submissionUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var match = IntakeWebhook().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return false;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var intake = match.Groups["intake"].Value;
        var source = match.Groups["source"].Value;
        endpoints = new ProcessorStatusEndpoints(
            new Uri($"{origin}{intake}/capabilities"),
            $"{origin}{intake}/handoffs/{source}");
        return true;
    }

    [GeneratedRegex(@"^(?<intake>.*/intake)/webhook/(?<source>[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntakeWebhook();
}
