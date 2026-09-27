using Deluno.Security.Contracts;
using Deluno.Security.Hardening;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Platform.Tests.Security.Hardening;

public sealed class DelunoAllowedHostsMiddlewareTests
{
    private sealed class FakeCache(AllowedHostsSettings settings) : IAllowedHostsSettingsCache
    {
        public Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public void Invalidate() { }
    }

    private static DefaultHttpContext HttpContextFor(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task Disabled_allows_any_host_through()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new DelunoAllowedHostsMiddleware(
            next, new FakeCache(AllowedHostsSettings.Off), new ForwardedHostTracker(), NullLogger<DelunoAllowedHostsMiddleware>.Instance);

        await middleware.InvokeAsync(HttpContextFor("anything.example.com"));

        Assert.True(called);
    }

    [Fact]
    public async Task Enabled_allows_a_listed_host()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new DelunoAllowedHostsMiddleware(
            next,
            new FakeCache(new AllowedHostsSettings(true, ["my-pc"])),
            new ForwardedHostTracker(),
            NullLogger<DelunoAllowedHostsMiddleware>.Instance);

        await middleware.InvokeAsync(HttpContextFor("my-pc:5099"));

        Assert.True(called);
    }

    [Fact]
    public async Task Enabled_allows_a_wildcard_subdomain()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new DelunoAllowedHostsMiddleware(
            next,
            new FakeCache(new AllowedHostsSettings(true, ["*.example.com"])),
            new ForwardedHostTracker(),
            NullLogger<DelunoAllowedHostsMiddleware>.Instance);

        await middleware.InvokeAsync(HttpContextFor("proxy.example.com"));

        Assert.True(called);
    }

    [Fact]
    public async Task Enabled_refuses_an_unlisted_host_with_a_plain_explanation_page_not_a_blank_400()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new DelunoAllowedHostsMiddleware(
            next,
            new FakeCache(new AllowedHostsSettings(true, ["my-pc"])),
            new ForwardedHostTracker(),
            NullLogger<DelunoAllowedHostsMiddleware>.Instance);

        var context = HttpContextFor("evil.example.com");
        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.StartsWith("text/html", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();
        Assert.Contains("does not answer", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evil.example.com", body);
    }

    [Fact]
    public async Task Enabled_always_allows_localhost_even_when_not_on_the_saved_list()
    {
        var called = false;
        RequestDelegate next = _ => { called = true; return Task.CompletedTask; };
        var middleware = new DelunoAllowedHostsMiddleware(
            next,
            new FakeCache(new AllowedHostsSettings(true, ["my-pc"])),
            new ForwardedHostTracker(),
            NullLogger<DelunoAllowedHostsMiddleware>.Instance);

        await middleware.InvokeAsync(HttpContextFor("localhost:5099"));

        Assert.True(called);
    }

    [Fact]
    public async Task A_forwarded_host_header_is_recorded_as_a_candidate_even_while_disabled()
    {
        var tracker = new ForwardedHostTracker();
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new DelunoAllowedHostsMiddleware(
            next, new FakeCache(AllowedHostsSettings.Off), tracker, NullLogger<DelunoAllowedHostsMiddleware>.Instance);

        var context = HttpContextFor("localhost:5099");
        context.Request.Headers["X-Forwarded-Host"] = "reverse-proxy.local";

        await middleware.InvokeAsync(context);

        Assert.Contains("reverse-proxy.local", tracker.Seen);
    }
}
