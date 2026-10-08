using System.Net;
using System.Net.Http.Headers;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Compression and cache headers the browser depends on so a revisit never
/// re-downloads the bundle and a screen's JSON is small on the wire (#1081).
/// </summary>
public sealed class DelunoHttpTransportTests
{
    [Fact]
    public void Hashed_chunks_are_immutable_and_everything_else_revalidates()
    {
        Assert.Equal("public, max-age=31536000, immutable", DelunoStaticFileCaching.CacheControlFor("/assets/index-abc123.js"));
        Assert.Equal("no-cache", DelunoStaticFileCaching.CacheControlFor("/index.html"));
        Assert.Equal("no-cache", DelunoStaticFileCaching.CacheControlFor("/sw.js"));
        Assert.Equal("no-cache", DelunoStaticFileCaching.CacheControlFor("/manifest.json"));
    }

    [Fact]
    public async Task Json_is_compressed_and_static_assets_carry_their_cache_headers()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "deluno-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app-abc.js"), new string('x', 4000));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "sw.js"), "// sw");
        try
        {
            using var host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .UseWebRoot(webRoot)
                    .ConfigureServices(services => services.AddDelunoResponseCompression())
                    .Configure(app =>
                    {
                        app.UseResponseCompression();
                        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = DelunoStaticFileCaching.Apply });
                        app.Run(async context =>
                        {
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("[" + string.Join(",", Enumerable.Repeat("{\"title\":\"a film title\"}", 300)) + "]");
                        });
                    }))
                .StartAsync();
            var client = host.GetTestClient();
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

            var api = await client.GetAsync("/api/movies");
            Assert.Contains("br", api.Content.Headers.ContentEncoding);

            var chunk = await client.GetAsync("/assets/app-abc.js");
            Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
            Assert.Equal("public, max-age=31536000, immutable", chunk.Headers.CacheControl?.ToString());

            var worker = await client.GetAsync("/sw.js");
            Assert.Equal("no-cache", worker.Headers.CacheControl?.ToString());
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }
}
