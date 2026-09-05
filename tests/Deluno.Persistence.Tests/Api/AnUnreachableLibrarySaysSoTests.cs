using Deluno.Persistence.Tests.Support;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A library Deluno cannot reach is not silence.
///
/// <para>Before this, an unmounted drive produced no signal at all. The
/// reconciliation scan would have called every title in it critically missing —
/// see the guard in <c>FilesystemReconciliationService</c> — and with that
/// guard in place it instead produces nothing, which is correct and also
/// useless on its own. Every title in the library is unverifiable and every
/// import into it will fail identically, and the only symptom was that Deluno
/// went quiet about a whole shelf.</para>
///
/// <para>DESIGN-007 decision 12. Driven through the real route table, because
/// an alert nothing can fetch is the same as no alert.</para>
/// </summary>
public sealed class AnUnreachableLibrarySaysSoTests
{
    [Fact]
    public async Task A_library_whose_folder_has_gone_is_raised_as_a_health_alert()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var root = Directory.CreateTempSubdirectory("deluno-unreachable-").FullName;

        var created = await app.Client.PostAsJsonAsync("/api/libraries/", NewLibrary("Films", root));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        // Nothing is wrong yet.
        Assert.DoesNotContain("library.unreachable", await AlertsAsync(app), StringComparison.Ordinal);

        // The drive goes away.
        Directory.Delete(root, recursive: true);

        var alerts = await AlertsAsync(app);
        Assert.Contains("library.unreachable", alerts, StringComparison.Ordinal);
        // Named, because "a library is unreachable" is not actionable and
        // "Films is not at D:\Films" is.
        Assert.Contains("Films", alerts, StringComparison.Ordinal);
        Assert.Contains(root.Replace("\\", "\\\\"), alerts, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> AlertsAsync(ApplicationTestHost app)
    {
        var response = await app.Client.GetAsync("/api/monitoring/alerts");
        Assert.True(response.IsSuccessStatusCode, $"GET alerts returned {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static object NewLibrary(string name, string rootPath) => new
    {
        name,
        mediaType = "movies",
        purpose = "collection",
        rootPath,
        downloadsPath = (string?)null,
        qualityProfileId = (string?)null,
        importWorkflow = "copy",
        processorName = (string?)null,
        processorOutputPath = (string?)null,
        processorTimeoutMinutes = (int?)null,
        processorFailureMode = (string?)null,
        autoSearchEnabled = false,
        missingSearchEnabled = false,
        upgradeSearchEnabled = false,
        searchIntervalHours = (int?)null,
        retryDelayHours = (int?)null,
        maxItemsPerRun = (int?)null
    };
}
