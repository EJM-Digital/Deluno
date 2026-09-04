using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Platform;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// The whole application, over HTTP, signed in as its owner.
///
/// <para><b>Why this exists.</b> The coverage inventory counted 369 API routes
/// and found 202 that no test so much as mentions. The reason was never that
/// nobody wanted to test them: it was that every existing API test stands up its
/// own miniature host, registering by hand the two or three services its own
/// endpoints need. That is fine for one route and absurd for two hundred, so
/// the routes nobody had budget for stayed untested.</para>
///
/// <para>This composes the application the way the application composes itself
/// — <see cref="DelunoApplicationComposition.AddDelunoApplicationModules"/> and
/// <see cref="DelunoApplicationEndpointMapping.MapDelunoApplicationEndpoints"/>,
/// the same two calls Deluno.Host, the tray and the service all make. A test
/// written against it exercises the real container, the real route table, the
/// real authentication handler and the real authorization policies, and it
/// costs one line to start.</para>
///
/// <para>It is also the only test that boots the shipped composition over HTTP.
/// The four defects #81 found — no listener, missing endpoint groups, a folder
/// picker that could not go up, a restore that did nothing — were all invisible
/// to a suite whose every host was hand-assembled.</para>
/// </summary>
internal sealed class ApplicationTestHost : IAsyncDisposable
{
    private readonly TestStorage _storage;
    private readonly IHost _host;

    private ApplicationTestHost(TestStorage storage, IHost host, HttpClient client)
    {
        _storage = storage;
        _host = host;
        Client = client;
    }

    /// <summary>Authenticated as the owner, who holds every scope.</summary>
    public HttpClient Client { get; }

    public IServiceProvider Services => _host.Services;

    public string DataRoot => _storage.DataRoot;

    public static async Task<ApplicationTestHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var storage = TestStorage.Create();
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Storage:DataRoot"] = storage.DataRoot }))
                .ConfigureServices((context, services) =>
                {
                    services.AddDelunoInfrastructure(context.Configuration);
                    services.AddDelunoPlatformSecrets(Path.Combine(storage.DataRoot, "secrets", "master.key"));
                    services.AddDelunoApplicationModules();
                    KeepOnlyTheServicesThatBuildTheSchema(services);
                })
                .Configure(app =>
                {
                    // The pipeline the hosts share, minus what belongs to a
                    // real deployment rather than to the API: static files,
                    // rate limiting, the migration gate and version aliasing.
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapDelunoApplicationEndpoints());
                }))
            .Build();

        await host.StartAsync(cancellationToken);

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await SignInAsOwnerAsync(host, client, cancellationToken));

        return new ApplicationTestHost(storage, host, client);
    }

    /// <summary>
    /// Schema initializers run; pollers, schedulers and scanners do not.
    ///
    /// <para>The route handlers under test need their tables to exist, and
    /// nothing else. Starting the background work as well would have every test
    /// racing a dispatch poller and an availability sweep for the same SQLite
    /// file, which is how a suite becomes something people rerun until it
    /// passes.</para>
    /// </summary>
    private static void KeepOnlyTheServicesThatBuildTheSchema(IServiceCollection services)
    {
        var schemaBuilders = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Where(descriptor => descriptor.ImplementationType is not null &&
                                 (descriptor.ImplementationType == typeof(DelunoStorageBootstrapService) ||
                                  descriptor.ImplementationType.Name.EndsWith("SchemaInitializer", StringComparison.Ordinal)))
            .ToArray();

        foreach (var descriptor in services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                     .ToArray())
        {
            services.Remove(descriptor);
        }

        foreach (var descriptor in schemaBuilders)
        {
            services.Add(descriptor);
        }
    }

    /// <summary>
    /// Creates the first owner and logs in through the real endpoint, so every
    /// request a test makes carries a token the real handler issued.
    /// </summary>
    private static async Task<string> SignInAsOwnerAsync(IHost host, HttpClient client, CancellationToken cancellationToken)
    {
        const string username = "owner";
        const string password = "Deluno-Test-2026!";

        var repository = host.Services.GetRequiredService<ISecurityRepository>();
        await repository.BootstrapUserAsync(
            new BootstrapUserRequest(username, "Test Owner", password),
            cancellationToken);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(username, password),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
        return login!.AccessToken;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _storage.Dispose();
    }
}
