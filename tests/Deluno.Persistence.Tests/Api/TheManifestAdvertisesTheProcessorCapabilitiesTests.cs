using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using System.Net.Http.Json;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A processor reads the external manifest to learn what Deluno will do for it: the capability names it finds there are
/// the ones docs/external-integration-api.md promises.
/// </summary>
public sealed class TheManifestAdvertisesTheProcessorCapabilitiesTests
{
    [Fact]
    public async Task The_manifest_lists_pre_import_processing_and_reject_regrab_and_a_tool_asking_for_a_redownload()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var manifest = await app.Client.GetFromJsonAsync<JsonElement>("/api/integrations/external/manifest");

        var capabilities = manifest.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("pre-import-processing", capabilities);
        Assert.Contains(ProcessorCapabilities.RejectRegrab, capabilities);
        Assert.Contains("external-redownload", capabilities);
        Assert.Equal("processor-reject-regrab", ProcessorCapabilities.RejectRegrab);
        Assert.Equal(capabilities.Length, capabilities.Distinct().Count());
    }
}
