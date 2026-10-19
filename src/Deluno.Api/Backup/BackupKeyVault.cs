using System.Security.Cryptography;
using System.Text.Json;

namespace Deluno.Api.Backup;

/// <summary>
/// The master key and the protection keys, sealed with the backup password the person chose (#1361). PBKDF2 (SHA-256) turns the
/// password into a key, AES-GCM seals the files and says, by its tag, whether the password was the right one.
/// Layout: magic, 16-byte salt, 12-byte nonce, 16-byte tag, ciphertext of a JSON list of {path, content}.
/// </summary>
public static class BackupKeyVault
{
    public const string EntryName = "deluno-keys.enc";
    public const int Iterations = 600_000;
    private static readonly byte[] Magic = "DLNK1"u8.ToArray();

    public sealed record KeyFile(string Path, byte[] Content);

    public static byte[] Seal(IReadOnlyList<KeyFile> files, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(files.Select(f => new Item(f.Path, Convert.ToBase64String(f.Content))).ToList());
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Derive(password, salt), 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }

        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    /// <summary>The files, or null when the password is not the one the keys were sealed with (or the entry is damaged).</summary>
    public static IReadOnlyList<KeyFile>? TryOpen(byte[] sealedBytes, string password)
    {
        var header = Magic.Length + 16 + 12 + 16;
        if (sealedBytes.Length < header || !sealedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return null;
        }

        var salt = sealedBytes.AsSpan(Magic.Length, 16);
        var nonce = sealedBytes.AsSpan(Magic.Length + 16, 12);
        var tag = sealedBytes.AsSpan(Magic.Length + 28, 16);
        var cipher = sealedBytes.AsSpan(header);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Derive(password, salt), 16);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var items = JsonSerializer.Deserialize<List<Item>>(plain) ?? [];
        return items.Select(i => new KeyFile(i.Path, Convert.FromBase64String(i.Content))).ToArray();
    }

    private static byte[] Derive(string password, ReadOnlySpan<byte> salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private sealed record Item(string Path, string Content);
}
