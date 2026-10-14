using Deluno.Infrastructure.SystemReadings;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>#1098: the network checks are real requests; what cannot answer is null, and the clock is judged against the metadata service's.</summary>
public sealed class NetworkCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Metadata = new("https://metadata.example.test");

    private static NetworkChecker Checker(FakeProbe probe, TimeSpan? limit = null)
        => new(probe, new FakeTimeProvider(Now), limit ?? TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Every_check_reports_what_it_measured()
    {
        var probe = new FakeProbe { Lookup = 12.5, Reach = 3.2, Http = new HttpTiming(88.4, Now) };

        var result = await Checker(probe).RunAsync("nas", Metadata, CancellationToken.None);

        Assert.Equal(12.5, result.DnsLookupMs);
        Assert.Equal(3.2, result.NasMs);
        Assert.Equal("nas", result.NasName);
        Assert.Equal(88.4, result.MetadataMs);
        Assert.True(result.ClockInSync);
        Assert.Equal(NetworkChecker.WellKnownName, probe.LookedUp);
        Assert.Equal("nas", probe.Reached);
        Assert.Equal(Metadata, probe.Requested);
    }

    [Fact]
    public async Task Without_a_network_library_there_is_no_nas_check()
    {
        var probe = new FakeProbe { Lookup = 5, Http = new HttpTiming(10, Now) };

        var result = await Checker(probe).RunAsync(null, Metadata, CancellationToken.None);

        Assert.Null(result.NasMs);
        Assert.Null(result.NasName);
        Assert.Null(probe.Reached);
    }

    [Fact]
    public async Task A_service_that_does_not_answer_is_null_not_slow()
    {
        var probe = new FakeProbe { Lookup = null, Reach = null, Http = null };

        var result = await Checker(probe).RunAsync("nas", Metadata, CancellationToken.None);

        Assert.Null(result.DnsLookupMs);
        Assert.Null(result.NasMs);
        Assert.Equal("nas", result.NasName);
        Assert.Null(result.MetadataMs);
        Assert.Null(result.ClockInSync);
    }

    [Fact]
    public async Task A_check_that_hangs_is_given_up_on_without_holding_up_the_others()
    {
        var probe = new FakeProbe { Lookup = 7, Http = new HttpTiming(20, Now), ReachHangs = true };

        var result = await Checker(probe, TimeSpan.FromMilliseconds(150)).RunAsync("nas", Metadata, CancellationToken.None);

        Assert.Null(result.NasMs);
        Assert.Equal(7, result.DnsLookupMs);
        Assert.Equal(20, result.MetadataMs);
    }

    [Fact]
    public async Task Cancelling_the_whole_run_is_not_swallowed()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var probe = new FakeProbe { ReachHangs = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(probe).RunAsync("nas", Metadata, cancel.Token));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(119, true)]
    [InlineData(-119, true)]
    [InlineData(121, false)]
    [InlineData(-300, false)]
    public void The_clock_is_in_step_within_two_minutes_either_way(int secondsAhead, bool expected)
    {
        var server = Now;
        var local = Now.AddSeconds(secondsAhead);

        Assert.Equal(expected, NetworkChecker.ClockInSync(local, server));
    }

    [Fact]
    public void A_service_that_sends_no_time_gives_no_clock_verdict()
    {
        Assert.Null(NetworkChecker.ClockInSync(Now, null));
    }

    [Theory]
    [InlineData(@"\\nas\media\Movies", "nas")]
    [InlineData(@"\\192.168.1.5\share", "192.168.1.5")]
    [InlineData("//nas/media/Movies", "nas")]
    [InlineData(@"D:\Movies", null)]
    [InlineData("/mnt/media", null)]
    [InlineData(null, null)]
    public void The_nas_is_the_server_part_of_a_network_path(string? path, string? expected)
    {
        Assert.Equal(expected, NetworkChecker.NetworkHostOf(path));
    }

    private sealed class FakeProbe : INetworkProbe
    {
        public double? Lookup { get; set; }
        public double? Reach { get; set; }
        public HttpTiming? Http { get; set; }
        public bool ReachHangs { get; set; }
        public string? LookedUp { get; private set; }
        public string? Reached { get; private set; }
        public Uri? Requested { get; private set; }

        public Task<double?> LookupMillisecondsAsync(string name, CancellationToken cancellationToken)
        {
            LookedUp = name;
            return Task.FromResult(Lookup);
        }

        public async Task<double?> ReachMillisecondsAsync(string host, CancellationToken cancellationToken)
        {
            Reached = host;
            if (ReachHangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // reason: a probe that never answers; the test cancels it
            }

            return Reach;
        }

        public Task<HttpTiming?> RequestAsync(Uri url, CancellationToken cancellationToken)
        {
            Requested = url;
            return Task.FromResult(Http);
        }
    }
}
