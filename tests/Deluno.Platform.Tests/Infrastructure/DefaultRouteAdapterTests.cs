using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Platform.Tests.Infrastructure;

public sealed class DefaultRouteAdapterTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.20");

    private static DefaultRouteAdapter.AdapterFacts Adapter(string name, bool up, string address, long bits = 1_000_000_000)
        => new(name, up, [IPAddress.Parse(address)], bits, 100, 200);

    [Fact]
    public void The_adapter_that_is_up_and_holds_the_route_address_is_reported_with_its_counters()
    {
        var reading = DefaultRouteAdapter.Read(
            () => Local,
            () => [Adapter("lo", true, "127.0.0.1"), Adapter("eth0", true, "192.168.1.20")]);

        Assert.Equal(new NetworkAdapterReading("eth0", "192.168.1.20", 1000, 100, 200), reading);
    }

    [Fact]
    public void An_adapter_that_is_down_is_never_the_one_even_with_the_right_address()
        => Assert.Null(DefaultRouteAdapter.Read(() => Local, () => [Adapter("eth0", false, "192.168.1.20")]));

    [Fact]
    public void No_adapter_with_the_address_means_no_reading()
        => Assert.Null(DefaultRouteAdapter.Read(() => Local, () => [Adapter("eth0", true, "10.0.0.5")]));

    [Fact]
    public void An_adapter_that_does_not_know_its_speed_is_reported_without_one()
        => Assert.Null(DefaultRouteAdapter.Read(() => Local, () => [Adapter("wlan0", true, "192.168.1.20", bits: -1)])!.LinkMbps);

    [Fact]
    public void No_route_means_no_reading_and_the_adapters_are_not_even_asked()
        => Assert.Null(DefaultRouteAdapter.Read(() => null, () => throw new InvalidOperationException("not asked")));

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(NetworkInformationException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    public void A_computer_that_will_not_say_gives_no_reading(Type failure)
    {
        Assert.Null(DefaultRouteAdapter.Read(() => throw (Exception)Activator.CreateInstance(failure)!, () => []));
        Assert.Null(DefaultRouteAdapter.Read(() => Local, () => throw (Exception)Activator.CreateInstance(failure)!));
    }

    [Fact]
    public void A_failure_of_another_kind_is_not_hidden()
        => Assert.Throws<FormatException>(() => DefaultRouteAdapter.Read(() => throw new FormatException(), () => []));

    [Fact]
    public void This_computer_can_be_asked_without_failing()
    {
        _ = DefaultRouteAdapter.Read();
        var adapters = DefaultRouteAdapter.AdaptersOfThisComputer().ToList();

        Assert.All(adapters.Where(adapter => !adapter.IsUp), adapter => Assert.Empty(adapter.Addresses));
    }
}
