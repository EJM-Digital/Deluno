namespace Deluno.Platform.Security.Hardening;

/// <summary>
/// Wraps an "active" <see cref="ISecretProtector"/> for new writes plus a
/// list of legacy readers, so values protected with an older backend
/// (e.g. the original DataProtection-based <see cref="DataProtectionSecretProtector"/>)
/// can still be unprotected and migrated.
///
/// <see cref="Protect"/> always delegates to the active protector.
/// <see cref="Unprotect"/> first asks the active protector; if the value
/// has an unrecognized prefix it walks legacy readers and asks the first
/// one whose <see cref="ISecretProtector.IsProtected"/> matches. This
/// keeps the migration path simple — old values still work, new writes
/// use the new backend, and the migrator (see <c>SecretValueMigrator</c>)
/// can opportunistically re-protect on read.
/// </summary>
public sealed class CompositeSecretProtector : ISecretProtector
{
    private readonly ISecretProtector _active;
    private readonly IReadOnlyList<ISecretProtector> _legacyReaders;

    public CompositeSecretProtector(ISecretProtector active, IEnumerable<ISecretProtector>? legacyReaders = null)
    {
        _active = active ?? throw new ArgumentNullException(nameof(active));
        _legacyReaders = legacyReaders?.ToArray() ?? Array.Empty<ISecretProtector>();
    }

    public ISecretProtector Active => _active;
    public IReadOnlyList<ISecretProtector> LegacyReaders => _legacyReaders;

    public string Protect(string purpose, string plaintext)
        => _active.Protect(purpose, plaintext);

    public string? Unprotect(string purpose, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return null;

        if (_active.IsProtected(protectedValue))
            return _active.Unprotect(purpose, protectedValue);

        foreach (var legacy in _legacyReaders)
        {
            if (legacy.IsProtected(protectedValue))
                return legacy.Unprotect(purpose, protectedValue);
        }

        // Per the existing DataProtectionSecretProtector convention,
        // values with no recognized prefix are returned as-is (treated as
        // plaintext stored before any protection was wired in). Don't
        // throw — that would break first-run upgrade paths.
        return protectedValue;
    }

    public bool IsProtected(string? value)
    {
        if (_active.IsProtected(value)) return true;
        foreach (var legacy in _legacyReaders)
            if (legacy.IsProtected(value)) return true;
        return false;
    }
}
