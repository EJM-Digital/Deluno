using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1181: the version shown beside a processor (and compared with the newest release for the "is out" notice) was only
/// refreshed by a connection test, so a processor updated or reinstalled afterwards kept its old number and the bell
/// raised a notice for a version it already ran. Saving the connection again now reads the live version at once, and
/// a processor that will not answer the credentialed route still states it in its API description.
/// </summary>
public sealed class AProcessorSavedAgainReportsItsLiveVersionTests
{
    [Fact]
    public async Task Creating_and_then_saving_a_processor_stores_the_version_it_runs_now()
    {
        using var processor = new TinyProcessor("1.0.0-rc.6");
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await app.Client.PostAsync("/api/integrations/processors/connections", Json(processor.Body("Weir")));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal("1.0.0-rc.6", await VersionOfAsync(app, id!));

        // The processor is updated; saving the connection again reads the new number without a connection test.
        processor.Version = "1.0.0-rc.10";
        var saved = await app.Client.PutAsync($"/api/integrations/processors/connections/{id}", Json(processor.Body("Weir")));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal("1.0.0-rc.10", await VersionOfAsync(app, id!));
    }

    private static async Task<string?> VersionOfAsync(ApplicationTestHost app, string id)
    {
        var items = await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/versions");
        return items.EnumerateArray().FirstOrDefault(item => item.GetProperty("id").GetString() == id) is { ValueKind: JsonValueKind.Object } row
            ? row.GetProperty("version").GetString()
            : null;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>A processor that answers only its API description (the credentialed route says 401), as a stale credential would.</summary>
    private sealed class TinyProcessor : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly int port;

        public TinyProcessor(string version)
        {
            Version = version;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { return; }
                    var path = context.Request.Url!.AbsolutePath;
                    if (path == "/openapi.json")
                    {
                        var bytes = Encoding.UTF8.GetBytes($$$"""{"info":{"title":"Weir API","version":"{{{Version}}}"}}""");
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    else
                    {
                        context.Response.StatusCode = path == "/ready" ? 200 : 401;
                    }

                    context.Response.Close();
                }
            });
        }

        public string Version { get; set; }

        public string Body(string name)
            => $$"""{"name":"{{name}}","provider":"generic-webhook","submissionUrl":"http://127.0.0.1:{{port}}/api/v1/intake/webhook/deluno","authHeaderName":"X-Webhook-Secret","secret":"s","isEnabled":true,"healthCheckUrl":"http://127.0.0.1:{{port}}/ready"}""";

        public void Dispose() => listener.Close();
    }
}
