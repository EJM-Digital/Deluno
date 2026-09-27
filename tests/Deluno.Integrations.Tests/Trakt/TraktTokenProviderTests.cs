using Deluno.Connections.Data;
using Deluno.Integrations.Trakt;
using Moq;

namespace Deluno.Integrations.Tests.Trakt;

/// <summary>
/// #846: the one thing every Trakt-reading caller asks for — a token that
/// works right now — refreshing silently while it can, and surfacing a
/// reconnect need rather than failing quietly once it cannot.
/// </summary>
public sealed class TraktTokenProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");

    [Fact]
    public async Task Returns_null_when_no_client_credentials_are_configured()
    {
        var repository = new Mock<ITraktConnectionRepository>();
        repository.Setup(r => r.GetClientCredentialsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((TraktClientCredentials?)null);
        var provider = new TraktTokenProvider(repository.Object, Mock.Of<ITraktClient>(), TimeProvider.System);

        var context = await provider.GetAuthContextAsync(CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task Returns_null_when_never_connected()
    {
        var repository = new Mock<ITraktConnectionRepository>();
        repository.Setup(r => r.GetClientCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktClientCredentials("client-id", "client-secret"));
        repository.Setup(r => r.GetTokensAsync(It.IsAny<CancellationToken>())).ReturnsAsync((TraktTokenPair?)null);
        var provider = new TraktTokenProvider(repository.Object, Mock.Of<ITraktClient>(), TimeProvider.System);

        var context = await provider.GetAuthContextAsync(CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task Uses_the_stored_access_token_without_refreshing_while_it_still_has_life_left()
    {
        var repository = new Mock<ITraktConnectionRepository>();
        repository.Setup(r => r.GetClientCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktClientCredentials("client-id", "client-secret"));
        repository.Setup(r => r.GetTokensAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktTokenPair("access-1", "refresh-1", Now.AddDays(30)));
        var client = new Mock<ITraktClient>();
        var provider = new TraktTokenProvider(repository.Object, client.Object, new FixedTimeProvider(Now));

        var context = await provider.GetAuthContextAsync(CancellationToken.None);

        Assert.Equal("client-id", context!.ClientId);
        Assert.Equal("access-1", context.AccessToken);
        client.Verify(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refreshes_and_saves_a_token_that_is_about_to_expire()
    {
        var repository = new Mock<ITraktConnectionRepository>();
        repository.Setup(r => r.GetClientCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktClientCredentials("client-id", "client-secret"));
        repository.Setup(r => r.GetTokensAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktTokenPair("access-1", "refresh-1", Now.AddSeconds(30)));
        var client = new Mock<ITraktClient>();
        client.Setup(c => c.RefreshAsync("client-id", "client-secret", "refresh-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktTokens("access-2", "refresh-2", 7776000));
        var provider = new TraktTokenProvider(repository.Object, client.Object, new FixedTimeProvider(Now));

        var context = await provider.GetAuthContextAsync(CancellationToken.None);

        Assert.Equal("access-2", context!.AccessToken);
        repository.Verify(r => r.SaveTokensAsync("access-2", "refresh-2", Now.AddSeconds(7776000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Marks_needing_reconnect_and_returns_null_when_the_refresh_token_has_stopped_working()
    {
        var repository = new Mock<ITraktConnectionRepository>();
        repository.Setup(r => r.GetClientCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktClientCredentials("client-id", "client-secret"));
        repository.Setup(r => r.GetTokensAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktTokenPair("access-1", "refresh-1", Now.AddSeconds(-10)));
        var client = new Mock<ITraktClient>();
        client.Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TraktTokens?)null);
        var provider = new TraktTokenProvider(repository.Object, client.Object, new FixedTimeProvider(Now));

        var context = await provider.GetAuthContextAsync(CancellationToken.None);

        Assert.Null(context);
        repository.Verify(r => r.MarkNeedsReconnectAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
