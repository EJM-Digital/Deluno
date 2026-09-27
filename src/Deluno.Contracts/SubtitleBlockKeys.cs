namespace Deluno.Contracts;

/// <summary>
/// One subtitle, identified the same way whether Deluno is about to download
/// it or is being asked to forget one it already wrote.
///
/// <para>Mirrors <see cref="BlockedReleaseKeys"/>: a release's identity there
/// is its hash or its name; a subtitle's here is the provider that offered it
/// and the token that provider uses to mean this exact file — not merely this
/// language from this provider, which two different uploads both are.</para>
/// </summary>
public static class SubtitleBlockKeys
{
    public static string For(string providerKey, string downloadToken)
        => $"{providerKey.Trim().ToLowerInvariant()}|{downloadToken.Trim().ToLowerInvariant()}";
}
