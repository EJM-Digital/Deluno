using System.Net;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Two guards of the shared request pipeline (DelunoRequestPipeline), over a real listener with a real data folder:
/// every answer carries the security headers (with HSTS everywhere except development), and while the databases are still
/// migrating every request except the health probes gets a plain 503.
/// </summary>
public sealed class SecurityHeadersAndStartupGateOnTheRealPipelineTests
{
    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

    [Fact]
    public async Task Every_answer_carries_nosniff_frame_deny_and_the_referrer_policy_and_a_year_of_HSTS_outside_development()
    {
        await using var host = await SharedPipelineHost.StartAsync(environment: "Production");

        using var response = await host.Client.GetAsync("/health");

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task The_headers_are_also_on_a_refused_request_and_a_missing_page()
    {
        await using var host = await SharedPipelineHost.StartAsync(environment: "Production");

        using var refused = await host.Client.GetAsync("/api/movies");
        using var missing = await host.Client.GetAsync("/api/no-such-route");

        foreach (var response in new[] { refused, missing })
        {
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        }
    }

    [Fact]
    public async Task In_development_there_is_no_HSTS_but_the_other_headers_remain()
    {
        await using var host = await SharedPipelineHost.StartAsync(environment: "Development");

        using var response = await host.Client.GetAsync("/health");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"), "HSTS would pin a local http address in the browser");
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    /// <summary>A gate whose databases never finish: every wait ends the way the real gate's 30 s wait ends.</summary>
    private sealed class NeverReadyGate : IDelunoStartupGate
    {
        public bool IsReady => false;
        public IReadOnlyList<string> PendingDatabases => ["platform"];
        public IReadOnlyDictionary<string, string> FailedDatabases => new Dictionary<string, string>();
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromException(new TimeoutException());
        public void MarkReady(string databaseName) { }
        public void MarkFailed(string databaseName, Exception exception) { }
    }

    [Theory]
    [InlineData("/api/movies")]
    [InlineData("/api/system/status")]
    [InlineData("/")]
    public async Task While_the_databases_are_migrating_a_page_or_api_call_gets_503_with_a_plain_error(string path)
    {
        await using var host = await SharedPipelineHost.StartAsync(configureServices: services => services.Replace(ServiceDescriptor.Singleton<IDelunoStartupGate>(new NeverReadyGate())));

        using var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"error\":\"Deluno is still completing database migrations. Try again shortly.\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/health/live")]
    [InlineData("/api/health/ready")]
    public async Task The_health_probes_skip_the_gate_so_a_slow_start_can_be_watched(string path)
    {
        await using var host = await SharedPipelineHost.StartAsync(configureServices: services => services.Replace(ServiceDescriptor.Singleton<IDelunoStartupGate>(new NeverReadyGate())));

        using var response = await host.Client.GetAsync(path);

        // The ready probe may itself say 503 (not ready yet), but in its own words, not the gate's.
        if (path != "/api/health/ready") Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("still completing database migrations", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Once_the_databases_are_ready_the_same_calls_are_answered_normally()
    {
        await using var host = await SharedPipelineHost.StartAsync();

        using var response = await host.Client.GetAsync("/api/movies");

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
