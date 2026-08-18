using System.Security.Cryptography;
using System.Text;

namespace Deluno.Security.Hardening.Backends;

/// <summary>
/// Cross-platform <see cref="ISecretProtector"/> using AES-256-GCM with a
/// user-managed master key. Targeted at Docker / headless Linux where no
/// system credential vault is available.
///
/// Master key sources (first non-empty wins):
/// <list type="number">
///   <item><description>
///     <c>DELUNO_MASTER_KEY</c> environment variable, base64-encoded 32
///     bytes. Recommended for container deployments where the key is
///     injected via the orchestrator (Docker secret, Kubernetes secret,
///     etc.) and never written to a volume.
///   </description></item>
///   <item><description>
///     A raw 32-byte <c>master.key</c> file at the configured path
///     (typically <c>&lt;dataRoot&gt;/secrets/master.key</c>). Created
///     on first run if neither this file nor the env var exists.
///   </description></item>
/// </list>
///
/// Output format: <c>aes:v1:&lt;base64(nonce || ciphertext || tag)&gt;</c>
/// where nonce is 12 bytes and tag is 16 bytes. The <c>purpose</c>
/// argument is fed in as AES-GCM associated data, giving per-purpose
/// domain separation.
/// </summary>
public sealed class FileBackedSecretProtector : ISecretProtector
{
    public const string Prefix = "aes:v1:";
    public const string EnvVarName = "DELUNO_MASTER_KEY";
    private const int KeySize = 32;     // 256-bit
    private const int NonceSize = 12;   // AES-GCM standard
    private const int TagSize = 16;     // AES-GCM standard

    private readonly byte[] _key;

    /// <summary>
    /// Creates a protector with an explicit 32-byte key. Used by tests
    /// and by the factory after key resolution.
    /// </summary>
    public FileBackedSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySize)
            throw new ArgumentException($"Master key must be exactly {KeySize} bytes; got {key.Length}.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <summary>
    /// Resolves the master key from the environment variable, then from
    /// the file path, creating a new random key file if neither exists.
    /// Returns both the key and a description of where it came from
    /// (for diagnostics).
    /// </summary>
    public static (byte[] Key, string Source) ResolveOrCreateKey(string masterKeyFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKeyFilePath);

        var envValue = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            byte[] envKey;
            try { envKey = Convert.FromBase64String(envValue); }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"{EnvVarName} must be base64-encoded 32 bytes. Got malformed base64.", ex);
            }
            if (envKey.Length != KeySize)
                throw new InvalidOperationException(
                    $"{EnvVarName} must decode to exactly {KeySize} bytes; got {envKey.Length}.");
            return (envKey, $"env:{EnvVarName}");
        }

        if (File.Exists(masterKeyFilePath))
        {
            var fileKey = File.ReadAllBytes(masterKeyFilePath);
            if (fileKey.Length != KeySize)
                throw new InvalidOperationException(
                    $"Master key file '{masterKeyFilePath}' must be exactly {KeySize} bytes; got {fileKey.Length}.");
            return (fileKey, $"file:{masterKeyFilePath}");
        }

        // First-run: generate a random key and persist it with restrictive
        // permissions. On Linux/macOS we set 0600 explicitly; on Windows
        // ACL inheritance from the parent secrets/ directory does the work
        // (factory creates that directory with restricted ACLs).
        var dir = Path.GetDirectoryName(masterKeyFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var newKey = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllBytes(masterKeyFilePath, newKey);
        TryRestrictPermissions(masterKeyFilePath);
        return (newKey, $"file:{masterKeyFilePath}:created");
    }

    public string Protect(string purpose, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new ArgumentException("Secret plaintext cannot be empty.", nameof(plaintext));

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var aad = PurposeAad(purpose);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, aad);
        }

        var packed = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, packed, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSize + cipher.Length, TagSize);
        return Prefix + Convert.ToBase64String(packed);
    }

    public string? Unprotect(string purpose, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return null;
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedValue;

        var packed = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (packed.Length < NonceSize + TagSize)
            throw new CryptographicException("Protected value is too short to be valid AES-GCM output.");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipher = new byte[packed.Length - NonceSize - TagSize];
        Buffer.BlockCopy(packed, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(packed, NonceSize, cipher, 0, cipher.Length);
        Buffer.BlockCopy(packed, NonceSize + cipher.Length, tag, 0, TagSize);

        var plain = new byte[cipher.Length];
        var aad = PurposeAad(purpose);
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain, aad);
        }
        return Encoding.UTF8.GetString(plain);
    }

    public bool IsProtected(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith(Prefix, StringComparison.Ordinal);

    private static byte[] PurposeAad(string purpose)
        => Encoding.UTF8.GetBytes("Deluno.Platform.Secrets:" + purpose);

    private static void TryRestrictPermissions(string path)
    {
        // Best-effort: on Unix we want 0600. On Windows the parent dir's
        // ACL controls access. Failure to set perms is logged at startup,
        // not thrown — the file exists and the app can run; the operator
        // can fix perms manually.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Swallow.
            }
        }
    }
}
