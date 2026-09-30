using System.Security.Cryptography;

namespace Deluno.Security;

public interface ISecretProtector
{
    string Protect(string purpose, string plaintext);

    string? Unprotect(string purpose, string? protectedValue);

    bool IsProtected(string? value);

    /// <summary>
    /// The one safe way to read a saved secret for a list or a screen (#1094).
    /// A value protected on another computer, account or key cannot be read
    /// here; that must never fail the list, so it comes back as "no value" with
    /// <see cref="SecretReadResult.Unreadable"/> set so the screen can ask for
    /// the key again. Every reader of a protected secret uses this instead of
    /// calling <see cref="Unprotect"/> directly.
    /// </summary>
    SecretReadResult TryUnprotect(string purpose, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return SecretReadResult.None;
        }

        try
        {
            var value = Unprotect(purpose, protectedValue);
            return string.IsNullOrEmpty(value) ? SecretReadResult.None : new SecretReadResult(value, false);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return SecretReadResult.CannotRead;
        }
    }
}

/// <summary>Outcome of <see cref="ISecretProtector.TryUnprotect"/>: no value, a value, or saved elsewhere and unreadable here.</summary>
public readonly record struct SecretReadResult(string? Value, bool Unreadable)
{
    public static SecretReadResult None => new(null, false);

    public static SecretReadResult CannotRead => new(null, true);
}

/// <summary>Plain-words reasons shown on a connection's screen and in Health when a saved secret cannot be read here (#1094).</summary>
public static class SecretUnreadable
{
    public static string Message(string what = "API key")
        => $"The saved {what} was stored on another computer and can't be read here. Enter it again.";
}
