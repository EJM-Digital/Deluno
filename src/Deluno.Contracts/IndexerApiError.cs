using System.Xml.Linq;

namespace Deluno.Contracts;

/// <summary>
/// The <c>&lt;error code="…" description="…"/&gt;</c> element a Torznab or Newznab server (Jackett, NZBHydra2, a
/// Newznab indexer) answers with, always with HTTP 200 (#1311). One reader for the test button, caps, search and RSS,
/// so a wrong key reads the same everywhere: as a credential failure, never as an empty result.
/// </summary>
public sealed record IndexerApiError(int? Code, string Description)
{
    /// <summary>The Newznab codes for a bad login: 100 incorrect credentials, 101 account suspended, 102 insufficient privileges.</summary>
    public bool IsCredentialFailure => Code is >= 100 and <= 102;

    /// <summary>Reads the error out of a response document, or null when it is not an error reply.</summary>
    public static IndexerApiError? TryRead(XDocument? document)
    {
        var root = document?.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "error", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var description = root.Attribute("description")?.Value;
        return new IndexerApiError(
            int.TryParse(root.Attribute("code")?.Value, out var code) ? code : null,
            string.IsNullOrWhiteSpace(description) ? "the indexer returned an error rather than results." : description.Trim());
    }

    /// <summary>Reads the error out of a response body, or null when the body is not an error reply.</summary>
    public static IndexerApiError? TryRead(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.Contains("<error", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return TryRead(XDocument.Parse(body));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>What Indexer History and the indexer's health say, in plain words.</summary>
    public string PlainMessage(string indexerName)
        => IsCredentialFailure
            ? $"{indexerName} refused the API key or sign-in ({Short}). Check the key in Indexers and test it again."
            : $"{indexerName} answered with an error instead of results ({Short}).";

    // NZBHydra2 answers a bad request with a whole framework message; keep the screen to the first sentence.
    private string Short => Description.Length <= 160 ? Description : Description[..160].TrimEnd() + "...";
}
