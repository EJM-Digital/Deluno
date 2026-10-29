using System.Security.Cryptography;

namespace Deluno.Security.Recovery;

/// <summary>
/// The look of a recovery code (#1392): twelve letters and digits in three groups, with the look-alikes (0, O, 1, I, L)
/// left out so a code read off paper is typed right. What is typed is compared without spaces, dashes or case.
/// </summary>
public static class RecoveryCodeFormat
{
    public const int CodesPerSet = 10;

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string NewCode()
    {
        var letters = new char[12];
        for (var i = 0; i < letters.Length; i++)
        {
            letters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var text = new string(letters);
        return $"{text[..4]}-{text[4..8]}-{text[8..]}";
    }

    public static string Normalize(string? typed)
        => new((typed ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
