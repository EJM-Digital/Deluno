using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1095: a task failure is worded for a person, and an old row with a machine timestamp or a raw exception reads the same way.</summary>
public sealed class PlainFailureTests
{
    private const string Key = "A saved key can't be read on this computer. Enter it again on the connection's page.";
    private const string Refused = "The service refused the connection. Check that it is running.";
    private const string Slow = "The service took too long to answer.";
    private const string Login = "The service refused Deluno's saved login or key. Check it on the connection's page.";
    private const string Disk = "The disk is full. Free up some space and Deluno will carry on.";
    private const string Missing = "A file or folder Deluno needs couldn't be found.";

    public static TheoryData<Exception, string> Mappings => new()
    {
        { new CryptographicException("Key not valid for use in specified state."), Key },
        { new InvalidOperationException("wrapper", new CryptographicException("x")), Key },
        { new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)), Refused },
        { new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)), "Deluno couldn't find the service's address. Check it is spelled right." },
        { new HttpRequestException("x", new SocketException((int)SocketError.HostUnreachable)), "Deluno couldn't reach the service over the network." },
        { new SocketException((int)SocketError.ConnectionRefused), Refused },
        { new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."), Slow },
        { new TimeoutException("x"), Slow },
        { new HttpRequestException("x", null, HttpStatusCode.Unauthorized), Login },
        { new HttpRequestException("x", null, HttpStatusCode.Forbidden), Login },
        { new HttpRequestException("x", null, HttpStatusCode.NotFound), "The service said that address doesn't exist. Check the connection's address." },
        { new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), "The service said Deluno is asking too often and told it to slow down." },
        { new HttpRequestException("x", null, HttpStatusCode.BadGateway), "The service had a problem on its side." },
        { new HttpRequestException("x", null, HttpStatusCode.InternalServerError), "The service had a problem on its side." },
        { new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)), Disk },
        { new IOException("no space", 28), Disk },
        { new UnauthorizedAccessException("Access to the path 'D:\\x' is denied."), "Deluno isn't allowed to use a file or folder it needs. Check the folder's permissions." },
        { new FileNotFoundException("Could not find file 'x'."), Missing },
        { new DirectoryNotFoundException("x"), Missing },
        { new InvalidOperationException("Sequence contains no elements"), "Something went wrong while searching the film library." },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Each_common_failure_is_worded_plainly(Exception exception, string expected)
    {
        Assert.Equal(expected, PlainFailure.Describe(exception, "searching the film library"));
    }

    [Fact]
    public void An_unknown_failure_names_the_task_and_never_the_exception_text()
    {
        Assert.Equal(
            "Something went wrong while refreshing a collection.",
            PlainFailure.Describe(new InvalidOperationException("NullReferenceException at Foo.Bar"), "refreshing a collection"));
        Assert.Equal("Something went wrong.", PlainFailure.Describe(new InvalidOperationException("x")));
    }

    [Theory]
    [InlineData(10, "Deluno will try again shortly.")]
    [InlineData(60, "Deluno will try again in about a minute.")]
    [InlineData(300, "Deluno will try again in about 5 minutes.")]
    [InlineData(3600, "Deluno will try again in about an hour.")]
    [InlineData(3 * 3600, "Deluno will try again in about 3 hours.")]
    [InlineData(86400, "Deluno will try again in about a day.")]
    public void Retry_wording_is_relative_to_the_event(int seconds, string expected)
    {
        Assert.Equal(expected, PlainFailure.RetrySentence(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void An_old_activity_row_with_an_iso_timestamp_reads_relative_to_its_own_time()
    {
        var created = DateTimeOffset.Parse("2026-09-30T09:20:28.8044076+00:00");
        var row = new ActivityEventItem(
            "a", "job.failed",
            "Search the film library will retry after 2026-09-30T09:25:28.8044076+00:00.",
            null, null, null, null, created);

        Assert.Equal("Search the film library will try again in about 5 minutes.", row.Message);
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}", row.Message);
    }

    [Fact]
    public void An_old_activity_row_with_the_crypto_sentence_reads_plainly()
    {
        var created = DateTimeOffset.Parse("2026-09-30T09:20:28+00:00");
        var row = new ActivityEventItem(
            "a", "job.failed",
            "Key not valid for use in specified state. Deluno will retry after 2026-09-30T10:20:28.0000000+00:00.",
            null, null, null, null, created);

        Assert.Equal(Key + " Deluno will try again in about an hour.", row.Message);
    }

    [Fact]
    public void A_scheduled_for_timestamp_in_an_outcome_reads_in_words()
    {
        var created = DateTimeOffset.Parse("2026-09-30T09:20:28+00:00");
        Assert.Equal(
            "Next automatic retry is scheduled in about 5 minutes.",
            PlainFailure.Rewrite("Next automatic retry is scheduled for 2026-09-30T09:25:28.0000000+00:00.", created));
    }

    [Fact]
    public void A_job_row_reads_its_last_error_in_plain_words_from_its_own_attempt_time()
    {
        var attempt = DateTimeOffset.Parse("2026-09-30T09:20:28+00:00");
        var job = new JobQueueItem(
            "j", "library.search.movies", "schedule", "failed", null, 1, attempt, attempt, null, null, null, null,
            "Key not valid for use in specified state. Deluno will retry after 2026-09-30T09:25:28+00:00.",
            null, null, null, null, 3, attempt, attempt.AddMinutes(5));

        Assert.DoesNotContain("Key not valid", job.LastError);
        Assert.Contains("in about 5 minutes", job.LastError);
    }

    [Fact]
    public void Plain_rows_pass_through_unchanged()
    {
        var created = DateTimeOffset.UtcNow;
        Assert.Equal("Deluno tried 3 times and gave up.", PlainFailure.Rewrite("Deluno tried 3 times and gave up.", created));
        Assert.Equal(string.Empty, PlainFailure.Rewrite(null, created));
    }
}
