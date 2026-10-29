using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Security.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1419: every /api answer that is a server error carries the request's trace id in its body (so a failure on screen can be
/// quoted), and the log line for that failure carries the same id (so it can be found).
/// </summary>
public sealed class AServerErrorAnswerCarriesTheTraceIdAndTheLogLineHasItTests
{
    private sealed record Line(LogLevel Level, string Message, List<string> Scopes);

    private sealed class CapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<Line> Lines { get; } = [];
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new Capture(this);
        public void Dispose() { }

        private sealed class Capture(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<string>();
                owner._scopes.ForEachScope((scope, list) => list.Add(string.Join(";", (scope as IEnumerable<KeyValuePair<string, object>>)?.Select(p => $"{p.Key}={p.Value}") ?? [scope?.ToString() ?? ""])), scopes);
                lock (owner.Lines) owner.Lines.Add(new Line(logLevel, formatter(state, exception), scopes));
            }
        }
    }

    private static async Task<(SharedPipelineHost Host, CapturingProvider Logs)> StartAsync()
    {
        var logs = new CapturingProvider();
        var host = await SharedPipelineHost.StartAsync(configureServices: services => services.AddLogging(b => b.AddProvider(logs)));
        var created = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        created.EnsureSuccessStatusCode();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        // A bin state that is a folder, not a file: reading it throws something that is not a damaged-file message.
        Directory.CreateDirectory(Path.Combine(host.DataRoot, "recycle-bin", "state.json"));
        return (host, logs);
    }

    private static bool Mentions(Line line, string id)
        => line.Message.Contains(id, StringComparison.Ordinal) || line.Scopes.Any(scope => scope.Contains(id, StringComparison.Ordinal));

    [Fact]
    public async Task A_route_that_fails_answers_with_a_trace_id_that_is_in_the_header_and_on_the_log_line()
    {
        var (host, logs) = await StartAsync();
        await using var _ = host;

        var response = await host.Client.GetAsync("/api/recycle-bin");

        Assert.True((int)response.StatusCode >= 500, $"the route did not fail: {(int)response.StatusCode}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, Assert.Single(response.Headers.GetValues("X-Deluno-Trace-Id")));
        Assert.Contains(logs.Lines, line => line.Level >= LogLevel.Error && Mentions(line, id!));
    }

    [Fact]
    public async Task A_trace_id_the_caller_sent_is_the_one_in_the_body_and_on_the_log_line()
    {
        var (host, logs) = await StartAsync();
        await using var _ = host;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/recycle-bin");
        request.Headers.Add("X-Deluno-Trace-Id", "call-42_a.b");

        var response = await host.Client.SendAsync(request);

        Assert.True((int)response.StatusCode >= 500, $"the route did not fail: {(int)response.StatusCode}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("call-42_a.b", body.RootElement.GetProperty("traceId").GetString());
        Assert.Contains(logs.Lines, line => line.Level >= LogLevel.Error && Mentions(line, "call-42_a.b"));
    }

    [Fact]
    public async Task A_damaged_state_file_answers_its_plain_message_and_carries_the_trace_id_too()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        var state = Path.Combine(host.DataRoot, "recycle-bin", "state.json");
        Directory.Delete(state);
        await File.WriteAllTextAsync(state, "{\"settings\":{\"retentionDays\":30},\"items\":[{\"id\":\"ab");

        var response = await host.Client.GetAsync("/api/recycle-bin");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Deluno-Trace-Id")), body.RootElement.GetProperty("traceId").GetString());
    }
}
