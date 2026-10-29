using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Tests.Recorded;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1301: finding the newest Windows installer of Deluge in its own download directory (<see cref="DirectoryListingLatestRelease"/>), run against
/// the directory listing, checksum file and 404 that ftp.osuosl.org really answered (recorded from the internet). A loopback server replays them at the
/// address the spec names, because the spec carries its address.
/// </summary>
public sealed class DirectoryListingLatestReleaseOnRecordedRepliesTests : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly string address;
    private readonly List<string> asked = [];
    private Func<string, RecordedReply?> answer = _ => null;

    public DirectoryListingLatestReleaseOnRecordedRepliesTests()
    {
        var port = FreePort();
        address = $"http://127.0.0.1:{port}/pub/deluge/windows/";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { return; }

                lock (asked) asked.Add(context.Request.Url!.AbsolutePath);
                var reply = answer(context.Request.Url!.AbsolutePath);
                context.Response.StatusCode = reply?.Status ?? 404;
                var body = Encoding.UTF8.GetBytes(reply?.Body ?? string.Empty);
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
    }

    public void Dispose() => listener.Close();

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private BundledComponentSpec Deluge() => BundledComponents.Load().Single(spec => spec.Kind == "deluge") with { DirectoryListingUrl = address };

    private static string NewestInstaller(string listing)
        => Regex.Matches(listing, @"href=""(deluge-[\d.]+-win64-setup\.exe)""").Select(match => match.Groups[1].Value).OrderBy(name => Version.Parse(Regex.Match(name, @"[\d.]+\d").Value)).Last();

    [Fact]
    public async Task The_newest_installer_in_the_real_listing_is_chosen_with_the_hash_from_its_checksum_file()
    {
        var listing = RecordedReplies.Delugedownloads("windows-listing").Exchanges[0].Reply;
        var checksum = RecordedReplies.Delugedownloads("installer-checksum").Exchanges[0].Reply;
        var newest = NewestInstaller(listing.Body!);
        answer = path => path.EndsWith("/windows/", StringComparison.Ordinal) ? listing : path.EndsWith(newest + ".sha256", StringComparison.Ordinal) ? checksum : null;

        var resolved = await DirectoryListingLatestRelease.ResolveAsync(Deluge(), CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(address + newest, resolved!.AssetUrl);
        Assert.Equal(GithubLatestRelease.ParseChecksum(checksum.Body!, newest), resolved.Sha256);
        Assert.False(string.IsNullOrEmpty(resolved.Sha256));
    }

    [Fact]
    public async Task An_installer_whose_checksum_file_the_directory_does_not_hold_is_still_found_with_no_hash()
    {
        var listing = RecordedReplies.Delugedownloads("windows-listing").Exchanges[0].Reply;
        var notFound = RecordedReplies.Delugedownloads("checksum-not-found").Exchanges[0].Reply;
        Assert.Equal(404, notFound.Status);
        answer = path => path.EndsWith("/windows/", StringComparison.Ordinal) ? listing : notFound;

        var resolved = await DirectoryListingLatestRelease.ResolveAsync(Deluge(), CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.EndsWith(NewestInstaller(listing.Body!), resolved!.AssetUrl, StringComparison.Ordinal);
        Assert.Null(resolved.Sha256);
    }

    [Fact]
    public async Task A_listing_with_no_matching_file_a_directory_that_is_not_there_and_a_spec_with_no_address_find_nothing()
    {
        var listing = RecordedReplies.Delugedownloads("windows-listing").Exchanges[0].Reply;
        answer = _ => listing;
        var noMatch = Deluge() with { AssetPattern = "^nothing-like-this$" };
        var noAddress = Deluge() with { DirectoryListingUrl = null };
        answer = path => path.EndsWith("/windows/", StringComparison.Ordinal) ? listing : null;

        Assert.Null(await DirectoryListingLatestRelease.ResolveAsync(noMatch, CancellationToken.None));
        Assert.Null(await DirectoryListingLatestRelease.ResolveAsync(noAddress, CancellationToken.None));
        answer = _ => null;
        Assert.Null(await DirectoryListingLatestRelease.ResolveAsync(Deluge(), CancellationToken.None));
        listener.Stop();
        Assert.Null(await DirectoryListingLatestRelease.ResolveAsync(Deluge(), CancellationToken.None));
    }
}
