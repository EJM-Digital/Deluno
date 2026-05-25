namespace Deluno.Platform.Security.Hardening;

/// <summary>
/// Read-and-migrate helper for callers that persist protected values
/// (e.g. settings repositories). Wraps a <see cref="CompositeSecretProtector"/>
/// to surface the case where a value was protected with a legacy backend
/// and is now eligible to be re-protected with the active backend.
///
/// Use pattern:
/// <code>
///   var outcome = migrator.Unprotect("nzb-server-password", row.PasswordProtected);
///   var plaintext = outcome.Plaintext;
///   if (outcome.MigratedValue is { } newCipher)
///       row.PasswordProtected = newCipher; // persist on next save
/// </code>
///
/// This keeps the migration opportunistic — values get rewritten the
/// next time they're read AND the caller chooses to save them. No
/// background sweep needed; no risk of partial-write corruption.
/// </summary>
public sealed class SecretValueMigrator
{
    private readonly CompositeSecretProtector _composite;

    public SecretValueMigrator(ISecretProtector protector)
    {
        // The composite is what knows about both active + legacy readers.
        // Migration is meaningless against a plain (non-composite) protector,
        // so reject the case loudly rather than silently no-op.
        _composite = protector as CompositeSecretProtector
            ?? throw new ArgumentException(
                $"SecretValueMigrator requires the registered ISecretProtector to be a {nameof(CompositeSecretProtector)}. " +
                $"Got {protector.GetType().FullName}.",
                nameof(protector));
    }

    public MigrationOutcome Unprotect(string purpose, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return new MigrationOutcome(Plaintext: null, MigratedValue: null);

        var active = _composite.Active;
        if (active.IsProtected(protectedValue))
        {
            // Already on the active backend — nothing to migrate.
            return new MigrationOutcome(
                Plaintext: active.Unprotect(purpose, protectedValue),
                MigratedValue: null);
        }

        // Walk legacy readers; if one matches, decrypt with it and re-encrypt
        // with the active backend.
        foreach (var legacy in _composite.LegacyReaders)
        {
            if (legacy.IsProtected(protectedValue))
            {
                var plain = legacy.Unprotect(purpose, protectedValue)
                    ?? throw new InvalidOperationException(
                        "Legacy reader returned null plaintext for a value it claims to recognize.");
                var rewritten = active.Protect(purpose, plain);
                return new MigrationOutcome(Plaintext: plain, MigratedValue: rewritten);
            }
        }

        // Unknown prefix — pass through unchanged (matches the existing
        // first-run upgrade convention).
        return new MigrationOutcome(Plaintext: protectedValue, MigratedValue: null);
    }
}

/// <summary>
/// Result of a read-and-migrate operation.
/// </summary>
/// <param name="Plaintext">
/// The decrypted plaintext, or null if the input was null/empty.
/// </param>
/// <param name="MigratedValue">
/// If non-null, the same plaintext re-protected with the active backend.
/// Callers should persist this in place of the original to complete the
/// migration. Null means no migration needed (already on active backend,
/// or input was plaintext).
/// </param>
public sealed record MigrationOutcome(string? Plaintext, string? MigratedValue);
