using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Deluno.Filesystem;
using Microsoft.AspNetCore.Http;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #1170: the Windows folder window opens on the server's own screen, so a
/// request from any other device is refused instead of left hanging.
/// </summary>
[Collection("The server folder window guard")]
public sealed class NativeFolderPickerAccessTests
{
    [Theory]
    [InlineData("127.0.0.1", "192.168.1.10", false, true)]
    [InlineData("::1", "192.168.1.10", false, true)]
    [InlineData("::ffff:127.0.0.1", "192.168.1.10", false, true)]
    [InlineData("192.168.1.10", "192.168.1.10", false, true)]
    [InlineData("192.168.1.20", "192.168.1.10", false, false)]
    [InlineData("127.0.0.1", "127.0.0.1", true, false)]
    public void Only_a_browser_on_the_servers_own_machine_may_use_the_windows_folder_window(string remote, string local, bool forwarded, bool expected)
    {
        var headers = new HeaderDictionary();
        if (forwarded)
        {
            headers["X-Forwarded-For"] = "192.168.1.20";
        }

        Assert.Equal(
            expected,
            FilesystemEndpointRouteBuilderExtensions.IsFromThisMachine(IPAddress.Parse(remote), IPAddress.Parse(local), headers));
    }

    [Fact]
    public async Task A_request_from_another_device_is_refused_and_does_not_open_a_window()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");

        var result = await FilesystemEndpointRouteBuilderExtensions.PickNativeFolderAsync(
            context, new NativeFolderPickerRequest(null), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task A_caller_on_this_machine_is_refused_like_any_other_device_when_the_host_has_the_native_picker_off()
    {
        var originalStarter = FilesystemEndpointRouteBuilderExtensions.NativePickerStarter;
        var opened = 0;
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) =>
        {
            opened++;
            return Task.FromResult(WindowsFolderPickerResult.Selected("/media"));
        };
        try
        {
            var off = new DefaultHttpContext();
            off.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Filesystem:NativeFolderPicker"] = "false" }).Build())
                .BuildServiceProvider();
            var refused = await FilesystemEndpointRouteBuilderExtensions.PickNativeFolderAsync(off, new NativeFolderPickerRequest(null), CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(refused).StatusCode);
            Assert.Equal(0, opened);

            // Not set (every real install): a caller on this machine still gets the window.
            var on = new DefaultHttpContext();
            await FilesystemEndpointRouteBuilderExtensions.PickNativeFolderAsync(on, new NativeFolderPickerRequest(null), CancellationToken.None);
            Assert.Equal(1, opened);
        }
        finally
        {
            FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = originalStarter;
        }
    }
}
