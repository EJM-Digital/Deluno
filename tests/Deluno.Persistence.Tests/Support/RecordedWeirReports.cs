using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// #1239: the reports the real Weir sends Deluno's processor events endpoint, as the recording proxy wrote them down
/// (tests/fixtures/recorded/deluno/&lt;version&gt;/events-*). A test of what Deluno does with a report Weir sends takes the
/// body from here and never builds it. Only what belongs to the test's own setup is changed: Deluno's library and hand-off
/// ids, the folder the output sits in (the golden VM's own is <see cref="VmOutputRoot"/>) and, when a test needs its hand-off
/// to carry its own release name, that name in place of the recorded one. Every member, its order and its words stay as Weir
/// sent them.
/// </summary>
internal static class RecordedWeirReports
{
    /// <summary>The folder Weir leaves finished movies in on the golden VM.</summary>
    public const string VmOutputRoot = @"C:\Weir\Ready\Movies";

    /// <summary>Exchange <paramref name="index"/> of a recorded <c>events-*</c> state.</summary>
    public static Exchange Exchange(string state, int index = 0) => RecordedReplies.Deluno(state).Exchanges[index];

    /// <summary>The release the report names, as Weir wrote it.</summary>
    public static string ReleaseOf(Exchange recorded) => JsonNode.Parse(recorded.Request.Body!)!["releaseName"]!.GetValue<string>();

    /// <summary>
    /// The recorded body ready to post. <paramref name="release"/>, when given, replaces the recorded release name wherever
    /// it appears in the body (a file keeps its <c>.mkv</c> when the recorded one had it).
    /// </summary>
    public static JsonObject Body(Exchange recorded, string libraryId, string handoffId, string outputRoot, string? release = null)
    {
        var body = JsonNode.Parse(recorded.Request.Body!)!.AsObject();
        var recordedRelease = ReleaseOf(recorded);
        var recordedStem = StripVideoExtension(recordedRelease);
        var stem = release is null ? null : StripVideoExtension(release);

        string Rebased(string path)
        {
            var relative = path.StartsWith(VmOutputRoot, StringComparison.OrdinalIgnoreCase) ? path[VmOutputRoot.Length..].TrimStart('\\') : path;
            if (stem is not null)
            {
                relative = relative.Replace(recordedStem, stem, StringComparison.Ordinal);
            }

            return Path.Combine(outputRoot, Path.Combine(relative.Split('\\')));
        }

        body["libraryId"] = libraryId;
        body["handoffId"] = handoffId;
        if (stem is not null)
        {
            body["releaseName"] = recordedRelease.Replace(recordedStem, stem, StringComparison.Ordinal);
        }

        if (body["outputPath"] is { } outputPath)
        {
            body["outputPath"] = Rebased(outputPath.GetValue<string>());
        }

        if (body["outputFiles"] is JsonArray files)
        {
            body["outputFiles"] = new JsonArray(files.Select(file => (JsonNode)JsonValue.Create(Rebased(file!.GetValue<string>()))!).ToArray());
        }

        return body;
    }

    /// <summary>The files the body names, which a test writes before it posts.</summary>
    public static string[] OutputFilesOf(JsonObject body)
        => body["outputFiles"] is JsonArray files ? files.Select(file => file!.GetValue<string>()).ToArray() : [];

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body)
        => client.PostAsync("/api/integrations/processors/events", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));

    /// <summary>The status Deluno answered Weir with when the report was recorded.</summary>
    public static HttpStatusCode RecordedStatus(Exchange recorded) => (HttpStatusCode)recorded.Reply.Status;

    private static string StripVideoExtension(string name)
        => name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
