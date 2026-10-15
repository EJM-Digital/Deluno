using Deluno.Contracts;

namespace Deluno.Persistence.Tests;

/// <summary>
/// A web address must mean the same on Windows and Linux: on Linux
/// "/x.torrent" parses as the file address file:///x.torrent.
/// </summary>
public sealed class WebAddressTests
{
    [Theory]
    [InlineData("http://host/x")]
    [InlineData("https://host:8080/x.torrent?a=b")]
    [InlineData("  http://192.168.1.10:8080/  ")]
    [InlineData("http://[::1]:9117/")]
    public void An_absolute_http_or_https_address_with_a_host_is_a_web_address(string value)
    {
        Assert.True(WebAddress.TryParse(value, out var uri));
        Assert.False(string.IsNullOrEmpty(uri.Host));
        Assert.True(WebAddress.IsValid(value));
    }

    [Theory]
    [InlineData("/x.torrent")]
    [InlineData("x.torrent")]
    [InlineData(@"C:\x")]
    [InlineData("file:///x")]
    [InlineData("ftp://host/x")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("http://")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_not_a_web_address(string? value)
    {
        Assert.False(WebAddress.TryParse(value, out _));
        Assert.False(WebAddress.IsValid(value));
    }

    [Theory]
    [InlineData("http://host/x", true)]
    [InlineData("https://host/x.torrent", true)]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("MAGNET:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("/x.torrent", false)]
    [InlineData(@"C:\x", false)]
    [InlineData("file:///x", false)]
    [InlineData("magnet:", false)]
    [InlineData(null, false)]
    public void A_download_link_is_a_web_address_or_a_magnet_link(string? value, bool expected)
        => Assert.Equal(expected, WebAddress.IsDownloadLink(value));
}
