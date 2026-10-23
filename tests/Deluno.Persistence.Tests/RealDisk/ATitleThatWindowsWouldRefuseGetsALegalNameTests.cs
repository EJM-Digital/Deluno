using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// A title that ends in a dot or a space, or is a Windows device name (CON, NUL, COM1...), is filed under a folder and a file name
/// Windows and a share read by Windows accept: no part of the destination ends in a dot or a space or is a bare device name (coverage
/// register, engines). The preview is asked on the real host over a real library folder. Refs #1296.
/// </summary>
public sealed class ATitleThatWindowsWouldRefuseGetsALegalNameTests
{
    private static readonly string[] Devices = ["CON", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT9"];

    [Theory]
    [InlineData("CON", 2010)]
    [InlineData("Nul", null)]
    [InlineData("COM1", 1999)]
    [InlineData("Movie.", 2010)]
    [InlineData("Movie.", null)]
    [InlineData("Movie ", 2010)]
    [InlineData("What If...", 2020)]
    public async Task The_folder_and_the_file_name_a_title_is_given_are_legal_on_Windows(string title, int? year)
    {
        var probe = new ScriptedProbe(_ => ScriptedProbe.Good());
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: RealDiskLibrary.UseProbe(probe));
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        await using var world = await RealDiskLibrary.OpenAsync(app);
        var source = world.WriteDownload("Sintel.2010.WEB.1080p.mkv");

        var response = await world.App.Client.PostAsJsonAsync("/api/filesystem/import/preview",
            new { sourcePath = source, mediaType = "movies", title, year, genres = Array.Empty<string>(), tags = Array.Empty<string>() });

        response.EnsureSuccessStatusCode();
        var destination = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("destinationPath").GetString()!;
        Assert.StartsWith(world.Root, destination, StringComparison.OrdinalIgnoreCase);
        var segments = Path.GetRelativePath(world.Root, destination).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.NotEmpty(segments);
        foreach (var segment in segments)
        {
            Assert.False(segment.EndsWith('.') || segment.EndsWith(' '), $"'{segment}' ends in a dot or a space");
            Assert.DoesNotContain(Devices, device => string.Equals(Path.GetFileNameWithoutExtension(segment), device, StringComparison.OrdinalIgnoreCase));
        }
    }
}
