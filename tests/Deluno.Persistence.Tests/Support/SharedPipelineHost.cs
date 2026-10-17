using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Platform;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// The application on a real Kestrel listener with the shared request pipeline every host uses
/// (<c>AddDelunoWebPipeline</c> + <c>UseDelunoRequestPipeline</c>: rate limiter, sign-in gate, Allowed Hosts), a real temp data
/// folder and no owner account yet, so a test sees a fresh install exactly as the first visitor does. Only the schema
/// set-up runs; pollers and sweeps do not.
/// </summary>
internal sealed class SharedPipelineHost : IAsyncDisposable
{
    private readonly string _root;
    private readonly WebApplication _app;

    private SharedPipelineHost(string root, WebApplication app, HttpClient client)
    {
        _root = root;
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public string DataRoot => Path.Combine(_root, "data");

    public static async Task<SharedPipelineHost> StartAsync(IDictionary<string, string?>? configuration = null, string? root = null, Action<IServiceCollection>? configureServices = null, string? environment = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "deluno-shared-pipeline", Guid.NewGuid().ToString("N"));
        var webRoot = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html><body>Deluno</body></html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot, EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Storage:DataRoot"] = Path.Combine(root, "data");
        foreach (var pair in configuration ?? new Dictionary<string, string?>())
        {
            builder.Configuration[pair.Key] = pair.Value;
        }

        builder.Services.AddDelunoWebPipeline(builder.Configuration);
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApplicationModules();
        builder.Services.AddDelunoPlatformSecrets(Path.Combine(root, "data", "secrets", "master.key"));
        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "data", "protection-keys")));

        foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
        {
            var keep = descriptor.ImplementationType is { } type &&
                       (type == typeof(DelunoStorageBootstrapService) ||
                        type.Name.EndsWith("SchemaInitializer", StringComparison.Ordinal));
            if (!keep)
            {
                builder.Services.Remove(descriptor);
            }
        }

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseDelunoRequestPipeline();
        await app.StartAsync();

        return new SharedPipelineHost(root, app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private bool _shutDown;

    /// <summary>Stops the host the way a stop does (the lifetime stops, then the container is disposed) and leaves the data folder for a test to look at.</summary>
    public async Task ShutDownAsync()
    {
        if (_shutDown) return;
        _shutDown = true;
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutDownAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
