using System.Net;
using System.Net.Sockets;
using Deluno.Contracts;
using Deluno.Infrastructure.SystemReadings;
using Deluno.Integrations.DownloadClients;
using Deluno.Tests.Recorded;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Readings;

/// <summary>
/// #1301: the readings Deluno takes by asking, each against a real answer. The System clock check reads the <c>Date</c> that TMDb really stamped on its
/// reply (tests/fixtures/recorded/tmdbapi/configuration-no-key, made by scripts/recorded-replies/docker/time-source.mjs). The connectivity probes
/// (the real <see cref="NetworkProbe"/> and the download-health check) ask a real loopback server and a real closed port, because they touch no app
/// of Deluno's: what they report is whether anything answered.
/// </summary>
public sealed class TimeAndConnectivityReadingsTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Replying(RecordedReply reply) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(reply.ToResponse());
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- System clock check, on TMDb's real Date ----

    [Fact]
    public async Task A_clock_that_agrees_with_the_Date_TMDb_stamped_is_ok_and_one_an_hour_off_is_a_problem_naming_the_direction()
    {
        var recorded = RecordedReplies.Tmdbapi("configuration-no-key").Exchanges[0].Reply;
        var stamped = DateTimeOffset.Parse(recorded.Headers["date"]);
        var handler = new Replying(recorded);

        var inStep = Assert.Single(await new SystemTimeHealthCheck(new Factory(handler), new FixedTime(stamped.AddSeconds(20))).RunAsync(CancellationToken.None));
        var behind = Assert.Single(await new SystemTimeHealthCheck(new Factory(handler), new FixedTime(stamped.AddHours(-1))).RunAsync(CancellationToken.None));

        Assert.Equal("https://api.themoviedb.org/3/configuration", handler.Asked!.ToString());
        Assert.Equal(HealthCheckStatuses.Ok, inStep.Status);
        Assert.Contains("within 20 seconds", inStep.Message, StringComparison.Ordinal);
        Assert.Equal(HealthCheckStatuses.Problem, behind.Status);
        Assert.Contains("60 minutes behind a trusted time source", behind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_with_no_Date_is_unknown_not_ok_and_not_a_problem()
    {
        var recorded = RecordedReplies.Tmdbapi("configuration-no-key").Exchanges[0].Reply with { Headers = new Dictionary<string, string> { ["content-type"] = "application/json" } };

        var outcome = Assert.Single(await new SystemTimeHealthCheck(new Factory(new Replying(recorded)), new FixedTime(DateTimeOffset.UtcNow)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Contains("could not read a trusted time source's clock", outcome.Message, StringComparison.Ordinal);
    }

    // ---- The real network probe ----

    private static async Task<(WebApplication App, string Url)> ServeAsync(Func<HttpContext, Task> answer)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context => answer(context));
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/'));
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task The_probe_times_a_request_to_a_server_that_answers_and_carries_the_time_on_its_clock()
    {
        var (app, url) = await ServeAsync(context =>
        {
            context.Response.Headers.Date = "Sun, 06 Nov 1994 08:49:37 GMT";
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        });
        await using var _ = app;
        using var http = new HttpClient();

        var timing = await new NetworkProbe(http).RequestAsync(new Uri(url), CancellationToken.None);

        Assert.NotNull(timing);
        Assert.Equal(DateTimeOffset.Parse("1994-11-06T08:49:37Z"), timing!.ServerDate);
        Assert.True(timing.Milliseconds >= 0);
    }

    [Fact]
    public async Task The_probe_reports_nothing_for_a_server_nobody_is_listening_on_and_the_checker_reads_a_clock_far_off_as_out_of_step()
    {
        using var http = new HttpClient();
        var probe = new NetworkProbe(http);
        Assert.Null(await probe.RequestAsync(new Uri($"http://127.0.0.1:{ClosedPort()}/"), CancellationToken.None));

        var (app, url) = await ServeAsync(context => { context.Response.Headers.Date = "Sun, 06 Nov 1994 08:49:37 GMT"; return Task.CompletedTask; });
        await using var _ = app;
        var checker = new NetworkChecker(probe, new FixedTime(DateTimeOffset.Parse("2026-10-28T12:00:00Z")));

        var result = await checker.RunAsync(nasHost: null, new Uri(url), CancellationToken.None);

        Assert.False(result.ClockInSync);
        Assert.NotNull(result.MetadataMs);
        Assert.Null(result.NasMs);
        Assert.Null(result.NasName);
    }

    [Fact]
    public async Task The_checker_reads_a_clock_that_matches_the_server_as_in_step()
    {
        var now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        var (app, url) = await ServeAsync(context => { context.Response.Headers.Date = now.UtcDateTime.ToString("R"); return Task.CompletedTask; });
        await using var _ = app;
        using var http = new HttpClient();

        var result = await new NetworkChecker(new NetworkProbe(http), new FixedTime(now)).RunAsync(null, new Uri(url), CancellationToken.None);

        Assert.True(result.ClockInSync);
    }

    [Fact]
    public async Task The_probe_finds_a_machine_that_is_there_by_ping_and_a_name_that_does_not_exist_as_null()
    {
        using var http = new HttpClient();
        var probe = new NetworkProbe(http);

        // Nothing serves files on 445 here, so the machine is found by the ping fallback.
        Assert.NotNull(await probe.ReachMillisecondsAsync("127.0.0.1", CancellationToken.None));
        Assert.Null(await probe.ReachMillisecondsAsync("no-such-machine.invalid", CancellationToken.None));
        Assert.NotNull(await probe.LookupMillisecondsAsync("localhost", CancellationToken.None));
        Assert.Null(await probe.LookupMillisecondsAsync("no-such-name.invalid", CancellationToken.None));
    }

    // ---- Download-health connectivity check (the person's own probe URLs) ----

    [Fact]
    public async Task The_connectivity_check_is_true_once_one_address_answers_2xx_and_false_for_a_refusal_a_closed_port_and_an_empty_list()
    {
        var (app, url) = await ServeAsync(context => { context.Response.StatusCode = context.Request.Path == "/up" ? 204 : 503; return Task.CompletedTask; });
        await using var _ = app;
        using var handler = new SocketsHttpHandler();
        var check = new HttpDownloadHealthConnectivityCheck(new Factory(handler));
        var closed = $"http://127.0.0.1:{ClosedPort()}/";

        Assert.True(await check.AnyReachableAsync($"{closed}, {url}/up", CancellationToken.None));
        Assert.False(await check.AnyReachableAsync($"{url}/down, {closed}", CancellationToken.None));
        Assert.False(await check.AnyReachableAsync("not an address, ", CancellationToken.None));
        Assert.False(await check.AnyReachableAsync(string.Empty, CancellationToken.None));
    }
}
