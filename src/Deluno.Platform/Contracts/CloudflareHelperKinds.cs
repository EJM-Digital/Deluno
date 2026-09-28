namespace Deluno.Platform.Contracts;

/// <summary>
/// Which Cloudflare helper Deluno is talking to (#1029). FlareSolverr and
/// Byparr answer the same <c>/v1</c> shape, but Byparr's source says it only
/// supports GET: it accepts <c>sessions.create</c>/<c>sessions.destroy</c>
/// and <c>request.post</c>'s <c>postData</c> and never errors, but does
/// nothing with any of them, and there is no per-request <c>proxy</c> field
/// either. Left on "auto", Deluno probes the helper's own address to tell
/// them apart; a person who knows which one they run can say so instead.
/// </summary>
public static class CloudflareHelperKinds
{
    /// <summary>Detect the helper by probing its address; the default, and every existing install's value.</summary>
    public const string Auto = "auto";

    public const string FlareSolverr = "flaresolverr";

    public const string Byparr = "byparr";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "flaresolverr" or "flare-solverr" or "flare_solverr" => FlareSolverr,
            "byparr" => Byparr,
            _ => Auto
        };
    }
}
