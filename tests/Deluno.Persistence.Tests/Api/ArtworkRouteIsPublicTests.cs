using System.Net;
using System.Security.Cryptography;
using System.Text;
using Deluno.Api;
using Deluno.Connections;
using Deluno.Filesystem;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations;
using Deluno.Integrations.Metadata;
using Deluno.Intake;
using Deluno.Jobs;
using Deluno.Libraries;
using Deluno.Movies;
using Deluno.Notifications;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Realtime;
using Deluno.Recovery;
using Deluno.Security;
using Deluno.Series;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1146: posters are fetched by &lt;img&gt;, which cannot send a sign-in, so
/// the artwork route must answer a request with no credentials at all. The
/// route sits inside an authorised /api/metadata group; this makes a real
/// unauthenticated request through the real authentication and authorisation
/// middleware, so moving the route into an authorised group fails here.
/// </summary>
public sealed class ArtworkRouteIsPublicTests : IAsyncLifetime
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-artwork-public", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Storage:DataRoot"] = _root;
        builder.WebHost.UseTestServer();
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApi();
        builder.Services.AddDelunoSecurityModule();
        builder.Services.AddDelunoNotificationsModule();
        builder.Services.AddDelunoIntakeModule();
        builder.Services.AddDelunoPlatformModule();
        builder.Services.AddDelunoPlatformSecrets(Path.Combine(_root, "secrets", "master.key"));
        builder.Services.AddDelunoQualityModule();
        builder.Services.AddDelunoConnectionsModule();
        builder.Services.AddDelunoLibrariesModule();
        builder.Services.AddDelunoMoviesModule();
        builder.Services.AddDelunoSeriesModule();
        builder.Services.AddDelunoJobsModule();
        builder.Services.AddDelunoRecoveryModule();
        builder.Services.AddDelunoIntegrationsModule();
        builder.Services.AddScoped<TmdbMetadataProvider>();
        builder.Services.AddDelunoFilesystemModule();
        builder.Services.AddDelunoRealtimeModule();
        builder.Services.AddDelunoWorkerModule();

        // Only the schema set-up is needed; no background work.
        var hosted = builder.Services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && !(d.ImplementationType?.Name.Contains("SchemaInitializer") ?? false))
            .ToList();
        foreach (var descriptor in hosted)
        {
            builder.Services.Remove(descriptor);
        }

        _app = builder.Build();
        _app.UseRouting();
        // The Windows app puts this gate in front of every API route (#1146).
        _app.UseDelunoSignInGate((_, _) => ["read"]);
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapDelunoApplicationEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestServer().CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task An_unknown_key_without_a_sign_in_is_404_not_401()
    {
        var response = await _client.GetAsync("/api/metadata/artwork/" + new string('a', 64));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_cached_key_without_a_sign_in_returns_the_image_with_the_long_cache_header()
    {
        var url = "https://images.deluno.test/poster.jpg";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var directory = Path.Combine(_root, "artwork-cache");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, key + ".jpg");
        await File.WriteAllBytesAsync(file, Jpeg);

        var factory = _app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>();
        await using (var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Cache))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO artwork_cache (cache_key, media_type, remote_url, local_path, fetched_utc, expires_utc) " +
                "VALUES (@k, 'movies', @u, @l, '2026-10-01T00:00:00Z', NULL);";
            void Add(string n, string v) { var p = command.CreateParameter(); p.ParameterName = n; p.Value = v; command.Parameters.Add(p); }
            Add("@k", key); Add("@u", url); Add("@l", file);
            command.ExecuteNonQuery();
        }

        var response = await _client.GetAsync("/api/metadata/artwork/" + key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("image/", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age=604800", response.Headers.CacheControl?.ToString());
        Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Other_metadata_routes_still_need_a_sign_in()
    {
        var response = await _client.GetAsync("/api/metadata/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
