using System.Text;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// #1102: some trackers (LimeTorrents) send UTF-8 release names that were already
/// re-read as Windows-1252, so "Синтел" arrives as "Ð¡Ð¸Ð½Ñ‚ÐµÐ»". This undoes that, and
/// only when the result is clearly the intended text.
/// </summary>
public static class ReleaseNameText
{
    // Windows-1252 characters for bytes 0x80-0x9F (the C1 gaps map to themselves).
    private const string Cp1252High =
        "\u20AC\u0081\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u008D\u017D\u008F" +
        "\u0090\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u009D\u017E\u0178";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string RepairMojibake(string value)
    {
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny("ÃÂÐÑ") < 0)
        {
            return value;
        }

        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c <= 0xFF)
            {
                bytes[i] = (byte)c;
                continue;
            }

            var index = Cp1252High.IndexOf(c);
            if (index < 0)
            {
                return value;
            }

            bytes[i] = (byte)(0x80 + index);
        }

        string repaired;
        try
        {
            repaired = StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return value;
        }

        return repaired.Length < value.Length && repaired.Any(c => c > 0x7F && char.IsLetter(c))
            ? repaired
            : value;
    }
}
