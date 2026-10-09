using Deluno.Connections.Usenet;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class NzbGetDownloadClient(IHttpClientFactory httpClientFactory, TimeSpan? readBackDelay = null) : DownloadClientBase
{
    private readonly TimeSpan _readBackDelay = readBackDelay ?? TimeSpan.FromMilliseconds(500);

    public override string Protocol => "nzbget";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, false, true, "basic", SupportsQueueReorder: true);

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var http = CreateHttp(client);
        // append, not appendurl (#633). NZBGet 26 refuses appendurl as called
        // here ("Invalid parameter (DupeKey)") with HTTP 200, and the reply was
        // never read, so every grab was reported sent and none arrived. append
        // takes the URL as its content, as Sonarr and Radarr send it, and
        // answers with the new NZBID.
        var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<long?>>(
            http,
            new Uri(baseUri, "jsonrpc"),
            new NzbGetRequest("append", [
                request.ReleaseName + ".nzb",
                request.DownloadUrl,
                DownloadClientHelpers.ResolveCategory(client, request) ?? string.Empty,
                // #663: this used to be hard-coded to 0 (normal), so a release
                // that should have jumped the queue — or stayed out of the way
                // of one that should — got neither. NZBGet's own scale: negative
                // is low, 0 normal, positive high.
                request.Priority ?? 0,
                false,
                client.AddPaused,
                string.Empty,
                0,
                "SCORE",
                // #663: NZBGet has no direct "password" parameter on append; a
                // protected archive's password is set the same way a person sets
                // it by hand, as a post-processing parameter override for the
                // built-in Unpack script. Sent only when Deluno actually has one
                // — never fabricated.
                string.IsNullOrWhiteSpace(request.Password)
                    ? Array.Empty<object>()
                    : new object[] { new NzbGetPPParameter("*Unpack:Password", request.Password) }]),
            cancellationToken);
        if (response?.Error is { } error)
        {
            // #876: NZBGet answered the same request with an explicit reason
            // it would not take the release — a dead link or an NZB it could
            // not parse, not a setup problem — so the caller blocklists it and
            // lets the next search pick a different one.
            return DownloadClientHelpers.DeadReleaseGrabFailure(client, request, $"NZBGet refused the release: {error.Message ?? "no reason given"}.");
        }

        if (response?.Result is not > 0)
        {
            return DownloadClientHelpers.GrabFailure(client, request, "NZBGet did not add the release.");
        }

        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to NZBGet.") with
        {
            ExternalId = response.Result.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
        var http = CreateHttp(client);
        var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetQueueItem>>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("listgroups", []), cancellationToken);
        var status = await GetStatusAsync(http, baseUri, cancellationToken);
        var queue = (response?.Result ?? []).Select(item =>
        {
            var size = item.FileSizeHi * 1_000_000L;
            var remaining = item.RemainingSizeHi * 1_000_000L;
            var downloaded = Math.Max(0, size - remaining);
            var progress = size <= 0 ? 0 : (downloaded / (double)size * 100);
            return new DownloadQueueItem(item.NzbId.ToString(CultureInfo.InvariantCulture), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.NzbName ?? "Unknown NZBGet item"), item.NzbName ?? "Unknown NZBGet item",
                item.Category ?? string.Empty, NormalizeStatus(item.Status, progress), progress, ((status?.DownloadRate ?? 0) / 1_000_000d),
                CalculateEta(remaining, status?.DownloadRate), size, downloaded, 0, "NZBGet", QueueError(item.Status), capturedUtc, JobFolder(item.DestDir));
        }).ToArray();
        var history = await GetHistoryCoreAsync(http, client, baseUri, capturedUtc, cancellationToken);
        var freeSpaceBytes = status?.FreeDiskSpaceMB is > 0 ? status.FreeDiskSpaceMB.Value * 1_000_000L : (long?)null;
        // #1060: NZBGet reports 0 (FreeDiskSpaceLo/Hi all ones) while its
        // DestDir does not exist, which it only creates on the first download.
        // When it runs on this machine, measure the nearest folder on that
        // path that does exist instead of leaving Health "unknown".
        if (freeSpaceBytes is null && ClientSaveFolders.IsSameMachine(client))
        {
            freeSpaceBytes = DownloadClientHelpers.FreeSpaceOfNearestExistingFolder(await GetDestDirAsync(http, baseUri, cancellationToken));
        }
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to NZBGet at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            null);
        // #812: DownloadPaused/PostPaused/ScanPaused are the client's own
        // "someone paused this" flags. ServerStandBy alone just means nothing
        // is downloading right now, which the empty queue already shows -- it
        // is not treated as Paused here so an idle-but-running client is not
        // mislabelled as paused.
        var clientPaused = status is { } value && (value.DownloadPaused == true || value.PostPaused == true || value.ScanPaused == true);
        var clientVersion = await GetVersionAsync(http, baseUri, cancellationToken);
        // #1142: NZBGet ships a placeholder server and cannot download until a real one is added.
        if (health == "healthy" && await ReadOptionsAsync(http, baseUri, cancellationToken) is { } options && !NzbGetServerPlan.HasWorkingServer(options))
        {
            health = "degraded";
            message = UsenetProviderText.NoProvider;
            // A client with no news server shows that, never "Paused": a fresh NZBGet starts paused, which would hide it.
            clientPaused = false;
        }

        return CreateSnapshot(
            client, queue, capturedUtc, health, message, history.Count > 0 ? history : null,
            freeSpaceBytes: freeSpaceBytes, clientPaused: clientPaused, clientVersion: clientVersion);
    }

    public override async Task<bool?> HasUsenetProviderAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return null;
        return await ReadOptionsAsync(CreateHttp(client), baseUri, cancellationToken) is { } options ? NzbGetServerPlan.HasWorkingServer(options) : null;
    }

    /// <summary>#1142: writes the person's news server into NZBGet's config (saveconfig), reloads it, then reads it back.</summary>
    public override async Task<UsenetProviderClientResult?> SetUsenetProviderAsync(DownloadClientItem client, UsenetProviderRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null)
        {
            return new(client.Id, client.Name, false, $"Deluno does not have {client.Name}'s address.");
        }

        var http = CreateHttp(client);
        try
        {
            var current = await ReadOptionsAsync(http, baseUri, cancellationToken);
            if (current is null)
            {
                return new(client.Id, client.Name, false, $"Deluno could not read {client.Name}'s settings, so it left them alone.");
            }

            var plan = NzbGetServerPlan.Apply(current, request)
                .Select(option => new Dictionary<string, string> { ["Name"] = option.Key, ["Value"] = option.Value })
                .ToArray();
            var saved = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<bool>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("saveconfig", [plan]), cancellationToken);
            if (saved?.Result != true)
            {
                return new(client.Id, client.Name, false, $"{client.Name} did not accept the provider.");
            }

            try
            {
                await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<bool>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("reload", []), cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // NZBGet drops the connection while it restarts its own services; the read-back below waits for it.
            }

            // NZBGet applies a reload a moment after it answers, and answers `config` from the old settings until
            // then. So the read-back asks again for a bounded time, and only counts the provider that was just
            // written (its address), never an older one that was already there.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var after = await ReadOptionsAsync(http, baseUri, cancellationToken);
                if (after is not null && NzbGetServerPlan.HasWorkingServer(after, request.Host))
                {
                    return new(client.Id, client.Name, true, $"{client.Name} now uses {request.Host.Trim()}.");
                }

                await Task.Delay(_readBackDelay, cancellationToken);
            }

            return new(client.Id, client.Name, false, $"{client.Name} did not keep the provider. Check the address and try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(client.Id, client.Name, false, $"Deluno could not reach {client.Name} to give it the provider.");
        }
    }

    /// <summary>NZBGet's options as it holds them now, or null when it could not be asked.</summary>
    private static async Task<IReadOnlyList<KeyValuePair<string, string>>?> ReadOptionsAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetOption>>>(
                http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("config", []), cancellationToken);
            return response?.Result is null
                ? null
                : response.Result.Where(option => option.Name is not null).Select(option => new KeyValuePair<string, string>(option.Name!, option.Value ?? string.Empty)).ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public override async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        return baseUri is null ? [] : await GetHistoryCoreAsync(CreateHttp(client), client, baseUri, capturedUtc, cancellationToken);
    }

    /// <summary>
    /// NZBGet's own <c>listfiles(FileID, LockFileID, NZBID)</c> (#816): called
    /// with 0/0 and this item's NZBID, it lists every file NZBGet still holds
    /// for that group. Only covers a download still in NZBGet's queue -
    /// NZBGet drops per-file detail once a group moves to history, the same
    /// point past which the content blocklist stops mattering because the
    /// files are already on disk for the import step to see directly. Null
    /// when the address or id is missing, or the call itself fails.
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || !DownloadClientHelpers.TryParseId(queueItemId, out var id)) return null;

        try
        {
            var http = CreateHttp(client);
            var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetFile>>>(
                http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("listfiles", [0, 0, id]), cancellationToken);
            return response?.Result
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

    /// <summary>
    /// Resumes NZBGet as a whole. Its pause is three separate switches (downloads,
    /// post-processing, folder scan), and status reports the client paused when
    /// any of them is on, so all three are released.
    /// </summary>
    public override async Task<ClientResumeResult> ResumeClientAsync(DownloadClientItem client, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return new ClientResumeResult(true, false, "NZBGet's address is missing.");
        var http = CreateHttp(client);
        var endpoint = new Uri(baseUri, "jsonrpc");
        foreach (var method in new[] { "resumedownload", "resumepost", "resumescan" })
        {
            var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<bool?>>(http, endpoint, new NzbGetRequest(method, []), cancellationToken);
            if (response is null) return new ClientResumeResult(true, false, "NZBGet did not answer when asked to resume.");
            if (response.Error is { } error) return new ClientResumeResult(true, false, $"NZBGet would not resume: {error.Message ?? "no reason given"}.");
            if (response.Result == false) return new ClientResumeResult(true, false, "NZBGet would not resume.");
        }

        return new ClientResumeResult(true, true, "NZBGet is downloading again.");
    }

    /// <summary>
    /// One NZBGet command per Deluno verb, and every one of them acting on the
    /// download the caller named.
    ///
    /// <para><b>Two of these used to be wrong in the same way: they did not
    /// match what was asked.</b> `pause` and `resume` sent
    /// <c>pausedownload</c> and <c>resumedownload</c>, which are NZBGet's
    /// global switches — asked to pause one download, Deluno stopped the whole
    /// client and reported success. And `delete` and `delete-with-data` both
    /// sent <c>GroupDelete</c>, so the distinction the two verbs exist to draw
    /// was silently dropped: a caller asking to take the files got the same
    /// request as one asking to leave them.</para>
    ///
    /// <para>The mapping is spelled out one arm at a time now, rather than
    /// sharing arms with <c>or</c>, so a verb that has no distinct command has
    /// to say so in the open.</para>
    ///
    /// <para>Written against NZBGet's documented <c>editqueue</c> command set.
    /// The lab has no NZBGet instance, so unlike the qBittorrent path this is
    /// not confirmed against a live server.</para>
    /// </summary>
    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);

        if (!DownloadClientHelpers.TryParseId(queueItemId, out var id))
        {
            return DownloadClientHelpers.UnreadableId(client, queueItemId, action, "NZBGet");
        }

        var http = CreateHttp(client);
        var endpoint = new Uri(baseUri, "jsonrpc");

        // NZBGet answers 200 with {"result": false} when it did nothing, and with
        // an "error" object when it refused, so the result is what counts (#1201).
        string? lastError = null;
        async Task<bool> Edit(string command, int offset = 0)
        {
            try
            {
                var reply = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<bool?>>(
                    http, endpoint, new NzbGetRequest("editqueue", [command, offset, "", new[] { id }]), cancellationToken);
                lastError = reply?.Error?.Message;
                return reply is { Error: null, Result: true };
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                lastError = exception.Message;
                return false;
            }
        }

        DownloadClientActionResult Done(bool worked, string message, string what)
            => worked
                ? Sent(client, queueItemId, action, message)
                : DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                    $"NZBGet did not {what}{(string.IsNullOrWhiteSpace(lastError) ? string.Empty : $": {lastError}")}. Nothing was changed.",
                    upstreamDetail: lastError);

        switch (action)
        {
            case DownloadClientActions.Pause:
                return Done(await Edit("GroupPause"), "Paused this download in NZBGet.", "pause this download");

            case DownloadClientActions.Resume:
                return Done(await Edit("GroupResume"), "Resumed this download in NZBGet.", "resume this download");

            // Out of the queue, files left where they are. A finished job is in
            // NZBGet's history rather than its queue, so when the queue edit
            // removes nothing the history one is tried.
            case DownloadClientActions.Delete:
                return Done(await Edit("GroupDelete") || await Edit("HistoryDelete"),
                    "Removed this download from NZBGet, leaving its files.", "remove this download");

            // The same, and the files with it. GroupFinalDelete is the only
            // command that does both; GroupDelete does not.
            case DownloadClientActions.DeleteWithData:
                return Done(await Edit("GroupFinalDelete") || await Edit("HistoryFinalDelete"),
                    "Removed this download from NZBGet, along with its files.", "remove this download");

            // Two requests, because NZBGet keeps the thing that refuses a
            // release somewhere the first one does not reach. The history entry
            // is what its duplicate check reads, and HistoryFinalDelete is the
            // only command that removes it — HistoryDelete hides it, and a
            // hidden entry still refuses the release. The job is in one place
            // or the other, so either one working is enough.
            case DownloadClientActions.Forget:
            {
                var fromQueue = await Edit("GroupFinalDelete");
                var fromHistory = await Edit("HistoryFinalDelete");
                return Done(fromQueue || fromHistory,
                    "Removed this download and its history entry from NZBGet, so it will accept this release again.", "forget this download");
            }

            // #810: GroupMoveTop/GroupMoveBottom are NZBGet's own edit-queue
            // commands for the ends of the queue; GroupMoveOffset moves it by
            // one position in either direction for up/down, the same
            // editqueue call the pause/delete commands above already use.
            case DownloadClientActions.MoveToTop:
                return Done(await Edit("GroupMoveTop"), "Moved this download to the top of NZBGet's queue.", "move this download");

            case DownloadClientActions.MoveUp:
                return Done(await Edit("GroupMoveOffset", -1), "Moved this download up in NZBGet's queue.", "move this download");

            case DownloadClientActions.MoveDown:
                return Done(await Edit("GroupMoveOffset", 1), "Moved this download down in NZBGet's queue.", "move this download");

            case DownloadClientActions.MoveToBottom:
                return Done(await Edit("GroupMoveBottom"), "Moved this download to the bottom of NZBGet's queue.", "move this download");

            // NZBGet verifies a download itself and exposes no per-item recheck,
            // so this is refused rather than quietly mapped to something else.
            default:
                return DownloadClientHelpers.Unsupported(client, queueItemId, action, "NZBGet");
        }
    }

    private static DownloadClientActionResult Sent(DownloadClientItem client, string queueItemId, string action, string message)
        => DownloadClientHelpers.ActionSuccess(client, queueItemId, action, message);

    /// <summary>
    /// Whether NZBGet has the category and which folder it saves it to (#616):
    /// the category's DestDir, or NZBGet's DestDir with the category appended
    /// when AppendCategoryDir is on, or DestDir itself.
    /// </summary>
    public override async Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
    {
        var name = category.Trim();
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (name.Length == 0 || baseUri is null)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Configuration,
                name.Length == 0 ? "Enter a category before checking it." : "Add the NZBGet address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetOption>>>(
                CreateHttp(client), new Uri(baseUri, "jsonrpc"), new NzbGetRequest("config", []), cancellationToken);
            var options = (response?.Result ?? []).Where(option => option.Name is not null)
                .GroupBy(option => option.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            string Resolve(string value) => System.Text.RegularExpressions.Regex.Replace(
                value, @"\$\{(\w+)\}", match => options.TryGetValue(match.Groups[1].Value, out var inner) ? inner : match.Value);
            var destDir = Resolve(options.GetValueOrDefault("DestDir") ?? string.Empty);
            // The intermediate folder is where a job is kept while it runs; empty means NZBGet works in the destination folder (#1164).
            var interDir = Resolve(options.GetValueOrDefault("InterDir") ?? string.Empty);

            var index = options.Keys
                .Where(key => key.StartsWith("Category", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".Name", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(key => string.Equals(options[key], name, StringComparison.OrdinalIgnoreCase))?[..^".Name".Length];
            if (index is null)
            {
                return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Missing,
                    $"NZBGet has no category named {name}, so its downloads go to NZBGet's DestDir ({destDir}). Add the category in NZBGet, Settings, Categories.",
                    Supported: true, Found: false, SavePath: ClientPaths.ClientFolder(destDir, null),
                    IncompletePath: string.IsNullOrWhiteSpace(interDir) ? ClientPaths.ClientFolder(destDir, null) : interDir);
            }

            var ownDir = Resolve(options.GetValueOrDefault(index + ".DestDir") ?? string.Empty);
            var appends = !string.Equals(options.GetValueOrDefault("AppendCategoryDir"), "no", StringComparison.OrdinalIgnoreCase);
            var folder = !string.IsNullOrWhiteSpace(ownDir)
                ? ClientPaths.ClientFolder(destDir, ownDir)
                : ClientPaths.ClientFolder(destDir, appends ? name : null);
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Ready,
                $"NZBGet has a category named {name}, saving to {folder}.", Supported: true, Found: true, SavePath: folder,
                IncompletePath: string.IsNullOrWhiteSpace(interDir) ? folder : interDir);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not read NZBGet's categories: {ex.Message}", Supported: true, Found: false);
        }
    }

    private sealed record NzbGetOption([property: JsonPropertyName("Name")] string? Name, [property: JsonPropertyName("Value")] string? Value);

    /// <summary>
    /// A post-processing parameter override sent with <c>append</c> (#663) —
    /// <c>Name</c>/<c>Value</c>, exactly cased, because the outbound JSON body
    /// is serialized with web (camelCase) defaults everywhere else on this
    /// call and NZBGet's own field names are not camelCase.
    /// </summary>
    private sealed record NzbGetPPParameter([property: JsonPropertyName("Name")] string Name, [property: JsonPropertyName("Value")] string Value);

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    private HttpClient CreateHttp(DownloadClientItem client)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        DownloadClientHelpers.AddBasicAuth(http, client);
        return http;
    }

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryCoreAsync(HttpClient http, DownloadClientItem client, Uri baseUri, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetHistoryItem>>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("history", []), cancellationToken);
        return (response?.Result ?? []).Select(item =>
        {
            var outcome = NormalizeOutcome(item.Status ?? string.Empty);
            var errorMessage = outcome == "failed" ? FailureReason(item.Status ?? string.Empty)
                : outcome == DownloadClientHistoryOutcomes.Duplicate ? DuplicateReason(item.Status ?? string.Empty)
                : outcome == DownloadClientHistoryOutcomes.NeedsAttention ? AttentionReason(item.Status ?? string.Empty)
                : outcome is DownloadClientHistoryOutcomes.ScriptFailed or DownloadClientHistoryOutcomes.ScriptWarning ? ScriptReason(item.ScriptStatuses)
                : QueueError(item.Status);
            return new DownloadClientHistoryItem(item.NzbId.ToString(CultureInfo.InvariantCulture), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.NzbName ?? item.Name ?? "Unknown NZBGet history item"), item.NzbName ?? item.Name ?? "Unknown NZBGet history item",
                item.Category ?? string.Empty, outcome, "NZBGet", item.FileSizeHi * 1_000_000L, DownloadClientHelpers.FromUnix(item.HistoryTime), errorMessage, JobFolder(item.DestDir),
                HistorySource: "native",
                ExternalId: item.NzbId.ToString(CultureInfo.InvariantCulture));
        }).ToArray();
    }

    private static async Task<NzbGetStatus?> GetStatusAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        return (await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<NzbGetStatus>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("status", []), cancellationToken))?.Result;
    }

    /// <summary>NZBGet's configured DestDir with ${MainDir}-style references resolved, or null when it cannot be read.</summary>
    private static async Task<string?> GetDestDirAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetOption>>>(
                http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("config", []), cancellationToken);
            var options = (response?.Result ?? []).Where(option => option.Name is not null)
                .GroupBy(option => option.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            var destDir = System.Text.RegularExpressions.Regex.Replace(
                options.GetValueOrDefault("DestDir") ?? string.Empty, @"\$\{(\w+)\}",
                match => options.TryGetValue(match.Groups[1].Value, out var inner) ? inner : match.Value);
            return string.IsNullOrWhiteSpace(destDir) ? null : destDir;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
    // #812: version() is its own RPC method, distinct from status(). A
    // failure here should never take down the whole snapshot.
    private static async Task<string?> GetVersionAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var result = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<string>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("version", []), cancellationToken);
            return string.IsNullOrWhiteSpace(result?.Result) ? null : result.Result;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static int CalculateEta(long remainingBytes, long? bytesPerSecond) => remainingBytes <= 0 || bytesPerSecond is null or <= 0 ? 0 : Convert.ToInt32(Math.Clamp(remainingBytes / (double)bytesPerSecond.Value, 0, int.MaxValue));
    // #880/#881: like SABnzbd, NZBGet has no per-item setup-problem signal
    // while a group is still in the live queue — WARNING/SPACE (disk full) is
    // a history status for a finished download whose unpack stopped, already
    // handled as DownloadClientHistoryOutcomes.NeedsAttention rather than a
    // failure. A live-queue QueueError stays a release-side finding.
    private static string? QueueError(string? status) => string.IsNullOrWhiteSpace(status) ? null : status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("stall", StringComparison.OrdinalIgnoreCase) ? status : null;
    /// <summary>
    /// NZBGet's history status is MAIN/DETAIL: SUCCESS/UNPACK, FAILURE/PAR,
    /// WARNING/SCRIPT, DELETED/MANUAL and so on (#635, #647). Only bare words
    /// were read, so "success/unpack" was never completed and no NZBGet
    /// download was ever handed on. WARNING/SCRIPT is a finished download
    /// whose script complained; every other WARNING and FAILURE is not usable.
    ///
    /// <para><b>FAILURE/SCRIPT and WARNING/SCRIPT are not a bad release
    /// either (#910).</b> The download itself completed; only NZBGet's own
    /// post-processing script failed or warned. James: "if something is
    /// unreadable and corrupt don't even retry" was about the file itself —
    /// a script the user configured is a problem on this machine, the same
    /// shape as <see cref="DownloadClientHistoryOutcomes.NeedsAttention"/>.
    /// Read as <see cref="DownloadClientHistoryOutcomes.ScriptFailed"/> and
    /// <see cref="DownloadClientHistoryOutcomes.ScriptWarning"/> so neither
    /// blocklists or searches again, and both still hand the finished file to
    /// the ordinary import path to be probed.</para>
    ///
    /// <para><b>DELETED/DUPE, DELETED/COPY and DELETED/GOOD are not failures
    /// (#663).</b> They mean NZBGet's own duplicate check correctly declined a
    /// release it — or a better copy of it — already held, which is the system
    /// working, not a download that went wrong. Reading them as "failed" used
    /// to blocklist the release and search for it again over something NZBGet
    /// had already handled ("NZBGet deleted it: it was a duplicate of one
    /// NZBGet already held" read exactly like a real failure). They come back
    /// as <see cref="DownloadClientHistoryOutcomes.Duplicate"/> instead, which
    /// <see cref="DownloadClientTelemetryService.RecordWhatTheClientShowsAsync"/>
    /// resolves without blocklisting or searching again.</para>
    ///
    /// <para>Every other DELETED reason — a failed health check, an unreadable
    /// NZB, or someone outside Deluno removing it by hand — is still read as a
    /// failure, because the release really is gone and nothing already holds
    /// it. Deluno's own removals (the health-remediation and "delete" paths)
    /// never reach here in the first place: they record the dispatch's outcome
    /// before NZBGet's history catches up, and
    /// <see cref="DownloadClientTelemetryService.RecordWhatTheClientShowsAsync"/>
    /// only acts on a dispatch that has no outcome recorded yet — so a DELETED
    /// row Deluno itself produced is not read as a failure twice.</para>
    ///
    /// <para><b>WARNING/SPACE is not a failure either (#733).</b> Per NZBGet's
    /// own history documentation
    /// (https://nzbget.com/documentation/api/history), a MAIN status of
    /// WARNING means the download itself completed and something in
    /// post-processing needs attention; SPACE is the detail NZBGet uses when
    /// unpacking stopped because the disk filled. The release is not lost —
    /// freeing space and re-running post-processing in NZBGet recovers it —
    /// so unlike every other WARNING/* detail it is read as
    /// <see cref="DownloadClientHistoryOutcomes.NeedsAttention"/> rather than
    /// "failed": nothing is blocklisted and nothing is searched for again.
    /// Sonarr's Nzbget.cs (<c>GetHistory</c>) draws the same line, reading
    /// <c>UnpackStatus.Space</c> as a warning rather than treating the item as
    /// failed. Every other WARNING/* detail (HEALTH, PAR, and the rest) keeps
    /// being read as "failed": Sonarr does not special-case them either, and
    /// none of them describe a download that is still recoverable without a
    /// new release.</para>
    /// </summary>
    internal static string NormalizeOutcome(string value)
    {
        var status = value.Trim();
        var (main, detail) = SplitStatus(status);
        return main switch
        {
            "SUCCESS" or "COMPLETED" or "SUCCEEDED" => DownloadQueueStatuses.Completed,
            "FAILURE" when detail == "SCRIPT" => DownloadClientHistoryOutcomes.ScriptFailed,
            "WARNING" when detail == "SCRIPT" => DownloadClientHistoryOutcomes.ScriptWarning,
            "WARNING" when detail == "SPACE" => DownloadClientHistoryOutcomes.NeedsAttention,
            "DELETED" when detail is "DUPE" or "COPY" or "GOOD" => DownloadClientHistoryOutcomes.Duplicate,
            "WARNING" or "FAILURE" or "DELETED" => "failed",
            _ when status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase) => "failed",
            _ when status.Contains("import", StringComparison.OrdinalIgnoreCase) => DownloadQueueStatuses.ImportReady,
            _ => status.Length == 0 ? "unknown" : status.ToLowerInvariant()
        };
    }

    /// <summary>
    /// NZBGet's own reason, in words a person reads rather than a MAIN/DETAIL
    /// pair (#647). Only meaningful once <see cref="NormalizeOutcome"/> has
    /// said the status is a failure.
    /// </summary>
    internal static string FailureReason(string value)
    {
        var (main, detail) = SplitStatus(value.Trim());
        return main switch
        {
            "FAILURE" => $"NZBGet could not download it: {FailureDetail(detail)}.",
            "WARNING" => $"NZBGet finished it with a warning: {FailureDetail(detail)}.",
            "DELETED" => $"NZBGet deleted it: {DeletedDetail(detail)}.",
            _ => $"NZBGet reported it as \"{value.Trim()}\"."
        };
    }

    /// <summary>
    /// The words for a <see cref="DownloadClientHistoryOutcomes.Duplicate"/>
    /// row (#663) — deliberately not "deleted", which reads as something went
    /// wrong. Nothing did: NZBGet already has this download.
    /// </summary>
    internal static string DuplicateReason(string value)
    {
        var (_, detail) = SplitStatus(value.Trim());
        return $"NZBGet already has this download: {DeletedDetail(detail)}.";
    }

    /// <summary>
    /// The words for a <see cref="DownloadClientHistoryOutcomes.NeedsAttention"/>
    /// row (#733) — the download is not lost, but a person has to act before it
    /// can finish.
    /// </summary>
    internal static string AttentionReason(string value)
    {
        var (_, detail) = SplitStatus(value.Trim());
        return detail switch
        {
            "SPACE" => "NZBGet finished downloading this, but unpacking stopped because the disk is full: free space and re-run post-processing in NZBGet.",
            _ => $"NZBGet needs help finishing this: {FailureDetail(detail)}."
        };
    }

    /// <summary>
    /// The words for a <see cref="DownloadClientHistoryOutcomes.ScriptFailed"/>
    /// or <see cref="DownloadClientHistoryOutcomes.ScriptWarning"/> row
    /// (#910), naming the script NZBGet itself names in
    /// <c>ScriptStatuses</c> — the first one not reporting SUCCESS — rather
    /// than the bare MAIN/DETAIL pair.
    /// </summary>
    internal static string ScriptReason(IReadOnlyList<NzbGetScriptStatus>? scriptStatuses)
    {
        var failing = scriptStatuses?.FirstOrDefault(status =>
            !string.Equals(status.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        return failing is { Name.Length: > 0 }
            ? $"Your client's script \"{failing.Name}\" {(string.Equals(failing.Status, "FAILURE", StringComparison.OrdinalIgnoreCase) ? "failed" : "warned")}."
            : "Your client's post-processing script reported a problem.";
    }

    private static string FailureDetail(string detail) => detail switch
    {
        "HEALTH" => "too many articles are missing",
        "PAR" => "it could not repair the download; too much data is missing or damaged",
        "UNPACK" => "unpacking the download failed",
        "MOVE" => "it could not move the finished files into place",
        "SCRIPT" => "a post-processing script failed it",
        "" or "NONE" => "no reason was given",
        _ => detail.ToLowerInvariant()
    };

    private static string DeletedDetail(string detail) => detail switch
    {
        "HEALTH" => "a health check failed",
        "DUPE" or "COPY" => "it was a duplicate of one NZBGet already held",
        "GOOD" => "a better duplicate was kept instead",
        "SCAN" => "the NZB file itself was unreadable",
        "MANUAL" => "something asked NZBGet to remove it",
        "" or "NONE" => "no reason was given",
        _ => detail.ToLowerInvariant()
    };

    private static (string Main, string Detail) SplitStatus(string status)
    {
        var slash = status.IndexOf('/');
        return slash < 0
            ? (status.ToUpperInvariant(), string.Empty)
            : (status[..slash].ToUpperInvariant(), status[(slash + 1)..].ToUpperInvariant());
    }

    /// <summary>NZBGet's DestDir is already the job's own folder; adding the job name again named a folder that does not exist (#635).</summary>
    private static string? JobFolder(string? destDir) => string.IsNullOrWhiteSpace(destDir) ? null : destDir.Trim().TrimEnd('/', '\\');

    private sealed record NzbGetRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] object[] Params);
    private sealed record NzbGetResponse<T>([property: JsonPropertyName("result")] T? Result, [property: JsonPropertyName("error")] NzbGetError? Error = null);
    private sealed record NzbGetError([property: JsonPropertyName("code")] int? Code, [property: JsonPropertyName("message")] string? Message);
    private sealed record NzbGetFile([property: JsonPropertyName("Filename")] string? Filename);
    private sealed record NzbGetQueueItem([property: JsonPropertyName("NZBID")] int NzbId, [property: JsonPropertyName("NZBName")] string? NzbName, [property: JsonPropertyName("Category")] string? Category, [property: JsonPropertyName("Status")] string? Status, [property: JsonPropertyName("FileSizeHi")] long FileSizeHi, [property: JsonPropertyName("RemainingSizeHi")] long RemainingSizeHi, [property: JsonPropertyName("DestDir")] string? DestDir);
    private sealed record NzbGetHistoryItem([property: JsonPropertyName("NZBID")] int NzbId, [property: JsonPropertyName("NZBName")] string? NzbName, [property: JsonPropertyName("Name")] string? Name, [property: JsonPropertyName("Category")] string? Category, [property: JsonPropertyName("Status")] string? Status, [property: JsonPropertyName("FileSizeHi")] long FileSizeHi, [property: JsonPropertyName("HistoryTime")] long? HistoryTime, [property: JsonPropertyName("DestDir")] string? DestDir, [property: JsonPropertyName("ScriptStatuses")] IReadOnlyList<NzbGetScriptStatus>? ScriptStatuses);

    /// <summary>One post-processing script's own outcome, from NZBGet's history <c>ScriptStatuses</c> (#910) — SUCCESS, FAILURE or a script-specific warning wording.</summary>
    internal sealed record NzbGetScriptStatus([property: JsonPropertyName("Name")] string? Name, [property: JsonPropertyName("Status")] string? Status);
    // FreeDiskSpaceMB (#663): the one field the health check never read, so a
    // client filling its disk stayed "healthy" right up until every download
    // in the queue started failing.
    private sealed record NzbGetStatus(
        [property: JsonPropertyName("DownloadRate")] long? DownloadRate,
        [property: JsonPropertyName("FreeDiskSpaceMB")] long? FreeDiskSpaceMB,
        // #812: true when nothing is downloading (idle, not unhealthy), and
        // the three separate pause flags NZBGet also reports.
        [property: JsonPropertyName("ServerStandBy")] bool? ServerStandBy = null,
        [property: JsonPropertyName("DownloadPaused")] bool? DownloadPaused = null,
        [property: JsonPropertyName("PostPaused")] bool? PostPaused = null,
        [property: JsonPropertyName("ScanPaused")] bool? ScanPaused = null);
}
