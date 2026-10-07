using System.Net;
using Deluno.Filesystem;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1170: the Windows folder window opens on the server's own screen, so a
/// request from any other device is refused instead of left hanging.
/// </summary>
public sealed class NativeFolderPickerAccessTests
{
    [Theory]
    [InlineData("127.0.0.1", "192.168.1.51", false, true)]
    [InlineData("::1", "192.168.1.51", false, true)]
    [InlineData("::ffff:127.0.0.1", "192.168.1.51", false, true)]
    [InlineData("192.168.1.51", "192.168.1.51", false, true)]
    [InlineData("192.168.1.196", "192.168.1.51", false, false)]
    [InlineData("127.0.0.1", "127.0.0.1", true, false)]
    public void Only_a_browser_on_the_servers_own_machine_may_use_the_windows_folder_window(string remote, string local, bool forwarded, bool expected)
    {
        var headers = new HeaderDictionary();
        if (forwarded)
        {
            headers["X-Forwarded-For"] = "192.168.1.196";
        }

        Assert.Equal(
            expected,
            FilesystemEndpointRouteBuilderExtensions.IsFromThisMachine(IPAddress.Parse(remote), IPAddress.Parse(local), headers));
    }

    [Fact]
    public async Task A_request_from_another_device_is_refused_and_does_not_open_a_window()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.196");
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.51");

        var result = await FilesystemEndpointRouteBuilderExtensions.PickNativeFolderAsync(
            context, new NativeFolderPickerRequest(null), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result).StatusCode);
    }
}
