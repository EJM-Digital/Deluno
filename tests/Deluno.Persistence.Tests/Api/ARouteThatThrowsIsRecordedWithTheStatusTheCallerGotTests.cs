using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deluno.Api.Monitoring;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1400: when an /api route throws, the shared pipeline's request record says what the caller was answered (500, or the plain
/// 503 for a damaged state file), not 200.
/// </summary>
public sealed class ARouteThatThrowsIsRecordedWithTheStatusTheCallerGotTests
{
    private sealed class RecordingTracker : IApiLatencyTracker
    {
        public List<(string Path, int Status)> Records { get; } = [];
        public void Record(string path, double durationMs, int statusCode) { lock (Records) Records.Add((path, statusCode)); }
        public ApiLatencySnapshot GetSnapshot(TimeSpan? window = null) => throw new NotSupportedException();
        public ApiAnswerTimes GetAnswerTimes(TimeSpan window) => throw new NotSupportedException();
        public ApiDayTotals GetDayTotals() => throw new NotSupportedException();
    }

    private static async Task<(SharedPipelineHost Host, RecordingTracker Tracker)> StartSignedInAsync()
    {
        var tracker = new RecordingTracker();
        var host = await SharedPipelineHost.StartAsync(configureServices: services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IApiLatencyTracker)).ToArray()) services.Remove(d);
            services.AddSingleton<IApiLatencyTracker>(tracker);
        });
        var created = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        created.EnsureSuccessStatusCode();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        return (host, tracker);
    }

    [Fact]
    public async Task A_route_that_throws_is_recorded_as_the_500_the_caller_got()
    {
        var (host, tracker) = await StartSignedInAsync();
        await using var _ = host;
        // A bin state that is a folder, not a file: reading it throws something that is not a damaged-file message.
        var state = Path.Combine(host.DataRoot, "recycle-bin", "state.json");
        Directory.CreateDirectory(state);

        var response = await host.Client.GetAsync("/api/recycle-bin");

        var recorded = tracker.Records.Last(r => r.Path == "/api/recycle-bin");
        Assert.True((int)response.StatusCode >= 500, $"the route did not fail: {(int)response.StatusCode}");
        Assert.Equal((int)response.StatusCode, recorded.Status);
    }

    [Fact]
    public async Task A_damaged_state_file_is_recorded_as_the_503_the_caller_got()
    {
        var (host, tracker) = await StartSignedInAsync();
        await using var _ = host;
        var state = Path.Combine(host.DataRoot, "recycle-bin", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        await File.WriteAllTextAsync(state, "{\"settings\":{\"retentionDays\":30},\"items\":[{\"id\":\"ab");

        var response = await host.Client.GetAsync("/api/recycle-bin");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(503, tracker.Records.Last(r => r.Path == "/api/recycle-bin").Status);
    }

    [Fact]
    public async Task A_route_that_answers_normally_is_still_recorded_as_it_answered()
    {
        var (host, tracker) = await StartSignedInAsync();
        await using var _ = host;

        var response = await host.Client.GetAsync("/api/recycle-bin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, tracker.Records.Last(r => r.Path == "/api/recycle-bin").Status);
    }
}
