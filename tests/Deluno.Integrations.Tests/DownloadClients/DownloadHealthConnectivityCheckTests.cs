using System.Net;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Cleanuparr's internet check (#824): the person's own probe addresses, checked before a cleanup pass.
/// Any address answering with a success counts; none valid, none answering or a thrown error is "not reachable".
/// </summary>
public sealed class DownloadHealthConnectivityCheckTests
{
    private sealed class Handler(Func<Uri, HttpStatusCode> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "down.example")
            {
                throw new HttpRequestException("no route");
            }

            return Task.FromResult(new HttpResponseMessage(answer(request.RequestUri)));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? NameAsked { get; private set; }

        public HttpClient CreateClient(string name)
        {
            NameAsked = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    [Fact]
    public async Task The_default_check_never_skips_a_pass()
        => Assert.True(await AlwaysReachableConnectivityCheck.Instance.AnyReachableAsync("", CancellationToken.None));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url, ftp://files.example/x")]
    public async Task Nothing_valid_to_ask_is_not_reachable_and_no_request_is_made(string urls)
    {
        var handler = new Handler(_ => HttpStatusCode.OK);
        Assert.False(await new HttpDownloadHealthConnectivityCheck(new Factory(handler)).AnyReachableAsync(urls, CancellationToken.None));
        Assert.Empty(handler.Asked);
    }

    [Fact]
    public async Task The_first_address_that_answers_with_a_success_ends_the_check()
    {
        var handler = new Handler(uri => uri.Host == "bad.example" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        var factory = new Factory(handler);
        var reachable = await new HttpDownloadHealthConnectivityCheck(factory)
            .AnyReachableAsync("https://down.example/, https://bad.example/, https://good.example/, https://never.example/", CancellationToken.None);

        Assert.True(reachable);
        Assert.Equal(HttpDownloadHealthConnectivityCheck.HttpClientName, factory.NameAsked);
        Assert.Equal(["down.example", "bad.example", "good.example"], handler.Asked.Select(uri => uri.Host));
    }

    [Fact]
    public async Task When_none_answers_well_it_is_not_reachable()
    {
        var handler = new Handler(_ => HttpStatusCode.BadGateway);
        Assert.False(await new HttpDownloadHealthConnectivityCheck(new Factory(handler))
            .AnyReachableAsync("https://down.example/,https://bad.example/", CancellationToken.None));
        Assert.Equal(2, handler.Asked.Count);
    }
}
