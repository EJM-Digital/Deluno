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
            // The key is the hand-off *as of this revision*, not the hand-off.
            //
            // It used to be the id alone, which is right for its first purpose:
            // a retry of the same submission must not make the processor do the
            // work twice. It is wrong for the second one. A forced re-download
            // resets this row rather than deleting it — deleting it would lose
            // the record that stops one download being submitted twice — so the
            // id is unchanged, the processor recognises the key it already
            // answered, and it declines to process a file the person has just
            // explicitly asked it to process again. The owner named exactly this:
            // "Weir will also need to have that record cleared as well so
            // it can re-process the file again if a user forces it."
            //
            // Stamping the revision keeps both. A retry of an unchanged row
            // carries the same key and is still ignored; a row that has been
            // deliberately put back to waiting carries a new one.
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key",
                $"{handoff.Id}:{handoff.UpdatedUtc.ToUnixTimeMilliseconds()}");
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new ProcessorSubmissionResult(true, "submitted", "Deluno submitted the hand-off to the processor.", statusCode);
            }

            // Say what the processor said.
            //
            // This used to record the status code alone. A real rejection on the
            // rig read "Processor submission returned HTTP 400." while the
            // response body held the whole answer - "Refiner's watched folder is
            // not set for this media scope, so there is nowhere to resolve the
            // hand-off against." - which names the setting to change. Throwing
            // that away leaves an owner with a number and nowhere to go, and it
            // is the processor, not Deluno, that knows why it refused.
            var explanation = await ReadRefusalAsync(response, cancellationToken);
            return new ProcessorSubmissionResult(
                false,
                "failed",
                explanation is null
                    ? $"Processor submission returned HTTP {statusCode}."
                    : $"The processor refused the hand-off with HTTP {statusCode}: {explanation}",
                statusCode);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            return new ProcessorSubmissionResult(false, "failed", "Deluno could not submit the hand-off. Check the processor connection before retrying.", null);
        }
    }

    /// <summary>
    /// The processor's own words, when it gave any.
    ///
    /// <para>Bounded and best-effort: a refusal is already a bad moment, and a
    /// processor that answers with a megabyte of HTML must not make it worse.
    /// A JSON <c>detail</c> or <c>message</c> is preferred because that is what
    /// FastAPI and friends put the sentence in; anything else is taken as plain
    /// text.</para>
    /// </summary>
    private static async Task<string?> ReadRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            body = body.Trim();
            if (body.StartsWith('{'))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    foreach (var name in (string[])["detail", "message", "error", "title"])
                    {
                        if (document.RootElement.TryGetProperty(name, out var value)
                            && value.ValueKind == System.Text.Json.JsonValueKind.String
                            && value.GetString() is { Length: > 0 } text)
                        {
                            return Trim(text);
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not JSON after all; fall through to the raw text.
                }
            }

            return Trim(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // Never let reading the explanation replace the failure being reported.
            return null;
        }

        static string Trim(string value)
        {
            var single = string.Join(" ", value.ReplaceLineEndings(" ").Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return single.Length <= 400 ? single : single[..400] + "…";
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
