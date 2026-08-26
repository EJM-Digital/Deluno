using System.Reflection;
using System.Text.Json;
using Deluno.Filesystem;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// The folder check is the first thing a new user presses, and its two halves
/// were never compared. The server answered with <c>canRead</c>,
/// <c>canWriteToParent</c> and <c>fullPath</c>; the UI read <c>readable</c>,
/// <c>writable</c>, <c>normalizedPath</c> and <c>message</c>. Every one of those
/// arrived undefined, so Readable and Writable were unlit for every path ever
/// checked and a healthy folder showed a warning with nothing written under it.
///
/// Nothing caught it because no test looked at the wire and no browser test ran
/// against a real filesystem. This pins the names the UI actually reads.
/// </summary>
public sealed class PathDiagnosticContractTests
{
    /// <summary>Exactly what `path-input.tsx` reads off the response.</summary>
    private static readonly string[] FieldsTheUiReads =
    [
        "path", "normalizedPath", "exists", "isDirectory", "readable", "writable",
        "isUncPath", "isLikelyDockerPath", "message", "warnings"
    ];

    private static JsonElement Diagnose(string path)
    {
        var method = typeof(FilesystemEndpointRouteBuilderExtensions)
            .GetMethod("BuildPathDiagnostic", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [path])!;
        return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public void The_response_carries_every_field_the_folder_check_renders()
    {
        var payload = Diagnose(Path.GetTempPath());

        foreach (var field in FieldsTheUiReads)
        {
            Assert.True(payload.TryGetProperty(field, out _), $"The folder check reads '{field}', and the server does not send it.");
        }
    }

    [Fact]
    public void A_readable_writable_folder_reports_itself_as_one()
    {
        var directory = Directory.CreateTempSubdirectory("deluno-path-diagnostic");
        try
        {
            var payload = Diagnose(directory.FullName);

            Assert.True(payload.GetProperty("exists").GetBoolean());
            Assert.True(payload.GetProperty("isDirectory").GetBoolean());
            Assert.True(payload.GetProperty("readable").GetBoolean());
            Assert.True(payload.GetProperty("writable").GetBoolean());
            Assert.Equal("Deluno can read and write this folder.", payload.GetProperty("message").GetString());
            Assert.Empty(payload.GetProperty("warnings").EnumerateArray());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_is_not_there_says_so_plainly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"deluno-not-here-{Guid.NewGuid():N}");

        var payload = Diagnose(missing);

        Assert.False(payload.GetProperty("exists").GetBoolean());
        Assert.Equal("That folder does not exist yet.", payload.GetProperty("message").GetString());
        // The parent is there, so nothing exotic is in play and the reader must
        // not be sent to look at Docker volumes and mapped drives.
        Assert.Empty(payload.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void A_path_whose_parent_is_missing_too_keeps_the_visibility_hint()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"deluno-gone-{Guid.NewGuid():N}", "deeper", "still");

        var payload = Diagnose(missing);

        Assert.False(payload.GetProperty("exists").GetBoolean());
        Assert.Contains(
            payload.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()),
            warning => warning!.Contains("nor the one above it is visible", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_is_not_mistaken_for_a_folder()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deluno-file-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "deluno");
        try
        {
            var payload = Diagnose(file);

            Assert.True(payload.GetProperty("exists").GetBoolean());
            Assert.False(payload.GetProperty("isDirectory").GetBoolean());
            Assert.Equal("That is a file, not a folder.", payload.GetProperty("message").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
