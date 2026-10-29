using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Deluno.Api;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Two small parts of the shared web pipeline, run as the host runs them: the version alias leaves the five dedicated /api/v1
/// routes where they are and rewrites every other /api/v1 path, and JSON is compressed with Brotli or Gzip at the fastest level
/// while a live event stream is left alone.
/// </summary>
public sealed class TheVersionAliasAndCompressionRowsTests
{
    private static async Task<IHost> StartAsync(Action<IApplicationBuilder> configure, Action<IServiceCollection>? services = null)
        => await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(collection => { collection.AddRouting(); services?.Invoke(collection); })
                .Configure(configure))
            .StartAsync();

    [Theory]
    [InlineData("/api/v1/download-dispatches/abc")]
    [InlineData("/api/v1/import-resolutions/abc")]
    [InlineData("/api/v1/dispatch-metrics")]
    [InlineData("/api/v1/release-preferences/registry")]
    [InlineData("/api/v1/guides/trash/package")]
    public async Task The_five_dedicated_routes_keep_their_v1_path_and_any_other_v1_path_is_rewritten_to_the_plain_one(string dedicated)
    {
        using var host = await StartAsync(app =>
        {
            app.UseDelunoApiVersioning();
            app.Run(context => context.Response.WriteAsync(context.Request.Path.Value!));
        });
        var client = host.GetTestClient();

        Assert.Equal(dedicated, await client.GetStringAsync(dedicated));
        Assert.Equal("/api/movies/abc", await client.GetStringAsync("/api/v1/movies/abc"));
        Assert.Equal("/api/movies/abc", await client.GetStringAsync("/api/movies/abc"));
    }

    [Fact]
    public async Task Json_is_compressed_with_brotli_or_gzip_at_the_fastest_level_and_a_live_event_stream_is_not()
    {
        using var host = await StartAsync(
            app =>
            {
                app.UseResponseCompression();
                app.Run(async context =>
                {
                    context.Response.ContentType = context.Request.Path.StartsWithSegments("/stream") ? "text/event-stream" : "application/json";
                    await context.Response.WriteAsync(string.Join(",", Enumerable.Repeat("{\"title\":\"a film title\"}", 300)));
                });
            },
            services => services.AddDelunoResponseCompression());
        Assert.Equal(CompressionLevel.Fastest, host.Services.GetRequiredService<IOptions<BrotliCompressionProviderOptions>>().Value.Level);
        Assert.Equal(CompressionLevel.Fastest, host.Services.GetRequiredService<IOptions<GzipCompressionProviderOptions>>().Value.Level);

        async Task<HttpResponseMessage> GetAsync(string path, params string[] encodings)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            foreach (var encoding in encodings) request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
            return await host.GetTestClient().SendAsync(request);
        }

        Assert.Contains("br", (await GetAsync("/api/movies", "br", "gzip")).Content.Headers.ContentEncoding);
        Assert.Contains("gzip", (await GetAsync("/api/movies", "gzip")).Content.Headers.ContentEncoding);
        Assert.Empty((await GetAsync("/stream", "br", "gzip")).Content.Headers.ContentEncoding);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/movies")).StatusCode);
    }
}
