using System.Net;
using System.Net.Sockets;
using Deluno.Infrastructure.Http;

namespace Deluno.Platform.Tests.Resilience;

/// <summary>
/// #1522: a name with several addresses is raced, IPv6 and IPv4 staggered, and the first connection wins.
/// These drive the race itself with stand-in attempts, so every outcome is reached without a network.
/// </summary>
public sealed class LoopbackFirstConnectionRaceTests
{
    private static readonly IPAddress V6 = IPAddress.IPv6Loopback;
    private static readonly IPAddress V4 = IPAddress.Loopback;

    private static Socket NewSocket(IPAddress address) => new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

    private static bool IsDisposed(Socket socket)
    {
        try
        {
            _ = socket.Available;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [Fact]
    public async Task When_every_address_fails_the_last_failure_is_thrown()
    {
        var thrown = await Assert.ThrowsAsync<SocketException>(() => LoopbackFirstConnection.RaceAsync(
            [V6, V4],
            TimeSpan.FromMilliseconds(5),
            (address, _) => Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused)),
            failedFamilyHost: null,
            CancellationToken.None));

        Assert.Equal(SocketError.ConnectionRefused, thrown.SocketErrorCode);
    }

    [Fact]
    public async Task When_there_is_nothing_to_try_the_host_is_reported_unreachable()
    {
        var thrown = await Assert.ThrowsAsync<SocketException>(() => LoopbackFirstConnection.RaceAsync(
            [],
            TimeSpan.FromMilliseconds(5),
            (address, _) => Task.FromResult(NewSocket(address)),
            failedFamilyHost: null,
            CancellationToken.None));

        Assert.Equal(SocketError.HostUnreachable, thrown.SocketErrorCode);
    }

    [Fact]
    public async Task A_cancelled_race_throws_the_cancellation_and_not_a_connection_error()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        var race = LoopbackFirstConnection.RaceAsync(
            [V6],
            TimeSpan.FromSeconds(30),
            async (address, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token); // reason: the stand-in attempt never answers; the race cancels it
                return NewSocket(address);
            },
            failedFamilyHost: null,
            cts.Token);

        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => race);
    }

    [Fact]
    public async Task A_slow_first_address_does_not_hold_up_the_next_one_which_wins_and_the_slow_one_is_closed_when_it_connects_late()
    {
        var slow = new TaskCompletionSource<Socket>();
        var late = NewSocket(V6);
        var host = $"slow-{Guid.NewGuid():N}.example.test";

        var winner = await LoopbackFirstConnection.RaceAsync(
            [V6, V4],
            TimeSpan.FromMilliseconds(10),
            (address, _) => address.AddressFamily == AddressFamily.InterNetworkV6 ? slow.Task : Task.FromResult(NewSocket(address)),
            host,
            CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetwork, winner.AddressFamily);
        Assert.Equal(AddressFamily.InterNetworkV6, LoopbackFirstConnection.OrderAddresses(host, [V4, V6])[0].AddressFamily);

        slow.SetResult(late);
        for (var i = 0; i < 100 && !IsDisposed(late); i++)
        {
            await Task.Delay(50); // reason: polls, at most five seconds, until the late connection is closed
        }

        Assert.True(IsDisposed(late), "a connection that arrives after the race is won is closed, not leaked");
        winner.Dispose();
    }

    [Fact]
    public async Task A_late_failure_after_the_race_is_won_is_swallowed()
    {
        var slow = new TaskCompletionSource<Socket>();

        var winner = await LoopbackFirstConnection.RaceAsync(
            [V6, V4],
            TimeSpan.FromMilliseconds(10),
            (address, _) => address.AddressFamily == AddressFamily.InterNetworkV6 ? slow.Task : Task.FromResult(NewSocket(address)),
            failedFamilyHost: null,
            CancellationToken.None);

        slow.SetException(new SocketException((int)SocketError.ConnectionRefused));
        await Task.Delay(50); // reason: gives the late failure time to be observed and swallowed; there is nothing to poll

        Assert.Equal(AddressFamily.InterNetwork, winner.AddressFamily);
        winner.Dispose();
    }

    [Fact]
    public async Task When_IPv6_fails_and_IPv4_connects_that_host_is_tried_IPv4_first_next_time()
    {
        var host = $"v6-down-{Guid.NewGuid():N}.example.test";
        Assert.Equal(AddressFamily.InterNetworkV6, LoopbackFirstConnection.OrderAddresses(host, [V4, V6])[0].AddressFamily);

        var winner = await LoopbackFirstConnection.RaceAsync(
            [V6, V4],
            TimeSpan.FromSeconds(30),
            (address, _) => address.AddressFamily == AddressFamily.InterNetworkV6
                ? Task.FromException<Socket>(new SocketException((int)SocketError.HostUnreachable))
                : Task.FromResult(NewSocket(address)),
            host,
            CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetwork, winner.AddressFamily);
        Assert.Equal(AddressFamily.InterNetwork, LoopbackFirstConnection.OrderAddresses(host, [V4, V6])[0].AddressFamily);
        winner.Dispose();
    }

    [Fact]
    public async Task When_IPv6_wins_after_a_failure_the_host_is_not_rested_and_a_failure_with_no_host_rests_nothing()
    {
        var host = $"v6-ok-{Guid.NewGuid():N}.example.test";

        var winner = await LoopbackFirstConnection.RaceAsync(
            [V4, V6],
            TimeSpan.FromSeconds(30),
            (address, _) => address.AddressFamily == AddressFamily.InterNetwork
                ? Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused))
                : Task.FromResult(NewSocket(address)),
            host,
            CancellationToken.None);

        Assert.Equal(AddressFamily.InterNetworkV6, winner.AddressFamily);
        Assert.Equal(AddressFamily.InterNetworkV6, LoopbackFirstConnection.OrderAddresses(host, [V4, V6])[0].AddressFamily);
        winner.Dispose();

        var anonymous = await LoopbackFirstConnection.RaceAsync(
            [V6, V4],
            TimeSpan.FromSeconds(30),
            (address, _) => address.AddressFamily == AddressFamily.InterNetworkV6
                ? Task.FromException<Socket>(new SocketException((int)SocketError.HostUnreachable))
                : Task.FromResult(NewSocket(address)),
            failedFamilyHost: null,
            CancellationToken.None);
        Assert.Equal(AddressFamily.InterNetwork, anonymous.AddressFamily);
        anonymous.Dispose();
    }

    [Fact]
    public async Task When_two_connections_finish_together_one_wins_and_the_other_is_closed()
    {
        var first = new TaskCompletionSource<Socket>();
        var firstSocket = NewSocket(V6);
        var secondSocket = NewSocket(V4);

        var winner = await LoopbackFirstConnection.RaceAsync(
            [V6, V4],
            TimeSpan.FromMilliseconds(10),
            (address, _) =>
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    return first.Task;
                }

                // Both finish in the same instant.
                first.SetResult(firstSocket);
                return Task.FromResult(secondSocket);
            },
            failedFamilyHost: null,
            CancellationToken.None);

        var loser = ReferenceEquals(winner, firstSocket) ? secondSocket : firstSocket;
        Assert.True(ReferenceEquals(winner, firstSocket) || ReferenceEquals(winner, secondSocket));
        Assert.False(IsDisposed(winner));
        Assert.True(IsDisposed(loser));
        winner.Dispose();
    }
}
