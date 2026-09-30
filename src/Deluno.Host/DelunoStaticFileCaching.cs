using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Host;

/// <summary>
/// Cache headers for the bundled web UI (#1081). Vite content-hashes everything
/// under /assets/, so those files never change under the same name and a
/// revisit must not download them again. index.html, the service worker and
/// the manifest keep their names across releases and always revalidate, so an
/// update is picked up on the next load.
/// </summary>
public static class DelunoStaticFileCaching
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string HtmlCacheControl = "no-cache";

    public static string CacheControlFor(PathString requestPath)
        => requestPath.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
            ? ImmutableCacheControl
            : HtmlCacheControl;

    public static void Apply(StaticFileResponseContext context)
        => context.Context.Response.Headers.CacheControl = CacheControlFor(context.Context.Request.Path);
}

public static class DelunoResponseCompressionExtensions
{
    /// <summary>
    /// Brotli and Gzip at the fastest level for JSON and the UI bundle (#1081).
    /// HTTPS compression stays off (the framework default) because compressing
    /// secret-bearing responses over TLS invites BREACH-style attacks; Deluno
    /// itself serves plain HTTP on the local machine, and a TLS reverse proxy
    /// compresses on its own.
    /// </summary>
    public static IServiceCollection AddDelunoResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }
}
