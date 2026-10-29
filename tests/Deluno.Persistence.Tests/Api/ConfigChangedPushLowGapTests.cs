using Deluno.Api;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Api;

/// <summary>The low-severity rows of the Settings change push (#1329): the segments that name an area,.</summary>
public sealed class ConfigChangedPushLowGapTests
{
    private sealed class Dashboard : NullRealtimeEventPublisher, IRealtimeEventPublisher
    {
        public bool Throw { get; init; }

        public List<(string Name, string Area)> Sent { get; } = [];

        Task IRealtimeEventPublisher.PublishDashboardEventAsync(string eventName, object payload, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("the hub is down");
            }

            lock (Sent) Sent.Add((eventName, (string)payload.GetType().GetProperty("area")!.GetValue(payload)!));
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData("/api/notification-preferences", ConfigAreas.Notifications)]
    [InlineData("/api/seerr-servers/1", ConfigAreas.MediaServers)]
    [InlineData("/api/subtitle-providers/opensubtitles", ConfigAreas.Subtitles)]
    [InlineData("/api/subtitles/test", ConfigAreas.Subtitles)]
    [InlineData("/api/api-keys", ConfigAreas.Security)]
    [InlineData("/api/api-keys/abc", ConfigAreas.Security)]
    [InlineData("/api/failure-rules/1", ConfigAreas.DownloadHealth)]
    [InlineData("/api/exclusions", ConfigAreas.DownloadHealth)]
    public void The_first_segments_that_were_untested_each_map_to_their_settings_area(string path, string area)
        => Assert.Equal(area, ConfigChangedPush.AreaFor(new PathString(path)));

    private static async Task<(HttpResponseMessage Response, Dashboard Publisher)> WriteAsync(bool publisherThrows)
    {
        var publisher = new Dashboard { Throw = publisherThrows };
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddSingleton<IRealtimeEventPublisher>(publisher))
                .Configure(app =>
                {
                    app.UseDelunoConfigChangedPush();
                    app.Run(context =>
                    {
                        context.Response.StatusCode = 201;
                        return context.Response.WriteAsync("saved");
                    });
                }))
            .Build();
        await host.StartAsync();
        using var client = host.GetTestClient();
        var response = await client.PostAsync("/api/tags", new StringContent("{}"));
        await host.StopAsync();
        return (response, publisher);
    }

    [Fact]
    public async Task The_config_push_is_sent_with_the_area_in_the_payload()
    {
        var (response, publisher) = await WriteAsync(publisherThrows: false);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([(SystemChangeEventNames.ConfigChanged, "tags")], publisher.Sent);
    }
}
