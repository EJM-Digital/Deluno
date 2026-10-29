using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>What the library routes say when a folder is a file, is missing, or a processor wait is out of range.</summary>
public sealed class LibraryFolderValidationApiTests
{
    private static async Task<string[]> ErrorsOfAsync(HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errors").GetProperty(field).EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    [Fact]
    public async Task A_library_cannot_point_at_a_file_or_a_missing_folder_and_each_refusal_says_which()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "films")).FullName;
        var file = Path.Combine(app.DataRoot, "notes.txt");
        await File.WriteAllTextAsync(file, "not a folder");

        var onFile = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = file });
        var onMissing = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = Path.Combine(app.DataRoot, "nope") });
        var downloadsOnFile = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root, downloadsPath = file });

        Assert.Equal(HttpStatusCode.BadRequest, onFile.StatusCode);
        var fileMessage = Assert.Single(await ErrorsOfAsync(onFile, "rootPath"));
        Assert.Equal($"{file} is a file, not a folder. Choose the folder this library lives in.", fileMessage);
        Assert.Equal(HttpStatusCode.BadRequest, onMissing.StatusCode);
        Assert.Contains("does not exist", Assert.Single(await ErrorsOfAsync(onMissing, "rootPath")));
        Assert.Equal(HttpStatusCode.BadRequest, downloadsOnFile.StatusCode);
        Assert.Contains("is a file, not a folder. Choose the folder downloads arrive in.", Assert.Single(await ErrorsOfAsync(downloadsOnFile, "downloadsPath")));

        // Nothing was created by any of the three.
        var libraries = await app.Client.GetFromJsonAsync<JsonElement>("/api/libraries");
        Assert.Equal(0, libraries.GetArrayLength());
    }

    [Fact]
    public async Task The_workflow_needs_an_existing_output_folder_for_refine_before_import_and_a_wait_of_24_hours_to_7_days()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "films")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(app.DataRoot, "cleaned")).FullName;
        var created = await app.Client.PostAsJsonAsync("/api/libraries", new { name = "Films", mediaType = "movies", rootPath = root });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Task<HttpResponseMessage> Put(object body) => app.Client.PutAsJsonAsync($"/api/libraries/{id}/workflow", body);

        var noFolder = await Put(new { importWorkflow = "refine-before-import" });
        Assert.Equal(HttpStatusCode.BadRequest, noFolder.StatusCode);
        Assert.Contains("Choose where the processor will write cleaned files before Deluno imports them.", await ErrorsOfAsync(noFolder, "processorOutputPath"));

        var missing = await Put(new { importWorkflow = "refine-before-import", processorOutputPath = Path.Combine(app.DataRoot, "gone") });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("does not exist", Assert.Single(await ErrorsOfAsync(missing, "processorOutputPath")));

        var tooShort = await Put(new { importWorkflow = "standard", processorTimeoutMinutes = 60 });
        var tooLong = await Put(new { importWorkflow = "standard", processorTimeoutMinutes = 20000 });
        foreach (var refused in new[] { tooShort, tooLong })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("Choose between 24 hours and 7 days of no progress before Deluno stops waiting for the processor.", Assert.Single(await ErrorsOfAsync(refused, "processorTimeoutMinutes")));
        }

        var saved = await Put(new { importWorkflow = "refine-before-import", processorOutputPath = output, processorTimeoutMinutes = 2880 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var item = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2880, item.GetProperty("processorTimeoutMinutes").GetInt32());
        Assert.Equal(output, item.GetProperty("processorOutputPath").GetString());
    }
}
