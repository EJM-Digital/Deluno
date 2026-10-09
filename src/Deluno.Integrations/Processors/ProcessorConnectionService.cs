using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Deluno.Infrastructure.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Processors;

/// <summary>
/// Sends a deliberately small, correlated submission payload to a configured
/// FileFlows custom webhook or generic processor webhook. It never grants the
/// external processor permission to import: Deluno still requires its guarded
/// completion callback before changing the library.
/// </summary>
public sealed class ProcessorConnectionService(
    IHttpClientFactory httpClientFactory,
    IProcessorHandoffTitleResolver? titleResolver = null,
    ILogger<ProcessorConnectionService>? logger = null) : IProcessorConnectionService
{
    public async Task<ProcessorConnectionTestResult> TestAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
    {
        if (connection.SecretUnreadable)
        {
            return new ProcessorConnectionTestResult(
                connection.Id, false, "degraded",
                connection.SecretProblem ?? Deluno.Security.SecretUnreadable.Message("secret"), null, null);
        }

        var started = Stopwatch.GetTimestamp();
        var healthUrl = string.IsNullOrWhiteSpace(connection.HealthCheckUrl) ? null : connection.HealthCheckUrl.Trim();
        try
        {
            // With a health-check address the processor can be asked a real question:
            // GET it, and a 2xx answer means it is up. Without one, all Deluno can do is
            // knock on the hand-off address, which only accepts submissions.
            using var response = await SendCheckAsync(connection, healthUrl, cancellationToken);
            var latency = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var statusCode = (int)response.StatusCode;

            if (healthUrl is not null)
            {
                if (response.IsSuccessStatusCode)
                {
                    return new ProcessorConnectionTestResult(connection.Id, true, "healthy", "The processor answered its health check and reports it is up.", statusCode, latency);
                }

                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        => new ProcessorConnectionTestResult(connection.Id, false, "degraded", $"The processor is running but rejected the configured credential on its health check (HTTP {statusCode}). Check the secret and header name on this connection.", statusCode, latency),
                    _ => new ProcessorConnectionTestResult(connection.Id, true, "degraded", $"The processor answered its health check with HTTP {statusCode}, so it is running but does not report that it is ready. Check the processor, then test again.", statusCode, latency)
                };
            }

            if (response.IsSuccessStatusCode)
            {
                return new ProcessorConnectionTestResult(connection.Id, true, "healthy", "The processor's hand-off address answered. That confirms it is up, but Deluno cannot check more than this without a health-check address.", statusCode, latency);
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
            // the letter of the spec, but FastAPI returns 404, and treating that as
            // "unreachable" reported a working processor as broken. It stays "degraded"
            // because nothing here proves it works; a health-check address does (#1055).
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    => new ProcessorConnectionTestResult(connection.Id, false, "degraded", "Processor endpoint is reachable but rejected the configured credential.", statusCode, latency),
                _ => new ProcessorConnectionTestResult(connection.Id, true, "degraded", $"Deluno could reach the processor, but its hand-off address only accepts submissions and answered the connection check with HTTP {statusCode}, so Deluno could not confirm it is working. Add a health-check address to this connection (Edit, under Processor connections) to test it properly; until then Deluno will validate it when the first hand-off is submitted.", statusCode, latency)
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            var latency = (int)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new ProcessorConnectionTestResult(connection.Id, false, "unreachable", "Deluno could not reach the processor endpoint. Check its URL, network path, and service status.", null, latency);
        }
    }

    /// <summary>
    /// A processor's capabilities answer (GET /api/v1/intake/capabilities, answered to the webhook secret) as
    /// text, or null when it cannot be had in a few seconds. Shared by the machine name and the version, both of
    /// which any processor may publish there.
    /// </summary>
    private async Task<string?> ReadCapabilitiesAsync(
        string? submissionUrl,
        string? authHeaderName,
        string? secret,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(submissionUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/v1/intake/capabilities"));
            if (!string.IsNullOrWhiteSpace(secret))
            {
                var header = string.IsNullOrWhiteSpace(authHeaderName) ? "Authorization" : authHeaderName.Trim();
                request.Headers.TryAddWithoutValidation(header, header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? $"Bearer {secret}" : secret);
            }

            using var response = await httpClientFactory.CreateClient("processor-health").SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> ReadVersionAsync(ProcessorConnectionItem connection, CancellationToken cancellationToken)
    {
        if (connection.SecretUnreadable)
        {
            return null;
        }

        var fromCapabilities = Deluno.Connections.Versions.ConnectionVersionParsers.FromProcessorCapabilities(
            await ReadCapabilitiesAsync(connection.SubmissionUrl, connection.AuthHeaderName, connection.Secret, cancellationToken));
        if (fromCapabilities is not null)
        {
            return fromCapabilities;
        }

        // The capabilities answer is for whoever holds the credential. When it cannot be had (a credential that no
        // longer matches, an older processor) the version the processor's own API description states, which it
        // answers to anyone, keeps the stored version from going stale (#1181).
        return Deluno.Connections.Versions.ConnectionVersionParsers.FromOpenApiInfo(
            await ReadOpenApiAsync(connection.SubmissionUrl, cancellationToken));
    }

    private async Task<string?> ReadOpenApiAsync(string? submissionUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(submissionUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/openapi.json"));
            using var response = await httpClientFactory.CreateClient("processor-health").SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> GetMachineNameAsync(
        string submissionUrl,
        string? authHeaderName,
        string? secret,
        CancellationToken cancellationToken)
    {
        var body = await ReadCapabilitiesAsync(submissionUrl, authHeaderName, secret, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("machine_name", out var value)
                   && value.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<string?> GetProductNameAsync(
        string submissionUrl,
        string? authHeaderName,
        string? secret,
        CancellationToken cancellationToken)
    {
        var body = await ReadCapabilitiesAsync(submissionUrl, authHeaderName, secret, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in new[] { "name", "product" })
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString()?.Trim() is { Length: > 0 and <= 60 } name)
                {
                    return name;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProcessorLibraryFolder>> GetLibraryFoldersAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
    {
        if (connection.SecretUnreadable || !ProcessorStatusEndpoints.TryDerive(connection.SubmissionUrl, out var endpoints))
        {
            return [];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoints.LibraryFolders);
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return ParseLibraryFolders(document.RootElement);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The list as a bare array, or inside the first array property of an object; entries with neither a watched nor an output folder are left out; an empty string is not reported.</summary>
    internal static IReadOnlyList<ProcessorLibraryFolder> ParseLibraryFolders(JsonElement root)
    {
        var list = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            list = default;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    list = property.Value;
                    break;
                }
            }
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<ProcessorLibraryFolder>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? Text(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
            var watched = Text("watched_folder");
            var output = Text("output_folder");
            if (watched is null && output is null)
            {
                continue;
            }

            found.Add(new ProcessorLibraryFolder(
                Text("manager_library_key"),
                watched,
                entry.TryGetProperty("folders_from_manager", out var fromManager) && fromManager.ValueKind == JsonValueKind.True,
                output,
                entry.TryGetProperty("minimum_file_size_bytes", out var minimum) && minimum.ValueKind == JsonValueKind.Number && minimum.TryGetInt64(out var bytes) && bytes > 0
                    ? bytes
                    : null));
        }

        return found;
    }

    private const int MaxCheckRedirects = 5;

    /// <summary>
    /// The connection check, following redirects by hand so the credential (and
    /// its custom header) only ever goes to the exact address the processor was
    /// handed off to: same scheme, host and port. Any other address, whether the
    /// health-check address itself or where a redirect leads, is asked bare (#1064).
    /// </summary>
    private async Task<HttpResponseMessage> SendCheckAsync(ProcessorConnectionItem connection, string? healthUrl, CancellationToken cancellationToken)
    {
        var method = healthUrl is null ? HttpMethod.Head : HttpMethod.Get;
        var url = healthUrl ?? connection.SubmissionUrl;
        var client = httpClientFactory.CreateClient("processor-health");
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (SameOrigin(url, connection.SubmissionUrl))
            {
                ApplyAuthentication(request, connection);
            }

            var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308
                && response.Headers.Location is { } location
                && hop < MaxCheckRedirects
                && Uri.TryCreate(new Uri(url), location, out var next)
                && next.Scheme is "http" or "https")
            {
                response.Dispose();
                url = next.AbsoluteUri;
                continue;
            }

            return response;
        }
    }

    /// <summary>Same scheme, host and port (#1064).</summary>
    internal static bool SameOrigin(string first, string second)
        => Uri.TryCreate(first, UriKind.Absolute, out var a)
           && Uri.TryCreate(second, UriKind.Absolute, out var b)
           && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
           && a.Port == b.Port;

    /// <summary>
    /// The hand-off body: the original keys in their original order, then, only when Deluno
    /// knows them from what it has stored, the title's name, year, ids, season, episode and
    /// poster (#1104). A value Deluno does not know is left out, never guessed.
    /// </summary>
    private async Task<Dictionary<string, object?>> BuildPayloadAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["eventType"] = "deluno.processor-handoff",
            ["handoffId"] = handoff.Id,
            ["libraryId"] = handoff.LibraryId,
            ["mediaType"] = handoff.MediaType,
            ["sourcePath"] = handoff.SourcePath,
            ["releaseName"] = handoff.ReleaseName,
            ["queueItemId"] = handoff.QueueItemId,
            ["callbackPath"] = "/api/integrations/processors/events"
        };

        ProcessorHandoffTitle? title = null;
        if (titleResolver is not null)
        {
            try
            {
                title = await titleResolver.ResolveAsync(handoff, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The title is a convenience for the processor; a lookup that fails must never stop the hand-off.
            }
        }

        if (title is not null)
        {
            void Add(string key, object? value)
            {
                if (value is not null)
                {
                    payload[key] = value;
                }
            }

            Add("title", title.Title);
            Add("year", title.Year);
            Add("tmdbId", title.TmdbId);
            Add("tvdbId", title.TvdbId);
            Add("imdbId", title.ImdbId);
            Add("season", title.Season);
            Add("episode", title.Episode);
            Add("posterUrl", title.PosterUrl);
        }

        if (handoff.OutputFiles is { Count: > 0 } files)
        {
            payload["outputFiles"] = files;
        }

        return payload;
    }

    public async Task<ProcessorSubmissionResult> SubmitAsync(
        ProcessorConnectionItem connection,
        ProcessorHandoffItem handoff,
        CancellationToken cancellationToken)
    {
        var title = handoff.ReleaseName;
        try
        {
            var payload = await BuildPayloadAsync(handoff, cancellationToken);
            if (payload.TryGetValue("title", out var resolvedTitle) && resolvedTitle is string resolved && resolved.Length > 0)
            {
                title = resolved;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, connection.SubmissionUrl)
            {
                Content = SizedJson.Create(payload)
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
            // explicitly asked it to process again. James named exactly this:
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
                // #1152: one line per hand-off, so a hand-off can be traced
                // without the HTTP pipeline's own chatter (now Warning).
                logger?.LogInformation(
                    "Hand-off sent: {HandoffTitle} to {Processor}, HTTP {StatusCode}, release {ReleaseName}, hand-off {HandoffId}",
                    title, connection.Name, statusCode, handoff.ReleaseName, handoff.Id);
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
            logger?.LogInformation(
                "Hand-off refused: {HandoffTitle} by {Processor}, HTTP {StatusCode}, reason {Reason}, release {ReleaseName}, hand-off {HandoffId}",
                title, connection.Name, statusCode, explanation ?? "none given", handoff.ReleaseName, handoff.Id);
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
            logger?.LogInformation(
                "Hand-off failed: {HandoffTitle} to {Processor}, could not reach it ({ErrorType}), release {ReleaseName}, hand-off {HandoffId}",
                title, connection.Name, exception.GetType().Name, handoff.ReleaseName, handoff.Id);
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

    public async Task<ProcessorStatusSupport> GetStatusSupportAsync(
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
    {
        if (!ProcessorStatusEndpoints.TryDerive(connection.SubmissionUrl, out var endpoints))
        {
            return ProcessorStatusSupport.None;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoints.Capabilities);
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                return new ProcessorStatusSupport(ProcessorAnswer.Forbidden, false, false, await ForbiddenMessageAsync(response, connection, cancellationToken));
            }

            if (IsWorthAskingAgain(response.StatusCode))
            {
                // A processor that is starting up or failing has not said what
                // it supports. Read as "nothing", it had its files judged as if
                // it could not be asked, and its outcome reports dropped (#580).
                return new ProcessorStatusSupport(
                    ProcessorAnswer.Unreachable,
                    false,
                    false,
                    $"{connection.Name} answered HTTP {(int)response.StatusCode}. {await ReadRefusalAsync(response, cancellationToken)}".TrimEnd());
            }

            if (!response.IsSuccessStatusCode)
            {
                // Absent or refused: this processor does not answer questions,
                // so Deluno keeps treating it the way it always has.
                return ProcessorStatusSupport.None;
            }

            var (capabilities, watchedPath) = await ReadCapabilitiesAsync(response, cancellationToken);
            return new ProcessorStatusSupport(
                ProcessorAnswer.Answered,
                capabilities.Contains("handoff-status"),
                capabilities.Contains("handoff-cancel"),
                null)
            {
                CanBeToldTheOutcome = capabilities.Contains("handoff-outcome"),
                WatchedPath = watchedPath,
                HasLibraryFolders = capabilities.Contains("library-folders")
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            return new ProcessorStatusSupport(ProcessorAnswer.Unreachable, false, false, $"Deluno could not reach {connection.Name}.");
        }
    }

    public async Task<ProcessorHandoffStatusResult> GetHandoffStatusAsync(
        ProcessorConnectionItem connection,
        string handoffId,
        CancellationToken cancellationToken)
    {
        if (!ProcessorStatusEndpoints.TryDerive(connection.SubmissionUrl, out var endpoints))
        {
            return new ProcessorHandoffStatusResult(ProcessorAnswer.Unsupported);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoints.Handoff(handoffId));
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);
            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.NotFound:
                    return new ProcessorHandoffStatusResult(ProcessorAnswer.NotFound, Message: $"{connection.Name} has no record of this hand-off.");
                case System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized:
                    return new ProcessorHandoffStatusResult(ProcessorAnswer.Forbidden, Message: await ForbiddenMessageAsync(response, connection, cancellationToken));
            }

            if (!response.IsSuccessStatusCode)
            {
                var explanation = await ReadRefusalAsync(response, cancellationToken);
                return new ProcessorHandoffStatusResult(
                    ProcessorAnswer.Unreachable,
                    Message: explanation is null
                        ? $"{connection.Name} answered HTTP {(int)response.StatusCode} when asked about the hand-off."
                        : $"{connection.Name} answered HTTP {(int)response.StatusCode}: {explanation}");
            }

            return await ReadHandoffStatusAsync(response, cancellationToken)
                ?? new ProcessorHandoffStatusResult(ProcessorAnswer.Unreachable, Message: $"{connection.Name} answered with a status Deluno could not read.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            return new ProcessorHandoffStatusResult(ProcessorAnswer.Unreachable, Message: $"Deluno could not reach {connection.Name}.");
        }
    }

    public async Task<ProcessorHandoffCancelResult> CancelHandoffAsync(
        ProcessorConnectionItem connection,
        string handoffId,
        CancellationToken cancellationToken)
    {
        if (!ProcessorStatusEndpoints.TryDerive(connection.SubmissionUrl, out var endpoints))
        {
            return new ProcessorHandoffCancelResult(ProcessorAnswer.Unsupported, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoints.Handoff(handoffId));
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => new ProcessorHandoffCancelResult(ProcessorAnswer.Answered, null),
                System.Net.HttpStatusCode.Conflict => new ProcessorHandoffCancelResult(ProcessorAnswer.Busy, await ReadRefusalAsync(response, cancellationToken)),
                System.Net.HttpStatusCode.NotFound => new ProcessorHandoffCancelResult(ProcessorAnswer.NotFound, null),
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized => new ProcessorHandoffCancelResult(ProcessorAnswer.Forbidden, await ForbiddenMessageAsync(response, connection, cancellationToken)),
                _ => new ProcessorHandoffCancelResult(ProcessorAnswer.Unreachable, await ReadRefusalAsync(response, cancellationToken))
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            return new ProcessorHandoffCancelResult(ProcessorAnswer.Unreachable, $"Deluno could not reach {connection.Name}.");
        }
    }

    public async Task<ProcessorOutcomeResult> ReportOutcomeAsync(
        ProcessorConnectionItem connection,
        string handoffId,
        ProcessorHandoffOutcome outcome,
        string? importedPath,
        string? reason,
        DateTimeOffset occurredUtc,
        CancellationToken cancellationToken)
    {
        if (!ProcessorStatusEndpoints.TryDerive(connection.SubmissionUrl, out var endpoints))
        {
            return new ProcessorOutcomeResult(ProcessorAnswer.Unsupported, false, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.HandoffOutcome(handoffId))
            {
                Content = SizedJson.Create(new
                {
                    outcome = outcome == ProcessorHandoffOutcome.Imported ? "imported" : "not-imported",
                    occurredUtc = occurredUtc.ToUniversalTime(),
                    importedPath = outcome == ProcessorHandoffOutcome.Imported ? importedPath : null,
                    // Not only why an import did not happen any more: an
                    // imported season pack sends why any of its files were
                    // skipped here too, so the processor is told the whole
                    // story about what it gets to release (#652).
                    reason
                })
            };
            ApplyAuthentication(request, connection);
            using var response = await httpClientFactory.CreateClient("processor-connections").SendAsync(request, cancellationToken);

            // A processor disagreeing about its own bookkeeping must never fail
            // an import that has already happened, so every answer here is
            // recorded rather than thrown.
            //
            // Which answers are worth sending again is plain HTTP, not any one
            // processor's wording (#580): the same report again gets the same
            // answer from a processor that has it, so repeating is safe, and
            // only an answer no repeat can change ends it.
            return response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => await ReadReleaseAsync(response, cancellationToken),
                System.Net.HttpStatusCode.NotFound => new ProcessorOutcomeResult(ProcessorAnswer.NotFound, false, $"{connection.Name} has no record of this hand-off."),
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized =>
                    new ProcessorOutcomeResult(ProcessorAnswer.Forbidden, false, await ForbiddenMessageAsync(response, connection, cancellationToken)),
                // Not finished yet, or ended in a state it may still correct.
                System.Net.HttpStatusCode.Conflict => await ReadConflictAsync(response, connection, cancellationToken),
                var code when IsWorthAskingAgain(code) =>
                    new ProcessorOutcomeResult(ProcessorAnswer.Unreachable, false, await ReadRefusalAsync(response, cancellationToken) ?? $"{connection.Name} answered HTTP {(int)code}."),
                // 400, 422 and the rest: the report itself was wrong, and
                // sending the same report again would be wrong again.
                var code => new ProcessorOutcomeResult(ProcessorAnswer.Refused, false, await ReadRefusalAsync(response, cancellationToken) ?? $"{connection.Name} answered HTTP {(int)code}.")
            };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or TaskCanceledException))
        {
            return new ProcessorOutcomeResult(ProcessorAnswer.Unreachable, false, $"Deluno could not reach {connection.Name}.");
        }
    }

    /// <summary>
    /// A 409 to an outcome report is "not finished yet" (worth asking again) or "I already have a different
    /// answer" (final). A processor that says which, in a <c>code</c> field of its JSON answer, is believed:
    /// <c>outcome_already_recorded</c> is final (#1155). Without that code it stays owed, as before, and a
    /// 409 that says the hand-off ended is still not final, since a processor may correct it. Deluno reads the
    /// code, never a processor's wording.
    /// </summary>
    private static async Task<ProcessorOutcomeResult> ReadConflictAsync(
        HttpResponseMessage response,
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
    {
        var said = await ReadRefusalAsync(response, cancellationToken) ?? $"{connection.Name} answered HTTP 409.";
        var final = false;
        try
        {
            // The body is read a second time from the buffered content.
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            final = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == "outcome_already_recorded";
        }
        catch (JsonException)
        {
            // No code to read: it stays owed.
        }

        return new ProcessorOutcomeResult(final ? ProcessorAnswer.Settled : ProcessorAnswer.Busy, false, said);
    }

    /// <summary>
    /// Answers that say "not now" rather than "no": the processor is failing,
    /// overloaded or slow. Asking again later can get a different answer.
    /// </summary>
    private static bool IsWorthAskingAgain(System.Net.HttpStatusCode code)
        => (int)code >= 500
           || code is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests;

    private static async Task<ProcessorOutcomeResult> ReadReleaseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var released = document.RootElement.TryGetProperty("released", out var value) &&
                           value.ValueKind == JsonValueKind.True;
            var message = document.RootElement.TryGetProperty("message", out var said) ? said.GetString() : null;
            return new ProcessorOutcomeResult(ProcessorAnswer.Answered, released, message);
        }
        catch (JsonException)
        {
            // It accepted the answer, which is the part that matters.
            return new ProcessorOutcomeResult(ProcessorAnswer.Answered, false, null);
        }
    }

    /// <summary>
    /// A refusal here is one of two things, and each has one fix. Weir
    /// answers 403 when no webhook secret is set anywhere, and 401 when the one
    /// Deluno sent is missing or wrong (Weir #480). Said as that, because
    /// "forbidden" gives nobody anything to do.
    /// </summary>
    /// <remarks>
    /// Said once. The processor's own words were added in brackets, and for a
    /// refused secret they are the same instruction again: on the rig the Queue
    /// read "Set a webhook secret in Weir proof… (A webhook secret is
    /// required.)". The status code already says which of the two it is.
    /// </remarks>
    private static Task<string> ForbiddenMessageAsync(
        HttpResponseMessage response,
        ProcessorConnectionItem connection,
        CancellationToken cancellationToken)
        => Task.FromResult(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? $"{connection.Name} refused the webhook secret on this processor connection. Copy {connection.Name}'s secret for Deluno into the connection, so Deluno can ask about hand-offs."
            : $"Set a webhook secret in {connection.Name} and the same secret on this processor connection in Deluno, so Deluno can ask about hand-offs.");

    private static async Task<(HashSet<string> Capabilities, string? WatchedPath)> ReadCapabilitiesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? watchedPath = null;
        try
        {
            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("capabilities", out var list) &&
                    list.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                        {
                            found.Add(value.Trim());
                        }
                    }
                }

                // Optional and generic (#909): any processor may add it, not
                // only Weir. Absent means "not reported", not "watching
                // nothing" -- a processor that predates this field is not
                // thereby broken.
                if (document.RootElement.TryGetProperty("watchedPath", out var path) &&
                    path.ValueKind == System.Text.Json.JsonValueKind.String &&
                    path.GetString() is { Length: > 0 } pathValue)
                {
                    watchedPath = pathValue.Trim();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable capabilities are no capabilities.
        }

        return (found, watchedPath);
    }

    private static async Task<ProcessorHandoffStatusResult?> ReadHandoffStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                ReadString(root, "state") is not { Length: > 0 } state)
            {
                return null;
            }

            return new ProcessorHandoffStatusResult(
                ProcessorAnswer.Answered,
                state.Trim().ToLowerInvariant(),
                root.TryGetProperty("queuePosition", out var position) && position.ValueKind == System.Text.Json.JsonValueKind.Number && position.TryGetInt32(out var number) ? number : null,
                ReadTimestamp(root, "scheduledFor"),
                ReadTimestamp(root, "lastChangedUtc"),
                ReadString(root, "outputPath"),
                ReadString(root, "message"));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        static string? ReadString(System.Text.Json.JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;

        static DateTimeOffset? ReadTimestamp(System.Text.Json.JsonElement root, string name)
            => ReadString(root, name) is { } text &&
               DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
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
