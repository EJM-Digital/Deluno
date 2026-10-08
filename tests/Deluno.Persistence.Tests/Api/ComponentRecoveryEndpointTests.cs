using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>#1177: a rebuilt app folder is shown on a screen and can be put right or dismissed from there.</summary>
public sealed class ComponentRecoveryEndpointTests
{
    private sealed class FakeNotices : IComponentRecoveryNotices
    {
        public bool Pending { get; private set; } = true;
        public int Readds { get; private set; }

        public Task<IReadOnlyList<ComponentRecoveryNotice>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ComponentRecoveryNotice>>(Pending
                ? [new ComponentRecoveryNotice("transmission", "Transmission", "Transmission's settings were rebuilt after Deluno was reinstalled; 3 torrents may need adding back.", 3, true)]
                : []);

        public Task<ComponentRecoveryResult?> ReaddAsync(string kind, CancellationToken cancellationToken)
        {
            if (kind != "transmission" || !Pending) return Task.FromResult<ComponentRecoveryResult?>(null);
            Readds++;
            Pending = false;
            return Task.FromResult<ComponentRecoveryResult?>(new ComponentRecoveryResult(true, "Deluno added 3 torrents back to Transmission."));
        }

        public Task<bool> DismissAsync(string kind, CancellationToken cancellationToken)
        {
            var had = Pending && kind == "transmission";
            if (!had) return Task.FromResult(false);
            Pending = false;
            return Task.FromResult(had);
        }
    }

    private static Task<ApplicationTestHost> Start(FakeNotices notices)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IComponentRecoveryNotices>();
            services.AddSingleton<IComponentRecoveryNotices>(notices);
        });

    [Fact]
    public async Task A_host_with_no_bundled_apps_reports_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.GetAsync("/api/system/component-recovery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    [Fact]
    public async Task A_rebuilt_app_is_listed_in_plain_words_then_added_back_and_gone()
    {
        var notices = new FakeNotices();
        await using var app = await Start(notices);

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/component-recovery");
        Assert.Contains("may need adding back", list[0].GetProperty("message").GetString());
        Assert.Equal(3, list[0].GetProperty("itemCount").GetInt32());

        var readd = await app.Client.PostAsync("/api/system/component-recovery/transmission/readd", null);
        Assert.Equal(HttpStatusCode.OK, readd.StatusCode);
        Assert.True((await readd.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("done").GetBoolean());
        Assert.Equal(1, notices.Readds);
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/system/component-recovery")).GetArrayLength());
    }

    [Fact]
    public async Task A_notice_can_be_dismissed_and_an_unknown_one_is_not_found()
    {
        var notices = new FakeNotices();
        await using var app = await Start(notices);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/system/component-recovery/qbittorrent/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsync("/api/system/component-recovery/transmission/dismiss", null)).StatusCode);
        Assert.Equal(0, (await app.Client.GetFromJsonAsync<JsonElement>("/api/system/component-recovery")).GetArrayLength());
        Assert.Equal(0, notices.Readds);
    }
}
