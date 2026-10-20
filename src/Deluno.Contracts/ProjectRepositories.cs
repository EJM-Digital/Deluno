namespace Deluno.Contracts;

/// <summary>
/// The one place the two GitHub repositories are named. Every address, download, feed and user-agent string in the
/// product is built from these, so a move to another owner is a change to this file and to repository.json
/// (docs/exec-plans/active/PLAN-ORG-MOVE.md). scripts/check-repository-references.mjs fails the build when an old
/// owner name is written anywhere else in the shipped code.
/// </summary>
public static class ProjectRepositories
{
    /// <summary>Deluno's own repository, as owner/name. Moved from the personal account to the organisation.</summary>
    public const string Deluno = "EJM-Digital/Deluno";

    /// <summary>Weir's repository, as owner/name. Weir has already moved.</summary>
    public const string Weir = "EJM-Digital/Weir";

    /// <summary>
    /// Deluno's addresses that were the built-in default before a move. A settings file written by an older install
    /// can hold one of these, and it must be read as "use the built-in default", not as a choice.
    /// </summary>
    public static readonly string[] FormerDelunoWebUrls =
    [
        "https://github.com/jampat000/Deluno",
    ];

    public const string DelunoWebUrl = "https://github.com/" + Deluno;

    public const string WeirWebUrl = "https://github.com/" + Weir;

    /// <summary>The user-agent Deluno sends to the services it calls, naming where it lives.</summary>
    public const string DelunoUserAgent = $"Deluno/1.0 (+{DelunoWebUrl})";

    public static string WebUrl(string repository) => $"https://github.com/{repository}";

    /// <summary>Whether a saved update address is only a former built-in default (any case, any trailing slash or ".git").</summary>
    public static bool IsFormerDelunoDefault(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var trimmed = address.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        return FormerDelunoWebUrls.Any(former => string.Equals(former, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The one image name, for every version: the repository, lower-cased, as the release workflow names it. Earlier releases were copied here from the former package (copy-former-images.yml).</summary>
    public static string DelunoImage => "ghcr.io/" + Deluno.ToLowerInvariant();
}
