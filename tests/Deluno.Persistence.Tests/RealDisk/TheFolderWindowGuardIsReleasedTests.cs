using Deluno.Filesystem;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Golden path rc.30.35, filesystem.native-folder-picker: after one ask the next ask was refused with "A folder window is
/// already open on the server" for three minutes. A window nobody answered stayed open and kept the one-at-a-time guard,
/// so every later ask was refused by a window nobody could see. The ask now closes its own window when it ends and frees
/// the guard before it replies.
/// </summary>
[Collection("The server folder window guard")]
public sealed class TheFolderWindowGuardIsReleasedTests : IDisposable
{
    private readonly Func<string?, NativePickerWindow, Task<WindowsFolderPickerResult>> originalStarter = FilesystemEndpointRouteBuilderExtensions.NativePickerStarter;
    private readonly TimeSpan originalTimeout = FilesystemEndpointRouteBuilderExtensions.NativePickerTimeout;

    public void Dispose()
    {
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = originalStarter;
        FilesystemEndpointRouteBuilderExtensions.NativePickerTimeout = originalTimeout;
    }

    private static async Task<int> AskAsync(CancellationToken cancellationToken = default)
    {
        var result = await FilesystemEndpointRouteBuilderExtensions.PickNativeFolderAsync(new DefaultHttpContext(), new NativeFolderPickerRequest("/media"), cancellationToken);
        return result is IStatusCodeHttpResult status ? status.StatusCode ?? 200 : 200;
    }

    [Fact]
    public async Task An_ask_straight_after_an_answered_one_is_not_refused()
    {
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.Selected("/media"));

        for (var round = 0; round < 50; round++)
        {
            Assert.Equal(200, await AskAsync());
        }
    }

    [Fact]
    public async Task An_ask_straight_after_a_cancelled_one_is_not_refused()
    {
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.FromCancelled());

        for (var round = 0; round < 50; round++)
        {
            Assert.Equal(200, await AskAsync());
        }
    }

    [Fact]
    public async Task A_window_nobody_answered_is_closed_and_the_next_ask_is_not_refused()
    {
        FilesystemEndpointRouteBuilderExtensions.NativePickerTimeout = TimeSpan.FromMilliseconds(100);
        var closed = 0;
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, window) =>
        {
            var answer = new TaskCompletionSource<WindowsFolderPickerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The window opens and nobody answers; closing it is the same as Cancel.
            window.Closing = () => { Interlocked.Increment(ref closed); answer.TrySetResult(WindowsFolderPickerResult.FromCancelled()); };
            return answer.Task;
        };

        Assert.Equal(408, await AskAsync());
        Assert.Equal(1, Volatile.Read(ref closed));

        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.FromCancelled());
        Assert.Equal(200, await AskAsync());
    }

    [Fact]
    public async Task A_window_still_open_refuses_a_second_ask_in_words_and_the_first_is_unharmed()
    {
        var answer = new TaskCompletionSource<WindowsFolderPickerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => answer.Task;

        var first = AskAsync();
        Assert.Equal(409, await AskAsync());

        answer.SetResult(WindowsFolderPickerResult.Selected("/media"));
        Assert.Equal(200, await first);
        Assert.Equal(200, await AskAsync());
    }

    [Fact]
    public async Task An_ask_the_browser_gave_up_on_closes_its_window_and_frees_the_guard()
    {
        var closed = 0;
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, window) =>
        {
            var answer = new TaskCompletionSource<WindowsFolderPickerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closing = () => { Interlocked.Increment(ref closed); answer.TrySetResult(WindowsFolderPickerResult.FromCancelled()); };
            return answer.Task;
        };
        using var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(gaveUp.Token));
        Assert.Equal(1, Volatile.Read(ref closed));

        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.FromCancelled());
        Assert.Equal(200, await AskAsync());
    }

    private static async Task<(int Status, System.Text.Json.JsonElement Body)> AnswerAsync()
    {
        var result = await FilesystemEndpointRouteBuilderExtensions.PickNativeFolderAsync(new DefaultHttpContext(), new NativeFolderPickerRequest("/media"), CancellationToken.None);
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var json = System.Text.Json.JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return (status, json);
    }

    [Fact]
    public async Task A_host_with_no_folder_window_answers_409_unavailable_in_the_hosts_own_words()
    {
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) =>
            Task.FromResult(WindowsFolderPickerResult.Unavailable("A folder window needs a signed-in desktop; this is a service."));

        var (status, body) = await AnswerAsync();

        Assert.Equal(409, status);
        Assert.True(body.GetProperty("nativePickerUnavailable").GetBoolean());
        Assert.Equal("A folder window needs a signed-in desktop; this is a service.", body.GetProperty("message").GetString());
        // The guard is free again: the next ask is answered, not refused as "already open".
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.FromCancelled());
        Assert.Equal(200, await AskAsync());
    }

    [Fact]
    public async Task A_chosen_folder_comes_back_as_the_path_and_a_cancelled_window_comes_back_as_cancelled()
    {
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.Selected("/media/Films"));
        var chosen = await AnswerAsync();
        FilesystemEndpointRouteBuilderExtensions.NativePickerStarter = (_, _) => Task.FromResult(WindowsFolderPickerResult.FromCancelled());
        var cancelled = await AnswerAsync();

        Assert.Equal(200, chosen.Status);
        Assert.Equal("/media/Films", chosen.Body.GetProperty("path").GetString());
        Assert.False(chosen.Body.GetProperty("cancelled").GetBoolean());
        Assert.Equal(200, cancelled.Status);
        Assert.True(cancelled.Body.GetProperty("cancelled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cancelled.Body.GetProperty("path").ValueKind);
    }
}
