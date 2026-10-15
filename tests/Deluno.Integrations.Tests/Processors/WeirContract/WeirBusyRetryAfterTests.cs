using System.Globalization;
using System.Net;
using Deluno.Integrations.Processors;
using Deluno.Jobs.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Tests.Recorded;
using Moq;
using static Deluno.Integrations.Tests.Processors.WeirContract.WeirContract;

namespace Deluno.Integrations.Tests.Processors.WeirContract;

/// <summary>
/// #1266: Weir 1.0.0-rc.18 answers a write that waited the whole busy timeout with <c>503</c>, a <c>Retry-After</c> header in seconds and the JSON
/// <c>{"detail": ..., "code": "database_busy"}</c> (Weir #964), and an unforeseen failure with a JSON <c>500</c>
/// <c>{"detail": ..., "code": "internal_error"}</c>. Deluno reads <c>Retry-After</c> through one rule, <see cref="ProcessorRetryAfter"/>,
/// for every call it makes to Weir, and waits at least that long before it asks again.
///
/// <para><b>These replies are Weir 1.0.0-rc.18's own</b>, recorded on the golden VM by <c>scripts/recorded-replies/Record-WeirErrors.ps1</c>
/// (<c>weir/1.0.0-rc.18/database-busy-outcome</c> and <c>internal-error-outcome</c>), on a second Weir server whose busy timeout was one second
/// and whose write lock another program held. The recorded <c>Retry-After</c> is therefore <c>1</c>; where a test is about another number or
/// a date or no header, it is that same reply with only that header changed. Deluno reads neither body field for this rule (only the status
/// and the header).</para>
/// </summary>
public sealed class WeirBusyRetryAfterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T09:00:00Z", CultureInfo.InvariantCulture);
    private const string HandoffId = "01a1371c38837e979272e31cd3ce6c3a";

    private static HttpResponseMessage Busy(string? retryAfter)
    {
        var response = Rec("database-busy-outcome").Single.Reply.ToResponse();
        response.Headers.Remove("Retry-After");
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    private static HttpResponseMessage InternalError() => Rec("internal-error-outcome").Single.Reply.ToResponse();

    // ----- the rule

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 7 ", 7)]
    [InlineData("1", 1)]
    [InlineData("99999", 3600)]
    public void Seconds_are_read_and_a_very_long_wait_is_cut_to_an_hour(string header, double seconds)
    {
        using var response = Busy(header);

        Assert.Equal(TimeSpan.FromSeconds(seconds), ProcessorRetryAfter.Read(response, Now));
    }

    [Fact]
    public void An_HTTP_date_is_read_as_the_time_from_now_to_it()
    {
        using var response = Busy(Now.AddSeconds(45).ToString("R", CultureInfo.InvariantCulture));

        Assert.Equal(TimeSpan.FromSeconds(45), ProcessorRetryAfter.Read(response, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5 seconds")]
    [InlineData("1.5.2")]
    [InlineData("Wed, 01 Jan 2020 00:00:00 GMT")]
    public void A_missing_or_unreadable_or_pointless_header_is_not_given(string? header)
    {
        using var response = Busy(header);

        Assert.Null(ProcessorRetryAfter.Read(response, Now));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void The_wait_is_the_time_given_plus_at_most_a_tenth_of_it(double jitter)
    {
        var wait = ProcessorRetryAfter.Wait(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), jitter);

        Assert.InRange(wait, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(33));
    }

    [Fact]
    public void With_no_time_given_the_schedule_the_caller_had_stands()
        => Assert.Equal(TimeSpan.FromMinutes(4), ProcessorRetryAfter.Wait(TimeSpan.FromMinutes(4), null, 0.7));

    [Fact]
    public void The_recorded_503_is_Weirs_own_and_carries_the_time_it_asked_for()
    {
        var recorded = Rec("database-busy-outcome").Single.Reply;
        using var response = recorded.ToResponse();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("database_busy", recorded.Body);
        Assert.Equal(TimeSpan.FromSeconds(1), ProcessorRetryAfter.Read(response, Now));
    }

    [Fact]
    public void The_same_report_sent_again_after_the_lock_is_gone_is_an_ordinary_unknown_hand_off()
    {
        Assert.Equal(404, Rec("database-busy-retried").Single.Reply.Status);
        Assert.Equal(Rec("database-busy-outcome").Single.Request.Path, Rec("database-busy-retried").Single.Request.Path);
    }

    // ----- every call reads it the same way

    public static TheoryData<string> Calls => new() { "outcome", "submit", "status", "cancel", "capabilities" };

    private static async Task<TimeSpan?> CallAsync(ProcessorConnectionService service, string call)
    {
        switch (call)
        {
            case "outcome":
                var outcome = await service.ReportOutcomeAsync(Connection, HandoffId, ProcessorHandoffOutcome.Imported, "/library/x.mkv", null, Now, CancellationToken.None);
                Assert.Equal(ProcessorAnswer.Unreachable, outcome.Answer);
                Assert.False(outcome.Released);
                return outcome.RetryAfter;
            case "submit":
                var submitted = await service.SubmitAsync(Connection, new ProcessorHandoffItem(
                    HandoffId, "lib-1", "movies", "qbittorrent", "hash-1", "Film.2020.1080p", "C:\\Downloads\\Film.mkv", "Weir", "waiting", null, null, null, Now, Now), CancellationToken.None);
                Assert.False(submitted.IsAccepted);
                return submitted.RetryAfter;
            case "status":
                var status = await service.GetHandoffStatusAsync(Connection, HandoffId, CancellationToken.None);
                Assert.Equal(ProcessorAnswer.Unreachable, status.Answer);
                return status.RetryAfter;
            case "cancel":
                var cancel = await service.CancelHandoffAsync(Connection, HandoffId, CancellationToken.None);
                Assert.Equal(ProcessorAnswer.Unreachable, cancel.Answer);
                return cancel.RetryAfter;
            default:
                var support = await service.GetStatusSupportAsync(Connection, CancellationToken.None);
                Assert.Equal(ProcessorAnswer.Unreachable, support.Answer);
                return support.RetryAfter;
        }
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Every_call_answered_503_database_busy_stays_retryable_and_carries_the_time_Weir_asked(string call)
    {
        var service = new ProcessorConnectionService(new Factory(new Scripted(() => Busy("30"))), null, null, null, new FakeTime(Now));

        Assert.Equal(TimeSpan.FromSeconds(30), await CallAsync(service, call));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Every_call_answered_503_with_a_date_or_nothing_reads_it_the_same_way(string call)
    {
        var dated = new ProcessorConnectionService(new Factory(new Scripted(() => Busy(Now.AddSeconds(20).ToString("R", CultureInfo.InvariantCulture)))), null, null, null, new FakeTime(Now));
        var missing = new ProcessorConnectionService(new Factory(new Scripted(() => Busy(null))), null, null, null, new FakeTime(Now));

        Assert.Equal(TimeSpan.FromSeconds(20), await CallAsync(dated, call));
        Assert.Null(await CallAsync(missing, call));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Weirs_JSON_500_internal_error_is_retryable_like_the_plain_text_one_and_asks_for_no_wait(string call)
    {
        var service = new ProcessorConnectionService(new Factory(new Scripted(InternalError)), null, null, null, new FakeTime(Now));

        Assert.Null(await CallAsync(service, call));
    }

    // ----- Deluno waits

    [Fact]
    public async Task After_a_503_Deluno_sends_nothing_to_Weir_until_the_time_asked_has_passed()
    {
        var sent = 0;
        var time = new FakeTime(Now);
        var service = new ProcessorConnectionService(new Factory(new Scripted(() => { sent++; return Busy("30"); })), null, null, null, time);

        await service.GetHandoffStatusAsync(Connection, HandoffId, CancellationToken.None);
        Assert.Equal(1, sent);

        // Any call, ten seconds later: held back, and it says how long is left.
        time.Now = Now.AddSeconds(10);
        var held = await service.CancelHandoffAsync(Connection, HandoffId, CancellationToken.None);
        Assert.Equal(1, sent);
        Assert.Equal(ProcessorAnswer.Unreachable, held.Answer);
        Assert.InRange(held.RetryAfter!.Value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(23));

        // After the time asked and the most jitter can add, it asks again.
        time.Now = Now.AddSeconds(34);
        await service.GetStatusSupportAsync(Connection, CancellationToken.None);
        Assert.Equal(2, sent);
    }

    [Fact]
    public async Task A_reply_with_no_Retry_After_holds_nothing_back()
    {
        var sent = 0;
        var service = new ProcessorConnectionService(new Factory(new Scripted(() => { sent++; return Busy(null); })), null, null, null, new FakeTime(Now));

        await service.GetHandoffStatusAsync(Connection, HandoffId, CancellationToken.None);
        await service.GetHandoffStatusAsync(Connection, HandoffId, CancellationToken.None);

        Assert.Equal(2, sent);
    }

    [Fact]
    public async Task An_outcome_report_Weir_answered_503_is_owed_again_after_the_time_asked_and_not_after_the_usual_minutes()
    {
        var repository = new Mock<IProcessorRepository>();
        repository.Setup(r => r.FindProcessorConnectionAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Connection);
        ProcessorOutcomeRecord? saved = null;
        repository.Setup(r => r.SaveProcessorOutcomeAsync(It.IsAny<ProcessorOutcomeRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessorOutcomeRecord, CancellationToken>((record, _) => saved = record).Returns(Task.CompletedTask);
        var activity = new Mock<IActivityFeedRepository>();
        // The capability read answers; the report is what is busy.
        var capabilities = Rec("capabilities").Single.Reply;
        var service = new ProcessorConnectionService(
            new Factory(new Scripted(request => request.Method == HttpMethod.Get ? capabilities.ToResponse() : Busy("30"))), null, null, null, new FakeTime(Now));
        var owed = new ProcessorOutcomeRecord(
            HandoffId, "Weir", "lib-1", "Film.2020.1080p", ProcessorOutcomeRecord.Imported, "/library/x.mkv", null, Now, ProcessorOutcomeDeliveries.Pending,
            0, null, Now, false, null, Now, Connection.Id);

        var record = await ProcessorOutcomeDelivery.SendAsync(repository.Object, service, activity.Object, owed, Now, CancellationToken.None);

        Assert.Equal(ProcessorOutcomeDeliveries.Pending, record.Delivery);
        Assert.NotNull(record.NextAttemptUtc);
        Assert.InRange(record.NextAttemptUtc!.Value - Now, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(33));
        Assert.Same(saved, record);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        public Scripted(Func<HttpResponseMessage> reply) => _reply = _ => reply();

        public Scripted(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_reply(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
