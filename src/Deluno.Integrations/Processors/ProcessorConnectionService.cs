using System.Diagnostics;
using System.Net.Http.Json;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Processors;

/// <summary>
/// Sends a deliberately small, correlated submission payload to a configured
/// FileFlows custom webhook or generic processor webhook. It never grants the
/// external processor permission to import: Deluno still requires its guarded
/// completion callback before changing the library.
/// </summary>
public sealed class ProcessorConnectionService(IHttpClientFactory httpClientFactory) : IProcessorConnectionService
{
    public async Task<ProcessorConnectionTestResult> TestAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, connection.SubmissionUrl);
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);
            var latency = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new ProcessorConnectionTestResult(connection.Id, true, "healthy", "Processor endpoint responded successfully.", statusCode, latency);
            }

            // A HEAD probe answers one question: did something answer? Any HTTP status
            // means the processor is there, so only a transport failure counts as
            // unreachable — and that is handled in the catch below.
            //
            // The exception is a rejected credential, which is reachable but not usable
            // and is worth surfacing on its own, because it is the one failure the
            // operator can fix from this screen.
            //
            // Everything else is inconclusive rather than bad. Processors answer a HEAD
            // on a POST-only route in whichever way their framework happens to: 405 is
            // the letter of the spec, but FastAPI — which Weir and plenty of others
            // are built on — returns 404, and treating that as "unreachable" reported a
            // working processor as broken.
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    => new ProcessorConnectionTestResult(connection.Id, false, "degraded", "Processor endpoint is reachable but rejected the configured credential.", statusCode, latency),
                _ => new ProcessorConnectionTestResult(connection.Id, true, "degraded", $"Processor endpoint is reachable but answered Deluno's safe connection check with HTTP {statusCode}, so it does not support that check. Deluno will validate the endpoint when the first hand-off is submitted.", statusCode, latency)
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            var latency = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new ProcessorConnectionTestResult(connection.Id, false, "unreachable", "Deluno could not reach the processor endpoint. Check its URL, network path, and service status.", null, latency);
        }
    }

    public async Task<ProcessorSubmissionResult> SubmitAsync(
        ProcessorConnectionItem connection,
        ProcessorHandoffItem handoff,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, connection.SubmissionUrl)
            {
                Content = JsonContent.Create(new
                {
                    eventType = "deluno.processor-handoff",
                    handoffId = handoff.Id,
                    libraryId = handoff.LibraryId,
                    mediaType = handoff.MediaType,
                    sourcePath = handoff.SourcePath,
                    releaseName = handoff.ReleaseName,
                    queueItemId = handoff.QueueItemId,
                    callbackPath = "/api/integrations/processors/events"
                })
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", handoff.Id);
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new ProcessorSubmissionResult(true, "submitted", "Deluno submitted the hand-off to the processor.", statusCode)
                : new ProcessorSubmissionResult(false, "failed", $"Processor submission returned HTTP {statusCode}.", statusCode);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            return new ProcessorSubmissionResult(false, "failed", "Deluno could not submit the hand-off. Check the processor connection before retrying.", null);
        }
    }

    private static void ApplyAuthentication(HttpRequestMessage request, ProcessorConnectionItem connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Secret))
        {
            return;
        }

        var value = string.Equals(connection.AuthHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase)
            ? $"Bearer {connection.Secret}"
            : connection.Secret;
        request.Headers.TryAddWithoutValidation(connection.AuthHeaderName, value);
    }
}
