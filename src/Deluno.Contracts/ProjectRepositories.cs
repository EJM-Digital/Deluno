namespace Deluno.Contracts;

/// <summary>
/// The one place the two GitHub repositories are named. Every address, download, feed and user-agent string in the
/// product is built from these, so a move to another owner is a change to this file and to repository.json
/// (docs/exec-plans/active/PLAN-ORG-MOVE.md). scripts/check-repository-references.mjs fails the build when an old
/// owner name is written anywhere else in the shipped code.
/// </summary>
public static class ProjectRepositories
{
    /// <summary>Deluno's own repository, as owner/name. Flipped after the repository transfer, not before.</summary>
    public const string Deluno = "jampat000/Deluno";

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

    /// <summary>Where the images of releases up to <see cref="LastFormerImageVersion"/> live. ghcr does not forward a moved name.</summary>
    public const string FormerDelunoImage = "ghcr.io/jampat000/deluno";

    /// <summary>The last release published under <see cref="FormerDelunoImage"/>.</summary>
    public const string LastFormerImageVersion = "1.0.0-rc.30.34";

    /// <summary>The image name for releases published from now on: the repository, lower-cased, as the release workflow names it.</summary>
    public static string DelunoImage => "ghcr.io/" + Deluno.ToLowerInvariant();

    /// <summary>
    /// The image name a given release was published under: the former name up to <see cref="LastFormerImageVersion"/>, the
    /// current one after. Anything that pulls an earlier release's image (an upgrade test, a rollback check) names it here.
    /// </summary>
    public static string DelunoImageForVersion(string version)
        => CompareVersions(version, LastFormerImageVersion) <= 0 ? FormerDelunoImage : DelunoImage;

    /// <summary>Orders versions like 1.0.0-rc.30.34 numerically, part by part; a release without a suffix is newer than its candidates.</summary>
    public static int CompareVersions(string left, string right)
    {
        static (int[] Core, int[]? Pre) Parse(string value)
        {
            var text = value.Trim().TrimStart('v', 'V');
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text[..plus];
            var dash = text.IndexOf('-');
            var core = dash < 0 ? text : text[..dash];
            var pre = dash < 0 ? null : text[(dash + 1)..];
            static int[] Numbers(string part) => part.Split('.', '-').Select(piece => int.TryParse(piece, out var n) ? n : -1).ToArray();
            return (Numbers(core), pre is null ? null : Numbers(pre));
        }

        static int Parts(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var x = i < a.Length ? a[i] : 0;
                var y = i < b.Length ? b[i] : 0;
                if (x != y) return x.CompareTo(y);
            }

            return 0;
        }

        var (leftCore, leftPre) = Parse(left);
        var (rightCore, rightPre) = Parse(right);
        var core = Parts(leftCore, rightCore);
        if (core != 0) return core;
        if (leftPre is null && rightPre is null) return 0;
        if (leftPre is null) return 1;
        if (rightPre is null) return -1;
        return Parts(leftPre, rightPre);
    }
}
