using System.Text.Json;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1329: the manifest is the first thing Weir reads of Deluno, and it offers a redownload or a reject only when the manifest names
/// the capability. These tests hold Deluno's recorded reply (<c>reply-manifest</c>, made from the real server code by
/// <see cref="DelunoRepliesToWeirTests"/>) to the documented list of capabilities and to every member of a library entry, so a
/// capability or a member that leaves the reply fails here before Weir meets the change.
/// </summary>
public sealed class TheManifestWeirReadsAgainstItsRecordingTests
{
    private static JsonElement RecordedManifest()
    {
        var recording = RecordedReplies.Load("deluno", DelunoRepliesToWeirTests.RecordedVersion, "reply-manifest");
        var exchange = Assert.Single(recording.Exchanges);
        Assert.Equal(200, exchange.Reply.Status);
        Assert.Equal("/api/integrations/external/manifest", exchange.Request.Path);
        return JsonDocument.Parse(exchange.Reply.Body!).RootElement.Clone();
    }

    [Fact]
    public void The_recorded_manifest_names_every_capability_of_the_documented_list()
    {
        var manifest = RecordedManifest();
        Assert.Equal("Deluno", manifest.GetProperty("product").GetString());
        var capabilities = manifest.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        string[] documented =
        [
            "movies", "tv", "indexers", "download-clients", "library-routing", "destination-rules", "metadata", "media-probing",
            "external-redownload", "pre-import-processing", "processor-reject-regrab", "activity-feed", "signalr"
        ];
        Assert.Empty(documented.Except(capabilities));
        Assert.Equal(capabilities.Length, capabilities.Distinct().Count());
        Assert.Equal(Deluno.Platform.Contracts.ProcessorCapabilities.RejectRegrab, "processor-reject-regrab");
    }

    [Fact]
    public void The_recorded_manifest_lists_every_library_with_every_member_Weir_reads()
    {
        var manifest = RecordedManifest();
        var libraries = manifest.GetProperty("libraries").EnumerateArray().ToArray();
        Assert.Equal(2, libraries.Length);

        string[] members =
        [
            "id", "name", "mediaType", "rootPath", "downloadsPath", "qualityProfileName", "importWorkflow", "processorName",
            "processorOutputPath", "processorTimeoutMinutes", "processorFailureMode", "missingSearchEnabled", "upgradeSearchEnabled",
            "maxItemsPerRun", "automationStatus", "cleanupMode", "removeEmptySourceFolders"
        ];
        foreach (var library in libraries)
        {
            Assert.Equal(members, library.EnumerateObject().Select(member => member.Name).ToArray());
        }

        var films = libraries.Single(library => library.GetProperty("name").GetString() == "films");
        Assert.Equal("movies", films.GetProperty("mediaType").GetString());
        Assert.Equal("<data-root>/films", films.GetProperty("rootPath").GetString());
        Assert.Equal("<data-root>/films-downloads", films.GetProperty("downloadsPath").GetString());
        Assert.Equal("refine-before-import", films.GetProperty("importWorkflow").GetString());
        Assert.Equal("Weir", films.GetProperty("processorName").GetString());
        Assert.Equal("<data-root>/films-ready", films.GetProperty("processorOutputPath").GetString());

        var shows = libraries.Single(library => library.GetProperty("name").GetString() == "shows");
        Assert.Equal("tv", shows.GetProperty("mediaType").GetString());
        Assert.Equal("standard", shows.GetProperty("importWorkflow").GetString());
        Assert.Equal(JsonValueKind.Null, shows.GetProperty("processorName").ValueKind);
    }
}
