using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Whether the machine's own clock is within a sane tolerance of a trusted
/// time source (#868). A wrong system clock breaks things in ways that do
/// not look like a clock problem: a private tracker rejects an announce
/// signed with the wrong time, and a TLS/HTTPS handshake fails outright when
/// a certificate looks not-yet-valid or expired to a clock that is off by
/// more than a few minutes -- both read like an unrelated connection
/// failure until somebody happens to check the time by hand.
///
/// <para>Reads the <c>Date</c> header off a plain request to TMDb, a service
/// Deluno already talks to for metadata, and compares it with Deluno's own
/// clock. No API key or request body is needed to get a timestamped
/// response back.</para>
/// </summary>
public sealed class SystemTimeHealthCheck(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : IHealthCheck
{
    private const string Id = "system:clock";
    private const string Name = "System clock";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly Uri TimeSourceUri = new("https://api.themoviedb.org/3/configuration");

    public string Category => HealthCheckCategories.System;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HealthCheckHttpClients.SystemTime);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TimeSourceUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var sourceUtc = response.Headers.Date;
            if (sourceUtc is null)
            {
                return [HealthCheckOutcome.Unknown(
                    Id, Category, Name,
                    "Deluno could not read a trusted time source's clock to compare against its own.")];
            }

            var drift = timeProvider.GetUtcNow() - sourceUtc.Value;
            var absoluteDrift = drift.Duration();

            if (absoluteDrift <= Tolerance)
            {
                return [HealthCheckOutcome.Ok(
                    Id, Category, Name,
                    $"Deluno's clock is within {FormatDrift(absoluteDrift)} of a trusted time source.")];
            }

            var direction = drift > TimeSpan.Zero ? "ahead of" : "behind";
            return [HealthCheckOutcome.Problem(
                Id, Category, Name,
                $"Deluno's clock is {FormatDrift(absoluteDrift)} {direction} a trusted time source.",
                cause: "A wrong system clock makes private trackers reject Deluno's announces, and can make TLS/HTTPS connections fail outright -- both trust a request partly by checking the time.",
                fix: "Fix the clock on the machine running Deluno, ideally by turning on automatic time sync (NTP).")];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return [HealthCheckOutcome.Unknown(
                Id, Category, Name,
                $"Deluno could not reach a trusted time source to check its clock: {exception.Message}")];
        }
    }

    private static string FormatDrift(TimeSpan drift)
        => drift.TotalMinutes < 1 ? $"{drift.TotalSeconds:0} seconds" : $"{drift.TotalMinutes:0.#} minutes";
}
