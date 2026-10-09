using Deluno.Connections.Usenet;
using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class SabnzbdDownloadClient(IHttpClientFactory httpClientFactory, TimeSpan? readBackDelay = null) : DownloadClientBase
{
    private readonly TimeSpan _readBackDelay = readBackDelay ?? TimeSpan.FromMilliseconds(500);

    public override string Protocol => "sabnzbd";

    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(
        SupportsQueue: true, SupportsHistory: true, SupportsPauseResume: true, SupportsRemove: true,
        SupportsRecheck: false, SupportsImportPath: true, AuthMode: "api-key", SupportsQueueReorder: true);

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var apiKey = client.Secret ?? client.Username;
        if (string.IsNullOrWhiteSpace(apiKey)) return DownloadClientHelpers.GrabFailure(client, request, "SABnzbd API key is missing.");
        var query = new Dictionary<string, string>
        {
            ["mode"] = "addurl", ["apikey"] = apiKey, ["name"] = request.DownloadUrl,
            ["cat"] = DownloadClientHelpers.ResolveCategory(client, request), ["output"] = "json"
        };
        // #1000: the release's own name as SABnzbd's job name. Without it
        // SABnzbd guesses one from the fetch URL or the file the indexer
        // serves (often an opaque id), so its queue and its finished-download
        // folder read as gibberish and the queue match by name gets weaker.
        // addurl's documented `nzbname` ("Name of the job, if empty the NZB
        // filename is used") overrides the guess. Radarr and Sonarr never
        // needed it because they fetch the NZB themselves and upload it under
        // the release title (SabnzbdProxy.DownloadNzb, addfile).
        if (!string.IsNullOrWhiteSpace(request.ReleaseName))
        {
            query["nzbname"] = request.ReleaseName.Trim();
        }

        // #663: SABnzbd's addurl priority is the small discrete enum documented
        // at sabnzbd.org/wiki (Low/Normal/High), not NZBGet's continuous scale —
        // Deluno's request only says which side of normal it wants, so that is
        // all that is mapped across. Omitted rather than forced to 0/Normal when
        // nothing was asked for, so an unset request still gets SABnzbd's own
        // default priority.
        // #805: SABnzbd's own Paused priority (-2) outranks whatever the
        // release's own urgency asked for — a batch someone queued paused by
        // hand should stay paused, not jump the queue the moment it starts.
        if (client.AddPaused)
        {
            query["priority"] = "-2";
        }
        else if (request.Priority is { } priority && priority != 0)
        {
            query["priority"] = (priority < 0 ? -1 : 1).ToString(CultureInfo.InvariantCulture);
        }
        // Sent only when Deluno actually has a password for this release — a
        // protected archive that arrived from an indexer that provides one.
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            query["password"] = request.Password;
        }

        var uri = new Uri(baseUri, $"api?{DownloadClientHelpers.BuildQuery(query)}");
        using var response = await httpClientFactory.CreateClient("download-clients").GetAsync(uri, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        string? externalId = null;
        var accepted = false;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            accepted = document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
            if (document.RootElement.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                externalId = ids.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            }
        }
        catch (JsonException)
        {
            // The grab service retains the raw payload for the typed failure.
        }

        if (!accepted)
        {
            // #876: SABnzbd answered the same request with a plain refusal —
            // a dead link or an NZB it could not read, not a setup problem —
            // so the caller blocklists it and lets the next search pick a
            // different one, instead of leaving it to fail the same way again
            // on its ordinary retry cycle.
            return DownloadClientHelpers.DeadReleaseGrabFailure(client, request, "SABnzbd did not accept the release URL. It may be a dead link or a file it could not read.") with
            {
                ResponseCode = (int)response.StatusCode,
                ResponseJson = responseJson
            };
        }

        // #1062: status true with no id is SABnzbd saying yes and adding nothing,
        // usually because it discarded a duplicate. A grab Deluno cannot name
        // is never a success, since nothing could follow it afterwards.
        if (string.IsNullOrWhiteSpace(externalId))
        {
            // The grab service follows it only with a Deluno record of this same
            // release; otherwise it is a copy added from elsewhere and the release
            // is failed so another is tried.
            return DownloadClientHelpers.GrabSuccess(client, request, "SABnzbd already has this NZB.") with
            {
                ResponseCode = (int)response.StatusCode,
                ResponseJson = responseJson,
                IsDuplicateFollow = true
            };
        }

        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to SABnzbd.") with
        {
            ResponseCode = (int)response.StatusCode,
            ResponseJson = responseJson,
            ExternalId = externalId
        };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
        var apiKey = client.Secret;
        if (string.IsNullOrWhiteSpace(apiKey)) return CreateConfigurationSnapshot(client, capturedUtc, "SABnzbd API key is missing.");
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SabQueueResponse>(cancellationToken);
        var queue = (payload?.Queue?.Slots ?? []).Select(item =>
        {
            var sizeBytes = ParseSize(item.Mb) * 1_000_000L;
            var remainingBytes = ParseSize(item.Mbleft) * 1_000_000L;
            var downloadedBytes = Math.Max(0, sizeBytes - remainingBytes);
            var progress = sizeBytes <= 0 ? 0 : (downloadedBytes / (double)sizeBytes * 100);
            return new DownloadQueueItem(item.NzoId ?? item.Filename ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Filename ?? "Unknown SABnzbd item"),
                item.Filename ?? "Unknown SABnzbd item", item.Category ?? string.Empty, NormalizeStatus(item.Status, progress), progress,
                ((double)ParseSize(payload?.Queue?.Speed)), ParseEta(item.TimeLeft), sizeBytes, downloadedBytes, 0, "SABnzbd",
                QueueError(item.Status), capturedUtc);
        }).ToArray();
        var history = await GetHistoryCoreAsync(http, client, baseUri, apiKey, capturedUtc, cancellationToken);
        var freeSpaceBytes = ParseGbString(payload?.Queue?.DiskSpace1);
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to SABnzbd at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            ParseGbString(payload?.Queue?.DiskSpaceTotal1));
        // #812: SABnzbd's own global pause, read from the same queue/status
        // call rather than a second request.
        var clientPaused = payload?.Queue?.Paused;
        var clientVersion = await GetVersionAsync(http, baseUri, apiKey, cancellationToken);

        // #1142: SABnzbd with no news server cannot download anything, yet answers every call happily.
        if (health == "healthy" && await ReadHasProviderAsync(http, baseUri, apiKey, cancellationToken) == false)
        {
            health = "degraded";
            message = UsenetProviderText.NoProvider;
            // A client with no news server shows that, never "Paused".
            clientPaused = false;
        }

        // #813: SABnzbd's own queue-level warnings (disk-full, a failed
        // unpack, and the like). have_warnings is a non-zero count string
        // once SABnzbd has one waiting; only fetched then, so a client with
        // nothing to say costs no extra call. Folded into the same
        // health/message a low-disk reading uses (#663) rather than a
        // separate alert: it clears itself the moment the warning is cleared
        // at SABnzbd (or a fresh one replaces it), the same as disk space
        // freeing up, with no ignore-list bookkeeping needed on Deluno's side.
        string[] warnings = [];
        if (!string.IsNullOrWhiteSpace(payload?.Queue?.HaveWarnings) && payload.Queue.HaveWarnings != "0")
        {
            warnings = await GetWarningsAsync(http, baseUri, apiKey, cancellationToken);
            if (warnings.Length > 0)
            {
                var warningText = $"SABnzbd reported: {string.Join(" ", warnings)}";
                message = health == "degraded" ? $"{message} {warningText}" : warningText;
                health = "degraded";
            }
        }

        return CreateSnapshot(
            client, queue, capturedUtc, health, message, history.Count > 0 ? history : null,
            freeSpaceBytes: freeSpaceBytes, clientPaused: clientPaused, clientVersion: clientVersion,
            clientWarnings: warnings.Length > 0 ? warnings : null);
    }

    public override async Task<bool?> HasUsenetProviderAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(client.Secret)) return null;
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        return await ReadHasProviderAsync(http, baseUri, client.Secret, cancellationToken);
    }

    /// <summary>#1142: writes the person's news server into SABnzbd (mode=set_config, section=servers), then reads it back.</summary>
    public override async Task<UsenetProviderClientResult?> SetUsenetProviderAsync(DownloadClientItem client, UsenetProviderRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(client.Secret))
        {
            return new(client.Id, client.Name, false, $"Deluno does not have {client.Name}'s address or API key.");
        }

        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(10);
        var query = string.Join('&', new Dictionary<string, string>
        {
            ["mode"] = "set_config",
            ["section"] = "servers",
            ["keyword"] = UsenetProviderText.ServerName,
            ["displayname"] = UsenetProviderText.ServerName,
            ["host"] = request.Host.Trim(),
            ["port"] = request.Port.ToString(CultureInfo.InvariantCulture),
            ["username"] = request.Username?.Trim() ?? string.Empty,
            ["password"] = request.Password ?? string.Empty,
            ["connections"] = request.Connections.ToString(CultureInfo.InvariantCulture),
            ["ssl"] = request.UseSsl ? "1" : "0",
            ["enable"] = "1",
            ["output"] = "json",
            ["apikey"] = client.Secret
        }.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        try
        {
            using var response = await http.GetAsync(new Uri(baseUri, $"api?{query}"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(client.Id, client.Name, false, $"{client.Name} did not accept the provider (it answered {(int)response.StatusCode}).");
            }

            // SABnzbd can take a moment to show a server it was just given, so the read-back asks again for a bounded time.
            bool? after = null;
            for (var attempt = 0; attempt < 10 && after != true; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(_readBackDelay, cancellationToken);
                }

                after = await ReadHasProviderAsync(http, baseUri, client.Secret, cancellationToken);
            }

            return after == true
                ? new(client.Id, client.Name, true, $"{client.Name} now uses {request.Host.Trim()}.")
                : new(client.Id, client.Name, false, $"{client.Name} did not keep the provider. Check the address and try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new(client.Id, client.Name, false, $"Deluno could not reach {client.Name} to give it the provider.");
        }
    }

    /// <summary>Whether SABnzbd has a working news server, or null when it could not be asked.</summary>
    private static async Task<bool?> ReadHasProviderAsync(HttpClient http, Uri baseUri, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=get_config&section=servers&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return UsenetProviderCheck.SabnzbdHasProvider(document.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>#813: clears SABnzbd's own queue-level warnings so they do not reappear once handled.</summary>
    public override async Task<bool> ClearWarningsAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(client.Secret)) return false;
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        var outcome = await SendAsync(http, baseUri, $"api?mode=warnings&name=clear&output=json&apikey={Uri.EscapeDataString(client.Secret)}", cancellationToken);
        // Clearing may answer with the (now empty) warnings list rather than a status; only a stated "no" is a failure.
        return outcome.Reached && outcome.StatusCode is { } code && (int)code is >= 200 and < 300 && (outcome.Accepted || !outcome.Readable);
    }

    /// <summary>Resumes SABnzbd as a whole (mode=resume), which is what its queue-level pause (queue.paused) needs.</summary>
    public override async Task<ClientResumeResult> ResumeClientAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(client.Secret)) return new ClientResumeResult(true, false, "SABnzbd's address or API key is missing.");
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        using var response = await http.GetAsync(
            new Uri(baseUri, $"api?mode=resume&output=json&apikey={Uri.EscapeDataString(client.Secret)}"),
            cancellationToken);
        if (!response.IsSuccessStatusCode) return new ClientResumeResult(true, false, $"SABnzbd answered {(int)response.StatusCode} when asked to resume.");
        // SABnzbd answers {"status": true} or {"status": false, "error": ...}.
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.False)
            {
                var reason = document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                return new ClientResumeResult(true, false, $"SABnzbd would not resume: {reason ?? "no reason given"}.");
            }
        }
        catch (JsonException)
        {
            // A reply that is not JSON but is HTTP 200 is taken as done, as the warnings call does.
        }

        return new ClientResumeResult(true, true, "SABnzbd is downloading again.");
    }

    // #813: mode=warnings returns the active warnings as an array of plain
    // strings (or objects on some versions); either shape is read as text.
    private static async Task<string[]> GetWarningsAsync(HttpClient http, Uri baseUri, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=warnings&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
            if (!response.IsSuccessStatusCode) return [];
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return warnings.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.String
                    ? entry.GetString()
                    : entry.TryGetProperty("text", out var text) ? text.GetString() : null)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    public override async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(client.Secret)) return [];
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        return await GetHistoryCoreAsync(http, client, baseUri, client.Secret, capturedUtc, cancellationToken);
    }

    /// <summary>
    /// SABnzbd's own <c>mode=get_files&amp;value=&lt;nzo_id&gt;</c> (#816):
    /// every file SABnzbd still holds for that job, by its own filename. Only
    /// meaningful while the job is still queued or downloading - like NZBGet,
    /// SABnzbd has already written the finished files to disk by the time a
    /// job reaches history, where the import step reads them directly. Null
    /// when the address, API key or queue item id is missing, or the call
    /// itself fails.
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        var apiKey = client.Secret ?? client.Username;
        if (baseUri is null || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(queueItemId)) return null;

        try
        {
            var http = httpClientFactory.CreateClient("download-clients");
            http.Timeout = TimeSpan.FromSeconds(8);
            var uri = new Uri(baseUri, $"api?mode=get_files&value={Uri.EscapeDataString(queueItemId)}&output=json&apikey={Uri.EscapeDataString(apiKey)}");
            var payload = await http.GetFromJsonAsync<SabFilesResponse>(uri, cancellationToken);
            return payload?.Files
                ?.Select(file => file.Filename)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public override async Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = category.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCategory))
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                "Enter a category before checking it.", Supported: true, Found: false);
        }

        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        var apiKey = client.Secret ?? client.Username;
        if (baseUri is null || string.IsNullOrWhiteSpace(apiKey))
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                "Add the SABnzbd address and API key before checking a category.", Supported: true, Found: false);
        }

        try
        {
            var http = httpClientFactory.CreateClient("download-clients");
            http.Timeout = TimeSpan.FromSeconds(8);
            // get_config rather than get_cats: get_cats returns names only, and
            // a name existing is not what makes a download land where Deluno is
            // watching. This one carries each category's folder.
            using var response = await http.GetAsync(
                new Uri(baseUri, $"api?mode=get_config&output=json&apikey={Uri.EscapeDataString(apiKey)}"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unreachable,
                    $"SABnzbd returned {(int)response.StatusCode} while checking its categories.", Supported: true, Found: false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var entry = ReadCategoryEntries(document.RootElement)
                .FirstOrDefault(item => string.Equals(item.Name, normalizedCategory, StringComparison.OrdinalIgnoreCase));
            var found = entry.Name is not null;

            if (found)
            {
                // A category's folder is usually relative ("Movies"): SABnzbd
                // puts it inside its completed folder. The full path is the one
                // that has to match the library's (#616).
                var completeDir = ReadCompleteDir(document.RootElement);
                // SABnzbd's temporary download folder, where a job is unpacked while it runs (#1164).
                var incompleteDir = ReadMiscFolder(document.RootElement, "download_dir");
                var folder = ClientPaths.ClientFolder(completeDir, entry.Directory);
                return string.IsNullOrWhiteSpace(entry.Directory)
                    ? new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                        $"SABnzbd has a category named {normalizedCategory}, but it has no folder, so its downloads go straight into SABnzbd's completed folder{(folder is null ? string.Empty : $" ({folder})")} with everything else. Set a folder on the category in SABnzbd.",
                        Supported: true, Found: true, SavePath: folder, IncompletePath: incompleteDir)
                    : new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Ready,
                        $"SABnzbd has a category named {normalizedCategory}, saving to {folder ?? entry.Directory}.",
                        Supported: true, Found: true, SavePath: folder ?? entry.Directory, IncompletePath: incompleteDir);
            }
            return new(
                client.Id,
                client.Name,
                normalizedCategory,
                found ? DownloadClientCategoryStatuses.Ready : DownloadClientCategoryStatuses.Missing,
                found
                    ? $"SABnzbd has a category named {normalizedCategory}."
                    : $"SABnzbd does not have a category named {normalizedCategory}. Create it there before using it in Deluno.",
                Supported: true,
                Found: found);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not reach SABnzbd to check its categories: {ex.Message}", Supported: true, Found: false);
        }
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        if (string.IsNullOrWhiteSpace(client.Secret))
        {
            return DownloadClientHelpers.ActionFailure(
                client,
                queueItemId,
                action,
                "SABnzbd API key is missing.",
                upstreamDetail: "The client configuration has no API key.",
                category: "configuration");
        }
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);

        if (string.Equals(action, DownloadClientActions.Forget, StringComparison.OrdinalIgnoreCase))
        {
            return await ForgetAsync(http, client, baseUri, client.Secret, queueItemId, cancellationToken);
        }

        // One arm per verb, and `del_files` is the whole of the difference
        // between two of them. `delete-with-data` was absent entirely until
        // now, which is how a forced re-download against a usenet client came
        // to do nothing while reporting success: the override asked for it, and
        // SABnzbd answered "does not support this action".
        //
        // `forget` is handled above rather than here, because on SABnzbd it is
        // genuinely a different request and not a flag on this one — its
        // duplicate detection reads the history, which outlives the queue.
        var mode = action switch
        {
            DownloadClientActions.Pause => "queue&name=pause",
            DownloadClientActions.Resume => "queue&name=resume",
            DownloadClientActions.Delete => "queue&name=delete",
            DownloadClientActions.DeleteWithData => "queue&name=delete&del_files=1",
            // #810: SABnzbd has no move-up/move-down of its own -- only the
            // discrete priority tiers below, set with mode=queue&name=priority.
            // Move up/down map onto the nearest tier in that direction rather
            // than a true one-slot move; move-to-bottom lands on the same Low
            // tier move-down does, since there is nothing lower short of
            // Paused, which would stop the download rather than reorder it.
            DownloadClientActions.MoveToTop => "queue&name=priority",
            DownloadClientActions.MoveUp => "queue&name=priority",
            DownloadClientActions.MoveDown => "queue&name=priority",
            DownloadClientActions.MoveToBottom => "queue&name=priority",
            // SABnzbd verifies its own downloads and exposes no per-item
            // recheck, so that verb is refused rather than quietly mapped onto
            // something adjacent.
            _ => null
        };
        if (mode is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "SABnzbd");
        var priorityValue = action switch
        {
            DownloadClientActions.MoveToTop => "&value2=2",
            DownloadClientActions.MoveUp => "&value2=1",
            DownloadClientActions.MoveDown => "&value2=-2",
            DownloadClientActions.MoveToBottom => "&value2=-2",
            _ => string.Empty
        };
        var itemId = Uri.EscapeDataString(queueItemId);
        var key = Uri.EscapeDataString(client.Secret);

        // A finished or failed job is in SABnzbd's history, not its queue, so a
        // queue delete finds nothing there (#1201). Try the queue first and, when
        // it removed nothing, the history, which is where the job and its folder
        // live once it is done.
        if (action is DownloadClientActions.Delete or DownloadClientActions.DeleteWithData)
        {
            var withFiles = action == DownloadClientActions.DeleteWithData;
            var fromQueue = await SendAsync(http, baseUri, $"api?mode={mode}&value={itemId}&apikey={key}&output=json", cancellationToken);
            if (fromQueue.Removed)
            {
                return DownloadClientHelpers.ActionSuccess(client, queueItemId, action,
                    withFiles ? "Removed this download from SABnzbd's queue, along with its files." : "Removed this download from SABnzbd's queue.");
            }

            var fromHistory = await SendAsync(http, baseUri,
                $"api?mode=history&name=delete&value={itemId}&del_files={(withFiles ? 1 : 0)}&apikey={key}&output=json", cancellationToken);
            if (fromHistory.Removed)
            {
                return DownloadClientHelpers.ActionSuccess(client, queueItemId, action,
                    withFiles ? "Removed this finished download from SABnzbd's history, along with its files." : "Removed this finished download from SABnzbd's history.");
            }

            return DidNotWork(client, queueItemId, action, fromHistory.Reached ? fromHistory : fromQueue, "had no download with that id to remove");
        }

        var sent = await SendAsync(http, baseUri, $"api?mode={mode}&value={itemId}{priorityValue}&apikey={key}&output=json", cancellationToken);
        return sent.Accepted
            ? DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "SABnzbd action sent.")
            : DidNotWork(client, queueItemId, action, sent, "did not accept it");
    }

    /// <summary>
    /// A failure in plain words for an answer that was not a yes (#1201). SABnzbd
    /// answers 200 even when it did nothing, so the JSON status is what counts.
    /// </summary>
    private static DownloadClientActionResult DidNotWork(
        DownloadClientItem client, string queueItemId, string action, SabActionOutcome outcome, string noReason)
    {
        if (!outcome.Reached)
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                "SABnzbd could not be reached, so nothing was changed.", category: "unreachable");
        }

        if (outcome.StatusCode is { } code && (int)code is < 200 or >= 300)
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, action, $"SABnzbd returned {(int)code}.", code);
        }

        var message = outcome.Readable
            ? $"SABnzbd {noReason}{(string.IsNullOrWhiteSpace(outcome.Error) ? string.Empty : $": {outcome.Error}")}. Nothing was changed."
            : "SABnzbd's answer could not be read, so Deluno cannot tell that it did anything.";
        return DownloadClientHelpers.ActionFailure(client, queueItemId, action, message,
            outcome.StatusCode, upstreamDetail: outcome.Error);
    }

    /// <summary>
    /// Remove this download from SABnzbd and from its memory of having had it.
    ///
    /// <para><b>The history is the half that matters.</b> SABnzbd's duplicate
    /// detection reads its history, not its queue, so emptying the queue leaves
    /// the record that refuses the same release next time. A force that only
    /// cleared the queue would report success and change nothing — which is a
    /// worse failure than doing nothing, because it is a confident one.</para>
    ///
    /// <para>Both calls are made because the item is in one place or the other
    /// and Deluno does not know which: a download interrupted half way is in
    /// the queue, and one that completed and was then deleted from disk is in
    /// the history. Neither call failing to find it is an error.</para>
    /// </summary>
    private static async Task<DownloadClientActionResult> ForgetAsync(
        HttpClient http,
        DownloadClientItem client,
        Uri baseUri,
        string apiKey,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        // SABnzbd's history delete takes an nzo id — and also the words "all"
        // and "failed", which mean "empty the history". Deluno only ever passes
        // an id it read from its own dispatch record, so this cannot happen
        // today; it is here because the cost of being wrong once is somebody's
        // entire download history, and the check is one comparison.
        if (DangerousHistorySelectors.Contains(queueItemId))
        {
            return DownloadClientHelpers.ActionFailure(
                client,
                queueItemId,
                DownloadClientActions.Forget,
                $"\"{queueItemId}\" is not a download id — to SABnzbd it means the whole history, so Deluno will not send it.",
                upstreamDetail: "Refused before the request was made.",
                category: "configuration");
        }

        var id = Uri.EscapeDataString(queueItemId);
        var key = Uri.EscapeDataString(apiKey);

        var queueRemoved = await SendAsync(http, baseUri, $"api?mode=queue&name=delete&value={id}&del_files=1&apikey={key}&output=json", cancellationToken);
        var historyRemoved = await SendAsync(http, baseUri, $"api?mode=history&name=delete&value={id}&del_files=1&apikey={key}&output=json", cancellationToken);

        if (historyRemoved.Reached || queueRemoved.Reached)
        {
            var what = (queueRemoved.Accepted, historyRemoved.Accepted) switch
            {
                (true, true) => "Removed the download and its history entry from SABnzbd.",
                (false, true) => "Removed SABnzbd's history entry, so it will accept this release again.",
                (true, false) => "Removed the download from SABnzbd's queue. It had no history entry for it.",
                _ => "SABnzbd had no record of this release to remove."
            };

            return DownloadClientHelpers.ActionSuccess(client, queueItemId, DownloadClientActions.Forget, what);
        }

        return DownloadClientHelpers.ActionFailure(
            client,
            queueItemId,
            DownloadClientActions.Forget,
            "SABnzbd could not be reached to forget this release.",
            historyRemoved.StatusCode ?? queueRemoved.StatusCode);
    }

    /// <summary>
    /// Values SABnzbd reads as "everything" rather than as one download.
    /// </summary>
    private static readonly HashSet<string> DangerousHistorySelectors =
        new(StringComparer.OrdinalIgnoreCase) { "all", "failed", "completed" };

    /// <summary>
    /// Whether SABnzbd answered at all, and whether it said yes.
    ///
    /// <para>It replies <c>200</c> with <c>{"status": false}</c> when it has
    /// nothing matching the id, so the HTTP code alone cannot tell "gone" from
    /// "never there" — and both are fine for a forget.</para>
    /// </summary>
    private static async Task<SabActionOutcome> SendAsync(HttpClient http, Uri baseUri, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(baseUri, path), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SabActionOutcome(Reached: true, Accepted: false, response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var answer = ReadAnswer(body);
            return new SabActionOutcome(Reached: true, answer.Status == true, response.StatusCode)
            {
                Readable = answer.Status is not null,
                Error = answer.Error,
                NoneListed = answer.NzoIdsEmpty
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SabActionOutcome(Reached: false, Accepted: false, null);
        }
    }

    private readonly record struct SabActionOutcome(bool Reached, bool Accepted, HttpStatusCode? StatusCode)
    {
        /// <summary>SABnzbd's reply was JSON with a status in it.</summary>
        public bool Readable { get; init; }

        public string? Error { get; init; }

        /// <summary>SABnzbd listed the ids it removed and the list was empty.</summary>
        public bool NoneListed { get; init; }

        /// <summary>Said yes, and (when it says which jobs it removed) removed at least one.</summary>
        public bool Removed => Accepted && !NoneListed;
    }

    private readonly record struct SabAnswer(bool? Status, string? Error, bool NzoIdsEmpty);

    /// <summary>
    /// Reads SABnzbd's <c>{"status": true|false, "error": ..., "nzo_ids": [...]}</c>.
    /// A status of null means the reply was not that shape.
    /// </summary>
    private static SabAnswer ReadAnswer(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SabAnswer(null, null, false);
            }

            bool? status = root.TryGetProperty("status", out var s)
                ? s.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var idsEmpty = root.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 0;
            return new SabAnswer(status, error, idsEmpty);
        }
        catch (JsonException)
        {
            return new SabAnswer(null, null, false);
        }
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        var normalized = nativeStatus?.ToLowerInvariant() ?? string.Empty;
        // Verifying/Repairing/Extracting/Moving/Running (#663) — checked ahead
        // of the progress-based "complete" test below, which would otherwise
        // misread one: SABnzbd's transfer is done and progress already reads
        // 100% while these run, so without this a slot stuck repairing for
        // hours looked identical to one actually finished. Past the transfer,
        // short of the library — the same "processing" NZBGet's post-processing
        // stages map to and Deluno's own cleanup step already uses.
        if (IsPostProcessingStage(normalized)) return DownloadQueueStatuses.Processing;
        if (Deluno.Platform.Contracts.DownloadHealthStrikes.IsFinishedPercent(progress) || normalized.Contains("complete")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queued")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("fail") || normalized.Contains("error")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }

    /// <summary>SABnzbd queue slot statuses distinct from Downloading (#663).</summary>
    private static bool IsPostProcessingStage(string normalized) =>
        normalized.Contains("verifying") || normalized.Contains("repairing") ||
        normalized.Contains("extracting") || normalized.Contains("moving") || normalized.Contains("running");

    /// <summary>
    /// SABnzbd's disk-space fields, a bare decimal number of GB with no unit
    /// suffix — unlike <see cref="ParseSize"/>, which reads MB-with-suffix
    /// strings such as "512.3 MB" and would misread this by a factor of 1000.
    /// </summary>
    private static long? ParseGbString(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb > 0
            ? Convert.ToInt64(gb * 1_000_000_000d)
            : null;

    // #812: mode=version is its own call, separate from queue/status. A
    // failure here should never take down the whole snapshot — an older or
    // momentarily unreachable SABnzbd still has a queue worth showing.
    private static async Task<string?> GetVersionAsync(HttpClient http, Uri baseUri, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=version&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await response.Content.ReadFromJsonAsync<SabVersionResponse>(cancellationToken);
            return string.IsNullOrWhiteSpace(payload?.Version) ? null : payload.Version;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private sealed record SabVersionResponse([property: JsonPropertyName("version")] string? Version);

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryCoreAsync(HttpClient http, DownloadClientItem client, Uri baseUri, string apiKey, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=history&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SabHistoryResponse>(cancellationToken);
        return (payload?.History?.Slots ?? []).Select(item =>
        {
            var outcome = NormalizeHistoryOutcome(item.Status ?? string.Empty, item.FailMessage);
            var errorMessage = outcome switch
            {
                DownloadClientHistoryOutcomes.Duplicate => DuplicateReason(item.FailMessage),
                DownloadClientHistoryOutcomes.NeedsAttention => AttentionReason(item.FailMessage),
                DownloadClientHistoryOutcomes.ScriptFailed or DownloadClientHistoryOutcomes.ScriptWarning
                    => ScriptReason(item.Script, isFailure: outcome == DownloadClientHistoryOutcomes.ScriptFailed),
                "failed" => FailureReason(item.FailMessage),
                _ => string.IsNullOrWhiteSpace(item.FailMessage) ? QueueError(item.Status) : item.FailMessage
            };
            return new DownloadClientHistoryItem(
                item.NzoId ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown SABnzbd history item"),
                item.Name ?? "Unknown SABnzbd history item", item.Category ?? string.Empty, outcome, "SABnzbd",
                ParseHistorySize(item.Bytes), item.Completed is > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.Completed.Value) : capturedUtc,
                errorMessage, item.Storage,
                HistorySource: "native",
                ExternalId: item.NzoId);
        }).ToArray();
    }

    private static long ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var cleaned = value.Trim().Replace("/s", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        var multiplier = cleaned.EndsWith("GB", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith("G", StringComparison.OrdinalIgnoreCase) ? 1000d
            : cleaned.EndsWith("KB", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith("K", StringComparison.OrdinalIgnoreCase) ? .001d
            : cleaned.EndsWith("B", StringComparison.OrdinalIgnoreCase) && !cleaned.EndsWith("MB", StringComparison.OrdinalIgnoreCase) && !cleaned.EndsWith("GB", StringComparison.OrdinalIgnoreCase) && !cleaned.EndsWith("KB", StringComparison.OrdinalIgnoreCase) ? .000001d : 1d;
        cleaned = cleaned.Replace("GB", "", StringComparison.OrdinalIgnoreCase).Replace("MB", "", StringComparison.OrdinalIgnoreCase).Replace("KB", "", StringComparison.OrdinalIgnoreCase).Replace("G", "", StringComparison.OrdinalIgnoreCase).Replace("M", "", StringComparison.OrdinalIgnoreCase).Replace("K", "", StringComparison.OrdinalIgnoreCase).Replace("B", "", StringComparison.OrdinalIgnoreCase);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? Convert.ToInt64(parsed * multiplier) : 0;
    }

    private static int ParseEta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split(':').Select(part => int.TryParse(part, out var parsed) ? parsed : 0).ToArray();
        return parts.Length switch { 3 => parts[0] * 3600 + parts[1] * 60 + parts[2], 2 => parts[0] * 60 + parts[1], _ => 0 };
    }

    private static long ParseHistorySize(JsonElement? value) => value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var parsed) ? parsed : value is not null && long.TryParse(value.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    // #880/#881: SABnzbd has no per-item signal for a local setup problem
    // while a slot is still in the live queue — a full disk shows up as a
    // client-wide diskspace1 warning (EvaluateDiskSpaceHealth, which already
    // never strikes an item), and a slot that actually fails repair or
    // extraction moves to history rather than staying in the queue with this
    // status. So a queue-level QueueError here stays a release-side finding,
    // same as before.
    private static string? QueueError(string? status) => string.IsNullOrWhiteSpace(status) ? null : status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("stall", StringComparison.OrdinalIgnoreCase) ? status : null;

    /// <summary>
    /// SABnzbd's history status has no MAIN/DETAIL split the way NZBGet's
    /// does (#663, #884, #886): "Failed" alone covers a duplicate SABnzbd's own
    /// detection declined, a full disk during unpacking, and a genuinely bad
    /// release, and the only place SABnzbd says which is the free-text
    /// <c>fail_message</c>.
    ///
    /// <para><b>Duplicate (#886).</b> With "Detect Duplicate Downloads" set to
    /// "Fail job (move to History)", SABnzbd's <c>NzbQueue.handle_duplicate_alternatives</c>
    /// sets <c>fail_msg</c> to exactly "Duplicate NZB" before moving the job to
    /// history as failed — read here the same way NZBGet's DELETED/DUPE is: not
    /// a failure, so nothing is blocklisted or searched for again.</para>
    ///
    /// <para><b>Disk full (#884).</b> SABnzbd's unrar, 7-Zip and par2 paths each
    /// set <c>fail_msg</c> to their own wording when the disk fills mid-unpack
    /// ("Unpacking failed, disk full", "Unpacking failed, write error or disk
    /// is full?", "Repairing failed, Disk full") — read as
    /// <see cref="DownloadClientHistoryOutcomes.NeedsAttention"/>, parity with
    /// NZBGet's WARNING/SPACE: the release is not lost, so nothing is
    /// blocklisted or searched for either.</para>
    ///
    /// <para><b>A post-processing script's own failure or warning is not a bad
    /// release either (#910).</b> SABnzbd runs the user's configured script
    /// after the download and unpack finish, and reports what went wrong in
    /// the same free-text <c>fail_msg</c> — "Script was aborted, please check
    /// script_log for more information." for a hard failure that moves the job
    /// to a Failed status, or a milder note left on an otherwise Completed job
    /// for a script that merely warned. Mirroring NZBGet's FAILURE/SCRIPT and
    /// WARNING/SCRIPT (<see cref="NzbGetDownloadClient.NormalizeOutcome"/>):
    /// read as <see cref="DownloadClientHistoryOutcomes.ScriptFailed"/> or
    /// <see cref="DownloadClientHistoryOutcomes.ScriptWarning"/>, so neither
    /// blocklists or searches again, and both still hand the finished file to
    /// the ordinary import path to be probed.</para>
    /// </summary>
    internal static string NormalizeHistoryOutcome(string status, string? failMessage)
    {
        if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            // A script warning does not fail the job — SABnzbd leaves it
            // Completed and only notes the warning in fail_msg — so it has to
            // be read here, before the generic success case wins.
            return IsScriptProblem(failMessage) ? DownloadClientHistoryOutcomes.ScriptWarning : DownloadQueueStatuses.Completed;
        }

        if (status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            if (IsDuplicateFailure(failMessage)) return DownloadClientHistoryOutcomes.Duplicate;
            if (IsDiskFullFailure(failMessage)) return DownloadClientHistoryOutcomes.NeedsAttention;
            if (IsScriptProblem(failMessage)) return DownloadClientHistoryOutcomes.ScriptFailed;
            return "failed";
        }

        if (status.Contains("import", StringComparison.OrdinalIgnoreCase)) return DownloadQueueStatuses.ImportReady;
        return status.Length == 0 ? "unknown" : status.Trim().ToLowerInvariant();
    }

    internal static bool IsDuplicateFailure(string? failMessage)
        => !string.IsNullOrWhiteSpace(failMessage) && failMessage.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDiskFullFailure(string? failMessage)
        => !string.IsNullOrWhiteSpace(failMessage) &&
           (failMessage.Contains("disk full", StringComparison.OrdinalIgnoreCase) ||
            failMessage.Contains("disk is full", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether SABnzbd's own free-text failure message is about its post-processing script rather than the release (#910).</summary>
    internal static bool IsScriptProblem(string? failMessage)
        => !string.IsNullOrWhiteSpace(failMessage) && failMessage.Contains("script", StringComparison.OrdinalIgnoreCase);

    internal static string FailureReason(string? failMessage)
        => string.IsNullOrWhiteSpace(failMessage) ? "SABnzbd could not download it." : $"SABnzbd could not download it: {failMessage}.";

    /// <summary>
    /// The words for a <see cref="DownloadClientHistoryOutcomes.ScriptFailed"/>
    /// or <see cref="DownloadClientHistoryOutcomes.ScriptWarning"/> row (#910),
    /// naming the script SABnzbd itself names in the history slot's
    /// <c>script</c> field when it has one.
    /// </summary>
    internal static string ScriptReason(string? scriptName, bool isFailure)
        => !string.IsNullOrWhiteSpace(scriptName) && !scriptName.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? $"Your client's script \"{scriptName}\" {(isFailure ? "failed" : "warned")}."
            : "Your client's post-processing script reported a problem.";

    /// <summary>The words for a <see cref="DownloadClientHistoryOutcomes.Duplicate"/> row (#886) — deliberately not "failed", which reads as something went wrong.</summary>
    internal static string DuplicateReason(string? failMessage)
        => $"SABnzbd already has this download: {(string.IsNullOrWhiteSpace(failMessage) ? "it was a duplicate." : failMessage + ".")}";

    /// <summary>The words for a <see cref="DownloadClientHistoryOutcomes.NeedsAttention"/> row (#884) — the download is not lost, but a person has to act before it can finish.</summary>
    internal static string AttentionReason(string? failMessage)
        => $"SABnzbd finished downloading this, but ran out of disk space during unpacking: {(string.IsNullOrWhiteSpace(failMessage) ? "free space, then retry it in SABnzbd." : failMessage + " Free space, then retry it in SABnzbd.")}";

    /// <summary>
    /// Each configured category with the folder it writes to, from
    /// <c>mode=get_config&amp;section=categories</c>.
    /// </summary>
    private static string? ReadMiscFolder(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("config", out var config)
           && config.TryGetProperty("misc", out var misc)
           && misc.TryGetProperty(name, out var dir)
           && dir.ValueKind == JsonValueKind.String
            ? dir.GetString()
            : null;

    private static string? ReadCompleteDir(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("config", out var config)
           && config.TryGetProperty("misc", out var misc)
           && misc.TryGetProperty("complete_dir", out var dir)
           && dir.ValueKind == JsonValueKind.String
            ? dir.GetString()
            : null;

    private static IEnumerable<(string? Name, string? Directory)> ReadCategoryEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("config", out var config) ||
            !config.TryGetProperty("categories", out var categories) ||
            categories.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var category in categories.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = category.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : null;
            var directory = category.TryGetProperty("dir", out var dirValue) && dirValue.ValueKind == JsonValueKind.String
                ? dirValue.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return (name, directory);
            }
        }
    }

    private static IReadOnlyList<string> ReadCategoryNames(JsonElement root)
    {
        if (!root.TryGetProperty("categories", out var categories))
        {
            return [];
        }

        return categories.ValueKind switch
        {
            JsonValueKind.Array => categories.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray(),
            JsonValueKind.Object => categories.EnumerateObject().Select(item => item.Name).ToArray(),
            _ => []
        };
    }

    private sealed record SabFilesResponse([property: JsonPropertyName("files")] IReadOnlyList<SabFile>? Files);
    private sealed record SabFile([property: JsonPropertyName("filename")] string? Filename);
    private sealed record SabQueueResponse([property: JsonPropertyName("queue")] SabQueue? Queue);
    // diskspace1/diskspacetotal1 (#663): the "complete" download folder's free
    // and total space, in GB as SABnzbd's own decimal string — the field the
    // health check never read, so a client filling its disk stayed "healthy"
    // right up until every download in the queue started failing.
    private sealed record SabQueue(
        [property: JsonPropertyName("speed")] string? Speed,
        [property: JsonPropertyName("slots")] IReadOnlyList<SabSlot>? Slots,
        [property: JsonPropertyName("diskspace1")] string? DiskSpace1,
        [property: JsonPropertyName("diskspacetotal1")] string? DiskSpaceTotal1,
        // #812: SABnzbd's own global pause flag.
        [property: JsonPropertyName("paused")] bool? Paused,
        // #813: set once SABnzbd has a queue-level warning waiting.
        [property: JsonPropertyName("have_warnings")] string? HaveWarnings);
    private sealed record SabSlot([property: JsonPropertyName("nzo_id")] string? NzoId, [property: JsonPropertyName("filename")] string? Filename, [property: JsonPropertyName("cat")] string? Category, [property: JsonPropertyName("status")] string? Status, [property: JsonPropertyName("mb")] string? Mb, [property: JsonPropertyName("mbleft")] string? Mbleft, [property: JsonPropertyName("timeleft")] string? TimeLeft);
    private sealed record SabHistoryResponse([property: JsonPropertyName("history")] SabHistory? History);
    private sealed record SabHistory([property: JsonPropertyName("slots")] IReadOnlyList<SabHistorySlot>? Slots);
    private sealed record SabHistorySlot([property: JsonPropertyName("nzo_id")] string? NzoId, [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("category")] string? Category, [property: JsonPropertyName("status")] string? Status, [property: JsonPropertyName("bytes")] JsonElement? Bytes, [property: JsonPropertyName("completed")] long? Completed, [property: JsonPropertyName("fail_message")] string? FailMessage, [property: JsonPropertyName("storage")] string? Storage, [property: JsonPropertyName("script")] string? Script);
}
