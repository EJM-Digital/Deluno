using Deluno.Platform.Contracts;
using Deluno.Platform.Processing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Tests.Processing;

public sealed class ProcessorEventAnswerLogTests
{
    private static ProcessorEventRequest Report(string? status = "completed", string? handoffId = "h-1")
        => new(null, null, null, null, null, null, status, null, null, HandoffId: handoffId);

    [Fact]
    public void An_answer_with_a_message_is_described_with_its_status_hand_off_and_message()
    {
        var line = ProcessorEventAnswerLog.Describe(Report(), TypedResults.Conflict(new { message = "Nothing is waiting for that." }));

        Assert.Equal("POST /api/integrations/processors/events answered 409 for hand-off h-1, report completed: Nothing is waiting for that.", line);
    }

    [Fact]
    public void The_hand_off_in_the_answer_wins_over_the_one_in_the_report()
    {
        var line = ProcessorEventAnswerLog.Describe(Report(handoffId: "from-report"), TypedResults.Ok(new { handoffId = "from-answer", status = "settled" }));

        Assert.Equal("POST /api/integrations/processors/events answered 200 for hand-off from-answer, report completed: settled", line);
    }

    [Fact]
    public void A_title_stands_in_for_a_missing_message_and_a_message_beats_a_title()
    {
        Assert.EndsWith(": Bad", ProcessorEventAnswerLog.Describe(Report(), TypedResults.BadRequest(new { title = "Bad" })));
        Assert.EndsWith(": M", ProcessorEventAnswerLog.Describe(Report(), TypedResults.BadRequest(new { title = "T", message = "M" })));
        Assert.EndsWith(": Bad", ProcessorEventAnswerLog.Describe(Report(), TypedResults.BadRequest(new { title = "Bad", message = " " })));
    }

    [Fact]
    public void Validation_errors_replace_the_reason_with_each_field_and_its_problem()
    {
        var line = ProcessorEventAnswerLog.Describe(
            Report(),
            TypedResults.BadRequest(new { title = "One or more validation errors occurred.", errors = new { status = new[] { "required" }, outputPath = "missing" } }));

        Assert.EndsWith(": status: [\"required\"] outputPath: missing", line);
    }

    [Fact]
    public void An_errors_value_that_is_not_an_object_is_ignored()
    {
        var line = ProcessorEventAnswerLog.Describe(Report(), TypedResults.BadRequest(new { message = "M", errors = "none" }));

        Assert.EndsWith(": M", line);
    }

    [Fact]
    public void A_report_that_was_already_handled_says_so()
    {
        Assert.Contains(
            "answered 200 (already handled) for",
            ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok(new { alreadyHandled = true })));
        Assert.DoesNotContain(
            "already handled",
            ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok(new { alreadyHandled = false })));
        Assert.DoesNotContain(
            "already handled",
            ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok(new { alreadyHandled = "true" })));
    }

    [Fact]
    public void Nothing_known_is_written_as_none_and_unknown_with_no_reason()
    {
        Assert.Equal(
            "POST /api/integrations/processors/events answered 200 for hand-off none, report unknown",
            ProcessorEventAnswerLog.Describe(null, null));
        Assert.Equal(
            "POST /api/integrations/processors/events answered 204 for hand-off none, report unknown",
            ProcessorEventAnswerLog.Describe(null, TypedResults.NoContent()));
        Assert.Contains("hand-off none", ProcessorEventAnswerLog.Describe(Report(handoffId: null), null));
    }

    [Fact]
    public void An_answer_that_is_not_an_object_has_no_reason()
    {
        Assert.EndsWith("report completed", ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok("plain text")));
        Assert.EndsWith("report completed", ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok(new { message = 5 })));
        Assert.EndsWith("report completed", ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok(new { message = "" })));
        Assert.EndsWith("report completed", ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok<object?>(null)));
    }

    [Fact]
    public void An_answer_that_cannot_be_turned_into_text_is_described_without_it()
    {
        var loop = new Loop();
        loop.Self = loop;

        var line = ProcessorEventAnswerLog.Describe(Report(), TypedResults.Ok(loop));

        Assert.EndsWith("for hand-off h-1, report completed", line);
    }

    [Fact]
    public async Task The_answer_is_passed_on_unchanged_and_one_line_is_logged()
    {
        var sink = new Sink();
        var context = ContextWith(new ServiceCollection().AddSingleton<ILoggerFactory>(new SinkFactory(sink)), Report());
        var answer = TypedResults.Ok(new { message = "ok" });

        var result = await ProcessorEventAnswerLog.LogAsync(context, _ => ValueTask.FromResult<object?>(answer));

        Assert.Same(answer, result);
        Assert.Equal(ProcessorEventAnswerLog.Category, sink.Category);
        Assert.Equal(["POST /api/integrations/processors/events answered 200 for hand-off h-1, report completed: ok"], sink.Lines);
    }

    [Fact]
    public async Task A_call_without_a_report_is_still_logged()
    {
        var sink = new Sink();
        var context = ContextWith(new ServiceCollection().AddSingleton<ILoggerFactory>(new SinkFactory(sink)), request: null);

        await ProcessorEventAnswerLog.LogAsync(context, _ => ValueTask.FromResult<object?>(null));

        Assert.Equal(["POST /api/integrations/processors/events answered 200 for hand-off none, report unknown"], sink.Lines);
    }

    [Fact]
    public async Task With_no_logging_set_up_the_answer_still_goes_through()
    {
        var context = ContextWith(new ServiceCollection(), Report());

        Assert.Equal("x", await ProcessorEventAnswerLog.LogAsync(context, _ => ValueTask.FromResult<object?>("x")));
    }

    [Fact]
    public async Task A_log_that_fails_never_changes_the_answer()
    {
        var context = ContextWith(new ServiceCollection().AddSingleton<ILoggerFactory>(new ThrowingFactory()), Report());

        Assert.Equal("x", await ProcessorEventAnswerLog.LogAsync(context, _ => ValueTask.FromResult<object?>("x")));
    }

    private static EndpointFilterInvocationContext ContextWith(IServiceCollection services, ProcessorEventRequest? request)
    {
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return request is null
            ? EndpointFilterInvocationContext.Create(http, "not a report")
            : EndpointFilterInvocationContext.Create(http, "other argument", request);
    }

    private sealed class Loop
    {
        public Loop? Self { get; set; }
    }

    private sealed class Sink
    {
        public string? Category { get; set; }

        public List<string> Lines { get; } = [];
    }

    private sealed class SinkFactory(Sink sink) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName)
        {
            sink.Category = categoryName;
            return new SinkLogger(sink);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SinkLogger(Sink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                sink.Lines.Add(formatter(state, exception));
            }
        }
    }

    private sealed class ThrowingFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => throw new InvalidOperationException("no log");

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}
