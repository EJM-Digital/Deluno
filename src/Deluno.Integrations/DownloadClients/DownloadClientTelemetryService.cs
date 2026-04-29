using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Integrations.DownloadClients;

public sealed class DownloadClientTelemetryService(
    IPlatformSettingsRepository platformRepository,
    IJobQueueRepository jobQueueRepository,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider)
    : IDownloadClientTelemetryService
{
    public async Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var capturedUtc = timeProvider.GetUtcNow();
        var clients = await platformRepository.ListDownloadClientsAsync(cancellationToken);
        var libraries = await platformRepository.ListLibrariesAsync(cancellationToken);
        var dispatches = await jobQueueRepository.ListDownloadDispatchesAsync(100, null, cancellationToken);
        var importJobs = await jobQueueRepository.ListAsync(200, cancellationToken);
        var snapshots = new List<DownloadClientTelemetrySnapshot>();

        foreach (var client in clients.OrderBy(item => item.Priority).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!client.IsEnabled)
            {
                snapshots.Add(CreateSnapshot(client, [], capturedUtc, "paused", "Client is disabled."));
                continue;
            }

            var liveSnapshot = await TryGetLiveSnapshotAsync(client, capturedUtc, cancellationToken);
            if (liveSnapshot is not null)
            {
                var clientDispatches = dispatches
                    .Where(dispatch => string.Equals(dispatch.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                snapshots.Add(EnrichQueueImportState(
                    EnrichWithDispatchHistory(
                        liveSnapshot,
                        clientDispatches,
                        capturedUtc),
                    libraries,
                    clientDispatches,
                    importJobs));
                continue;
            }

            var dispatchHistory = dispatches
                .Where(dispatch => string.Equals(dispatch.DownloadClientId, client.Id, StringComparison.OrdinalIgnoreCase))
                .Select(dispatch => CreateDispatchHistoryItem(client, dispatch, capturedUtc))
                .ToArray();

            snapshots.Add(CreateSnapshot(
                client,
                [],
                capturedUtc,
                NormalizeHealth(client.HealthStatus),
                client.LastHealthMessage ?? "Live telemetry unavailable; showing Deluno dispatch history only.",
                dispatchHistory));
        }

        return new DownloadTelemetryOverview(
            Summary: Summarize(snapshots.SelectMany(snapshot => snapshot.Queue)),
            Clients: snapshots,
            CapturedUtc: capturedUtc);
    }

    public async Task<DownloadClientActionResult> ExecuteActionAsync(
        string clientId,
        DownloadClientActionRequest request,
        CancellationToken cancellationToken)
    {
        var client = (await platformRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, false, "Download client was not found.");
        }

        var action = NormalizeAction(request.Action);
        if (action is null)
        {
            return new DownloadClientActionResult(client.Id, request.QueueItemId, request.Action, false, "Unsupported action.");
        }

        try
        {
            return client.Protocol switch
            {
                "qbittorrent" => await ExecuteQbittorrentActionAsync(client, action, request.QueueItemId, cancellationToken),
                "sabnzbd" => await ExecuteSabnzbdActionAsync(client, action, request.QueueItemId, cancellationToken),
                "transmission" => await ExecuteTransmissionActionAsync(client, action, request.QueueItemId, cancellationToken),
                "deluge" => await ExecuteDelugeActionAsync(client, action, request.QueueItemId, cancellationToken),
                "nzbget" => await ExecuteNzbGetActionAsync(client, action, request.QueueItemId, cancellationToken),
                "utorrent" => await ExecuteUTorrentActionAsync(client, action, request.QueueItemId, cancellationToken),
                _ => new DownloadClientActionResult(client.Id, request.QueueItemId, action, false, $"{client.Protocol} queue actions are not supported by Deluno.")
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new DownloadClientActionResult(client.Id, request.QueueItemId, action, false, exception.Message);
        }
    }

    private async Task<DownloadClientTelemetrySnapshot?> TryGetLiveSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return client.Protocol switch
            {
                "qbittorrent" => await GetQbittorrentSnapshotAsync(client, capturedUtc, cancellationToken),
                "sabnzbd" => await GetSabnzbdSnapshotAsync(client, capturedUtc, cancellationToken),
                "transmission" => await GetTransmissionSnapshotAsync(client, capturedUtc, cancellationToken),
                "deluge" => await GetDelugeSnapshotAsync(client, capturedUtc, cancellationToken),
                "nzbget" => await GetNzbGetSnapshotAsync(client, capturedUtc, cancellationToken),
                "utorrent" => await GetUTorrentSnapshotAsync(client, capturedUtc, cancellationToken),
                _ => null
            };
        }
        catch (Exception ex)
        {
            return CreateSnapshot(client, [], capturedUtc, "degraded", ex.Message);
        }
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetQbittorrentSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null)
        {
            return null;
        }

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await LoginQbittorrentAsync(http, client, cancellationToken);

        var torrents = await http.GetFromJsonAsync<IReadOnlyList<QbitTorrentItem>>(
            "api/v2/torrents/info",
            cancellationToken) ?? [];

        var queue = torrents.Select(item => new DownloadQueueItem(
            Id: item.Hash ?? item.Name ?? Guid.CreateVersion7().ToString("N"),
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            MediaType: InferMediaType(client, item.Category),
            Title: CleanReleaseTitle(item.Name ?? "Unknown qBittorrent item"),
            ReleaseName: item.Name ?? "Unknown qBittorrent item",
            Category: item.Category ?? string.Empty,
            Status: MapQbitState(item.State, item.Progress),
            Progress: Math.Clamp(Math.Round((item.Progress ?? 0) * 100, 1), 0, 100),
            SpeedMbps: Math.Round((item.DownloadSpeed ?? 0) / 1_000_000d, 1),
            EtaSeconds: Convert.ToInt32(Math.Clamp(item.Eta ?? 0, 0, int.MaxValue)),
            SizeBytes: item.Size ?? 0,
            DownloadedBytes: item.Downloaded ?? 0,
            Peers: item.NumSeeds ?? 0,
            IndexerName: "qBittorrent",
            ErrorMessage: item.State?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ? item.State : null,
            AddedUtc: FromUnix(item.AddedOn),
            SourcePath: ChoosePath(item.ContentPath, item.SavePath)))
            .ToArray();

        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to qBittorrent at {baseUri.Host}:{baseUri.Port}.");
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetSabnzbdSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null)
        {
            return null;
        }

        var apiKey = client.Secret;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return CreateSnapshot(client, [], capturedUtc, "degraded", "SABnzbd API key is missing.");
        }

        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        var queueUrl = new Uri(baseUri, $"api?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}");
        using var response = await http.GetAsync(queueUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SabQueueResponse>(cancellationToken);
        var slots = payload?.Queue?.Slots ?? [];

        var queue = slots.Select(item =>
        {
            var sizeBytes = ParseSabSize(item.Mb) * 1_000_000L;
            var remainingBytes = ParseSabSize(item.Mbleft) * 1_000_000L;
            var downloadedBytes = Math.Max(0, sizeBytes - remainingBytes);
            var progress = sizeBytes <= 0 ? 0 : Math.Round(downloadedBytes / (double)sizeBytes * 100, 1);

            return new DownloadQueueItem(
                Id: item.NzoId ?? item.Filename ?? Guid.CreateVersion7().ToString("N"),
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: InferMediaType(client, item.Category),
                Title: CleanReleaseTitle(item.Filename ?? "Unknown SABnzbd item"),
                ReleaseName: item.Filename ?? "Unknown SABnzbd item",
                Category: item.Category ?? string.Empty,
                Status: MapSabStatus(item.Status, progress),
                Progress: progress,
                SpeedMbps: Math.Round((double)ParseSabSize(payload?.Queue?.Speed), 1),
                EtaSeconds: ParseEta(item.TimeLeft),
                SizeBytes: sizeBytes,
                DownloadedBytes: downloadedBytes,
                Peers: 0,
                IndexerName: "SABnzbd",
                ErrorMessage: MapQueueError(item.Status),
                AddedUtc: capturedUtc);
        }).ToArray();

        var history = await TryGetSabnzbdHistoryAsync(http, client, baseUri, apiKey, capturedUtc, cancellationToken);
        return CreateSnapshot(
            client,
            queue,
            capturedUtc,
            "healthy",
            $"Connected to SABnzbd at {baseUri.Host}:{baseUri.Port}.",
            history.Count > 0 ? history : null);
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetTransmissionSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return null;

        var request = new TransmissionRequest(
            "torrent-get",
            new Dictionary<string, object>
            {
                ["fields"] = new[]
                {
                    "id", "name", "status", "percentDone", "rateDownload", "eta",
                    "totalSize", "downloadedEver", "peersConnected", "addedDate", "doneDate", "downloadDir",
                    "labels", "error", "errorString"
                }
            });
        var response = await SendTransmissionAsync(client, baseUri, request, cancellationToken);
        var torrents = response.Arguments?.Torrents ?? [];
        var queue = torrents.Select(item => new DownloadQueueItem(
            Id: item.Id?.ToString(CultureInfo.InvariantCulture) ?? item.Name ?? Guid.CreateVersion7().ToString("N"),
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            MediaType: InferMediaType(client, item.Labels?.FirstOrDefault()),
            Title: CleanReleaseTitle(item.Name ?? "Unknown Transmission item"),
            ReleaseName: item.Name ?? "Unknown Transmission item",
            Category: item.Labels?.FirstOrDefault() ?? string.Empty,
            Status: MapTransmissionStatus(item.Status, item.PercentDone, item.Error, item.ErrorString),
            Progress: Math.Clamp(Math.Round((item.PercentDone ?? 0) * 100, 1), 0, 100),
            SpeedMbps: Math.Round((item.RateDownload ?? 0) / 1_000_000d, 1),
            EtaSeconds: Math.Max(0, item.Eta ?? 0),
            SizeBytes: item.TotalSize ?? 0,
            DownloadedBytes: item.DownloadedEver ?? 0,
            Peers: item.PeersConnected ?? 0,
            IndexerName: "Transmission",
            ErrorMessage: string.IsNullOrWhiteSpace(item.ErrorString) ? null : item.ErrorString,
            AddedUtc: FromUnix(item.AddedDate),
            SourcePath: ResolveDownloadPath(item.DownloadDir, item.Name)))
            .ToArray();

        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to Transmission at {baseUri.Host}:{baseUri.Port}.");
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetDelugeSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return null;

        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        await DelugeLoginAsync(http, baseUri, client, cancellationToken);
        var payload = new DelugeRequest(
            "web.update_ui",
            [
                new[]
                {
                    "name", "state", "progress", "download_payload_rate", "eta",
                    "total_size", "total_done", "num_peers", "time_added", "label", "message", "save_path"
                },
                new Dictionary<string, object>()
            ],
            2);
        var response = await PostJsonAsync<DelugeResponse<DelugeUpdateResult>>(http, new Uri(baseUri, "json"), payload, cancellationToken);
        var torrents = response?.Result?.Torrents ?? new Dictionary<string, DelugeTorrent>();
        var queue = torrents.Select(pair =>
        {
            var item = pair.Value;
            return new DownloadQueueItem(
                Id: pair.Key,
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: InferMediaType(client, item.Label),
                Title: CleanReleaseTitle(item.Name ?? "Unknown Deluge item"),
                ReleaseName: item.Name ?? "Unknown Deluge item",
                Category: item.Label ?? string.Empty,
                Status: MapTextStatus(item.State, item.Progress),
                Progress: Math.Clamp(Math.Round(item.Progress ?? 0, 1), 0, 100),
                SpeedMbps: Math.Round((item.DownloadPayloadRate ?? 0) / 1_000_000d, 1),
                EtaSeconds: Math.Max(0, Convert.ToInt32(item.Eta ?? 0)),
                SizeBytes: Convert.ToInt64(item.TotalSize ?? 0),
                DownloadedBytes: Convert.ToInt64(item.TotalDone ?? 0),
                Peers: item.NumPeers ?? 0,
                IndexerName: "Deluge",
                ErrorMessage: string.IsNullOrWhiteSpace(item.Message) ? null : item.Message,
                AddedUtc: FromUnix(Convert.ToInt64(item.TimeAdded ?? 0)),
                SourcePath: ResolveDownloadPath(item.SavePath, item.Name));
        }).ToArray();

        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to Deluge at {baseUri.Host}:{baseUri.Port}.");
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetNzbGetSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return null;

        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        AddBasicAuth(http, client);
        var response = await PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetQueueItem>>>(
            http,
            new Uri(baseUri, "jsonrpc"),
            new NzbGetRequest("listgroups", []),
            cancellationToken);
        var status = await TryGetNzbGetStatusAsync(http, baseUri, cancellationToken);
        var queue = (response?.Result ?? []).Select(item =>
        {
            var size = item.FileSizeHi * 1_000_000L;
            var remaining = item.RemainingSizeHi * 1_000_000L;
            var downloaded = Math.Max(0, size - remaining);
            var progress = size <= 0 ? 0 : Math.Round(downloaded / (double)size * 100, 1);
            var speedMbps = Math.Round((status?.DownloadRate ?? 0) / 1_000_000d, 1);
            return new DownloadQueueItem(
                Id: item.NzbId.ToString(CultureInfo.InvariantCulture),
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: InferMediaType(client, item.Category),
                Title: CleanReleaseTitle(item.NzbName ?? "Unknown NZBGet item"),
                ReleaseName: item.NzbName ?? "Unknown NZBGet item",
                Category: item.Category ?? string.Empty,
                Status: MapTextStatus(item.Status, progress),
                Progress: progress,
                SpeedMbps: speedMbps,
                EtaSeconds: CalculateEtaSeconds(remaining, status?.DownloadRate),
                SizeBytes: size,
                DownloadedBytes: downloaded,
                Peers: 0,
                IndexerName: "NZBGet",
                ErrorMessage: MapQueueError(item.Status),
                AddedUtc: capturedUtc,
                SourcePath: ResolveDownloadPath(item.DestDir, item.NzbName));
        }).ToArray();

        var history = await TryGetNzbGetHistoryAsync(http, client, baseUri, capturedUtc, cancellationToken);
        return CreateSnapshot(
            client,
            queue,
            capturedUtc,
            "healthy",
            $"Connected to NZBGet at {baseUri.Host}:{baseUri.Port}.",
            history.Count > 0 ? history : null);
    }

    private async Task<DownloadClientTelemetrySnapshot?> GetUTorrentSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return null;

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), Credentials = BuildCredential(client) };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        var token = await GetUTorrentTokenAsync(http, cancellationToken);
        var payload = await http.GetFromJsonAsync<UTorrentListResponse>($"gui/?token={Uri.EscapeDataString(token)}&list=1", cancellationToken);
        var queue = (payload?.Torrents ?? []).Select(item =>
        {
            var hash = item.Count > 0 ? JsonElementToString(item[0]) : Guid.CreateVersion7().ToString("N");
            var name = item.Count > 2 ? JsonElementToString(item[2]) : "Unknown uTorrent item";
            var size = item.Count > 3 ? JsonElementToInt64(item[3]) : 0;
            var progress = item.Count > 4 ? Math.Round(JsonElementToDouble(item[4]) / 10d, 1) : 0;
            var downloaded = (long)(size * (progress / 100d));
            var speed = item.Count > 9 ? JsonElementToDouble(item[9]) / 1_000_000d : 0;
            var eta = item.Count > 10 ? Convert.ToInt32(JsonElementToDouble(item[10])) : 0;
            var category = item.Count > 11 ? JsonElementToString(item[11]) : string.Empty;
            var peers = item.Count > 12 ? Convert.ToInt32(JsonElementToDouble(item[12])) : 0;
            var addedUtc = item.Count > 23 ? FromUnix(Convert.ToInt64(JsonElementToDouble(item[23]))) : capturedUtc;
            return new DownloadQueueItem(
                Id: hash,
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: InferMediaType(client, category),
                Title: CleanReleaseTitle(name),
                ReleaseName: name,
                Category: category,
                Status: progress >= 100 ? "importReady" : speed > 0 ? "downloading" : "queued",
                Progress: Math.Clamp(progress, 0, 100),
                SpeedMbps: Math.Round(speed, 1),
                EtaSeconds: Math.Max(0, eta),
                SizeBytes: size,
                DownloadedBytes: downloaded,
                Peers: peers,
                IndexerName: "uTorrent",
                ErrorMessage: null,
                AddedUtc: addedUtc);
        }).ToArray();

        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to uTorrent at {baseUri.Host}:{baseUri.Port}.");
    }

    private static async Task LoginQbittorrentAsync(HttpClient http, DownloadClientItem client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret))
        {
            return;
        }

        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
            new KeyValuePair<string, string>("password", client.Secret ?? string.Empty)
        ]);

        using var response = await http.PostAsync("api/v2/auth/login", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!text.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("qBittorrent rejected the configured username/password.");
        }
    }

    private async Task<DownloadClientActionResult> ExecuteQbittorrentActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, "Client address is missing.");
        }

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await LoginQbittorrentAsync(http, client, cancellationToken);

        var endpoints = action switch
        {
            // qBittorrent 5 uses stop/start terminology; older installs used pause/resume.
            "pause" => new[] { "api/v2/torrents/stop", "api/v2/torrents/pause" },
            "resume" => new[] { "api/v2/torrents/start", "api/v2/torrents/resume" },
            "delete" => new[] { "api/v2/torrents/delete" },
            "recheck" => new[] { "api/v2/torrents/recheck" },
            _ => null
        };
        if (endpoints is null)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, "qBittorrent does not support this action.");
        }

        var pairs = new List<KeyValuePair<string, string>> { new("hashes", queueItemId) };
        if (action == "delete")
        {
            pairs.Add(new KeyValuePair<string, string>("deleteFiles", "false"));
        }

        HttpResponseMessage? lastResponse = null;
        foreach (var endpoint in endpoints)
        {
            lastResponse?.Dispose();
            lastResponse = await http.PostAsync(endpoint, new FormUrlEncodedContent(pairs), cancellationToken);
            if (lastResponse.IsSuccessStatusCode || lastResponse.StatusCode != HttpStatusCode.NotFound)
            {
                break;
            }
        }

        using (lastResponse)
        {
            return new DownloadClientActionResult(
                client.Id,
                queueItemId,
                action,
                lastResponse?.IsSuccessStatusCode == true,
                lastResponse?.IsSuccessStatusCode == true ? "qBittorrent action sent." : $"qBittorrent returned {(int?)lastResponse?.StatusCode ?? 0}.");
        }
    }

    private async Task<DownloadClientActionResult> ExecuteSabnzbdActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, "Client address is missing.");
        }

        if (string.IsNullOrWhiteSpace(client.Secret))
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, "SABnzbd API key is missing.");
        }

        var mode = action switch
        {
            "pause" => "queue&name=pause",
            "resume" => "queue&name=resume",
            "delete" => "queue&name=delete",
            _ => null
        };
        if (mode is null)
        {
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, "SABnzbd does not support this action.");
        }

        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        var url = new Uri(baseUri, $"api?mode={mode}&value={Uri.EscapeDataString(queueItemId)}&apikey={Uri.EscapeDataString(client.Secret)}&output=json");
        using var response = await http.GetAsync(url, cancellationToken);
        return new DownloadClientActionResult(
            client.Id,
            queueItemId,
            action,
            response.IsSuccessStatusCode,
            response.IsSuccessStatusCode ? "SABnzbd action sent." : $"SABnzbd returned {(int)response.StatusCode}.");
    }

    private async Task<DownloadClientActionResult> ExecuteTransmissionActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var method = action switch
        {
            "pause" => "torrent-stop",
            "resume" => "torrent-start",
            "delete" => "torrent-remove",
            "recheck" => "torrent-verify",
            _ => null
        };
        if (method is null) return Unsupported(client, queueItemId, action, "Transmission");
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, queueItemId, action);
        var args = new Dictionary<string, object> { ["ids"] = new[] { ParseId(queueItemId) } };
        if (action == "delete") args["delete-local-data"] = false;
        await SendTransmissionAsync(client, baseUri, new TransmissionRequest(method, args), cancellationToken);
        return Success(client, queueItemId, action, "Transmission action sent.");
    }

    private async Task<DownloadClientActionResult> ExecuteDelugeActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var method = action switch
        {
            "pause" => "core.pause_torrent",
            "resume" => "core.resume_torrent",
            "delete" => "core.remove_torrent",
            "recheck" => "core.force_recheck",
            _ => null
        };
        if (method is null) return Unsupported(client, queueItemId, action, "Deluge");
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, queueItemId, action);
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        await DelugeLoginAsync(http, baseUri, client, cancellationToken);
        object[] parameters = action == "delete"
            ? [new[] { queueItemId }, false]
            : [new[] { queueItemId }];
        await PostJsonAsync<DelugeResponse<object>>(http, new Uri(baseUri, "json"), new DelugeRequest(method, parameters, 3), cancellationToken);
        return Success(client, queueItemId, action, "Deluge action sent.");
    }

    private async Task<DownloadClientActionResult> ExecuteNzbGetActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var method = action switch
        {
            "pause" => "pausedownload",
            "resume" => "resumedownload",
            "delete" => "editqueue",
            _ => null
        };
        if (method is null) return Unsupported(client, queueItemId, action, "NZBGet");
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, queueItemId, action);
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        AddBasicAuth(http, client);
        object[] parameters = action == "delete"
            ? ["GroupDelete", 0, "", new[] { ParseId(queueItemId) }]
            : Array.Empty<object>();
        await PostJsonAsync<NzbGetResponse<object>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest(method, parameters), cancellationToken);
        return Success(client, queueItemId, action, "NZBGet action sent.");
    }

    private async Task<DownloadClientActionResult> ExecuteUTorrentActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        var verb = action switch
        {
            "pause" => "pause",
            "resume" => "start",
            "delete" => "remove",
            "recheck" => "recheck",
            _ => null
        };
        if (verb is null) return Unsupported(client, queueItemId, action, "uTorrent");
        var baseUri = ResolveEndpoint(client);
        if (baseUri is null) return MissingAddress(client, queueItemId, action);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), Credentials = BuildCredential(client) };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        var token = await GetUTorrentTokenAsync(http, cancellationToken);
        using var response = await http.GetAsync($"gui/?token={Uri.EscapeDataString(token)}&action={verb}&hash={Uri.EscapeDataString(queueItemId)}", cancellationToken);
        return new DownloadClientActionResult(client.Id, queueItemId, action, response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "uTorrent action sent." : $"uTorrent returned {(int)response.StatusCode}.");
    }

    private static DownloadClientTelemetrySnapshot CreateSnapshot(
        DownloadClientItem client,
        IReadOnlyList<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc,
        string health,
        string? message,
        IReadOnlyList<DownloadClientHistoryItem>? history = null)
        => new(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: client.Protocol,
            EndpointUrl: client.EndpointUrl,
            HealthStatus: health,
            LastHealthMessage: message,
            Summary: Summarize(queue),
            Queue: queue,
            History: history ?? CreateHistoryFromQueue(client, queue, capturedUtc),
            CapturedUtc: capturedUtc);

    private static DownloadClientTelemetrySnapshot EnrichWithDispatchHistory(
        DownloadClientTelemetrySnapshot snapshot,
        IEnumerable<DownloadDispatchItem> dispatches,
        DateTimeOffset capturedUtc)
    {
        var liveIds = new HashSet<string>(snapshot.History.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        var dispatchHistory = dispatches
            .Where(dispatch => !liveIds.Contains(dispatch.Id))
            .Select(dispatch => CreateDispatchHistoryItem(snapshot, dispatch, capturedUtc))
            .ToArray();

        if (dispatchHistory.Length == 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            History = snapshot.History
                .Concat(dispatchHistory)
                .OrderByDescending(item => item.CompletedUtc)
                .Take(50)
                .ToArray()
        };
    }

    private static DownloadClientTelemetrySnapshot EnrichQueueImportState(
        DownloadClientTelemetrySnapshot snapshot,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches,
        IReadOnlyList<JobQueueItem> importJobs)
    {
        if (snapshot.Queue.Count == 0)
        {
            return snapshot;
        }

        var jobsBySource = importJobs
            .Where(job => job.JobType == "filesystem.import.execute")
            .Select(job => new { Job = job, SourcePath = TryReadImportSourcePath(job.PayloadJson) })
            .Where(item => !string.IsNullOrWhiteSpace(item.SourcePath))
            .GroupBy(item => NormalizeSourceKey(item.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Job.CreatedUtc).First().Job, StringComparer.OrdinalIgnoreCase);

        var queue = snapshot.Queue.Select(item =>
        {
            if (item.Status is not ("importReady" or "completed"))
            {
                return item;
            }

            if (!string.IsNullOrWhiteSpace(item.SourcePath) &&
                jobsBySource.TryGetValue(NormalizeSourceKey(item.SourcePath), out var job))
            {
                var status = job.Status switch
                {
                    "queued" or "running" => "importQueued",
                    "completed" => "imported",
                    "failed" => "importFailed",
                    _ => item.Status
                };
                return item with { Status = status };
            }

            var library = ResolveLibraryForQueueItem(item, libraries, dispatches);
            if (library is not null &&
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase))
            {
                return item with { Status = "waitingForProcessor" };
            }

            return item;
        }).ToArray();

        return snapshot with
        {
            Queue = queue,
            Summary = Summarize(queue)
        };
    }

    private static LibraryItem? ResolveLibraryForQueueItem(
        DownloadQueueItem item,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DownloadDispatchItem> dispatches)
    {
        var dispatch = dispatches
            .OrderByDescending(dispatch => dispatch.CreatedUtc)
            .FirstOrDefault(dispatch =>
                string.Equals(dispatch.ReleaseName, item.ReleaseName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dispatch.ReleaseName, item.Title, StringComparison.OrdinalIgnoreCase));
        if (dispatch is not null)
        {
            var dispatchedLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Id, dispatch.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (dispatchedLibrary is not null)
            {
                return dispatchedLibrary;
            }
        }

        var normalizedMediaType = item.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
        var mediaLibraries = libraries
            .Where(library => string.Equals(library.MediaType, normalizedMediaType, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(item.SourcePath))
        {
            var source = NormalizeSourceKey(item.SourcePath);
            var pathMatch = mediaLibraries.FirstOrDefault(library =>
                !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                source.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        return mediaLibraries.FirstOrDefault();
    }

    private static string? TryReadImportSourcePath(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!TryGetProperty(root, "preview", out var preview) ||
                !TryGetProperty(preview, "sourcePath", out var sourcePath) ||
                sourcePath.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return sourcePath.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeSourceKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');

    private async Task<IReadOnlyList<DownloadClientHistoryItem>> TryGetSabnzbdHistoryAsync(
        HttpClient http,
        DownloadClientItem client,
        Uri baseUri,
        string apiKey,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var historyUrl = new Uri(baseUri, $"api?mode=history&limit=30&output=json&apikey={Uri.EscapeDataString(apiKey)}");
            using var response = await http.GetAsync(historyUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<SabHistoryResponse>(cancellationToken);
            return (payload?.History?.Slots ?? [])
                .Select(item => new DownloadClientHistoryItem(
                    Id: item.NzoId ?? item.Name ?? Guid.CreateVersion7().ToString("N"),
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: client.Protocol,
                    MediaType: InferMediaType(client, item.Category),
                    Title: CleanReleaseTitle(item.Name ?? "Unknown SABnzbd history item"),
                    ReleaseName: item.Name ?? "Unknown SABnzbd history item",
                    Category: item.Category ?? string.Empty,
                    Outcome: NormalizeHistoryOutcome(item.Status ?? string.Empty),
                    IndexerName: "SABnzbd",
                    SizeBytes: ParseSabHistorySize(item.Bytes),
                    CompletedUtc: FromUnix(item.Completed),
                    ErrorMessage: string.IsNullOrWhiteSpace(item.FailMessage) ? null : item.FailMessage,
                    SourcePath: item.Storage))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<DownloadClientHistoryItem>> TryGetNzbGetHistoryAsync(
        HttpClient http,
        DownloadClientItem client,
        Uri baseUri,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetHistoryItem>>>(
                http,
                new Uri(baseUri, "jsonrpc"),
                new NzbGetRequest("history", []),
                cancellationToken);

            return (response?.Result ?? [])
                .OrderByDescending(item => item.HistoryTime)
                .Take(30)
                .Select(item => new DownloadClientHistoryItem(
                    Id: item.NzbId.ToString(CultureInfo.InvariantCulture),
                    ClientId: client.Id,
                    ClientName: client.Name,
                    Protocol: client.Protocol,
                    MediaType: InferMediaType(client, item.Category),
                    Title: CleanReleaseTitle(item.NzbName ?? item.Name ?? "Unknown NZBGet history item"),
                    ReleaseName: item.NzbName ?? item.Name ?? "Unknown NZBGet history item",
                    Category: item.Category ?? string.Empty,
                    Outcome: NormalizeHistoryOutcome(item.Status ?? string.Empty),
                    IndexerName: "NZBGet",
                    SizeBytes: item.FileSizeHi * 1_000_000L,
                    CompletedUtc: FromUnix(item.HistoryTime),
                    ErrorMessage: MapQueueError(item.Status),
                    SourcePath: ResolveDownloadPath(item.DestDir, item.NzbName ?? item.Name)))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<NzbGetStatus?> TryGetNzbGetStatusAsync(
        HttpClient http,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostJsonAsync<NzbGetResponse<NzbGetStatus>>(
                http,
                new Uri(baseUri, "jsonrpc"),
                new NzbGetRequest("status", []),
                cancellationToken);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DownloadClientHistoryItem> CreateHistoryFromQueue(
        DownloadClientItem client,
        IEnumerable<DownloadQueueItem> queue,
        DateTimeOffset capturedUtc)
    {
        return queue
            .Where(item => item.Status is "completed" or "importReady" || !string.IsNullOrWhiteSpace(item.ErrorMessage))
            .OrderByDescending(item => item.AddedUtc)
            .Take(30)
            .Select(item => new DownloadClientHistoryItem(
                Id: item.Id,
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: client.Protocol,
                MediaType: item.MediaType,
                Title: item.Title,
                ReleaseName: item.ReleaseName,
                Category: item.Category,
                Outcome: !string.IsNullOrWhiteSpace(item.ErrorMessage)
                    ? "failed"
                    : item.Status == "importReady"
                        ? "importReady"
                        : "completed",
                IndexerName: item.IndexerName,
                SizeBytes: item.SizeBytes,
                CompletedUtc: item.Status is "completed" or "importReady" ? capturedUtc : item.AddedUtc,
                ErrorMessage: item.ErrorMessage,
                SourcePath: item.SourcePath))
            .ToArray();
    }

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        DownloadClientTelemetrySnapshot snapshot,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
        => CreateDispatchHistoryItem(
            snapshot.ClientId,
            snapshot.ClientName,
            snapshot.Protocol,
            dispatch,
            capturedUtc);

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        DownloadClientItem client,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
        => CreateDispatchHistoryItem(
            client.Id,
            client.Name,
            client.Protocol,
            dispatch,
            capturedUtc);

    private static DownloadClientHistoryItem CreateDispatchHistoryItem(
        string clientId,
        string clientName,
        string protocol,
        DownloadDispatchItem dispatch,
        DateTimeOffset capturedUtc)
    {
        return new DownloadClientHistoryItem(
            Id: dispatch.Id,
            ClientId: clientId,
            ClientName: clientName,
            Protocol: protocol,
            MediaType: dispatch.MediaType,
            Title: CleanReleaseTitle(dispatch.ReleaseName),
            ReleaseName: dispatch.ReleaseName,
            Category: dispatch.MediaType,
            Outcome: NormalizeHistoryOutcome(dispatch.Status),
            IndexerName: dispatch.IndexerName,
            SizeBytes: 0,
            CompletedUtc: dispatch.CreatedUtc == default ? capturedUtc : dispatch.CreatedUtc,
            ErrorMessage: dispatch.NotesJson);
    }

    private async Task<TransmissionResponse> SendTransmissionAsync(
        DownloadClientItem client,
        Uri baseUri,
        TransmissionRequest payload,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        AddBasicAuth(http, client);
        var uri = new Uri(baseUri, "transmission/rpc");
        using var first = await http.PostAsJsonAsync(uri, payload, cancellationToken);
        if ((int)first.StatusCode == 409 &&
            first.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            http.DefaultRequestHeaders.Remove("X-Transmission-Session-Id");
            http.DefaultRequestHeaders.Add("X-Transmission-Session-Id", values.First());
            using var second = await http.PostAsJsonAsync(uri, payload, cancellationToken);
            second.EnsureSuccessStatusCode();
            return await second.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new TransmissionResponse(null);
        }

        first.EnsureSuccessStatusCode();
        return await first.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new TransmissionResponse(null);
    }

    private static async Task DelugeLoginAsync(HttpClient http, Uri baseUri, DownloadClientItem client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Secret)) return;
        await PostJsonAsync<DelugeResponse<object>>(
            http,
            new Uri(baseUri, "json"),
            new DelugeRequest("auth.login", [client.Secret], 1),
            cancellationToken);
    }

    private static async Task<T?> PostJsonAsync<T>(HttpClient http, Uri uri, object payload, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(uri, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static void AddBasicAuth(HttpClient http, DownloadClientItem client)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)) return;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username ?? string.Empty}:{client.Secret ?? string.Empty}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", raw);
    }

    private static NetworkCredential? BuildCredential(DownloadClientItem client)
        => string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)
            ? null
            : new NetworkCredential(client.Username ?? string.Empty, client.Secret ?? string.Empty);

    private static async Task<string> GetUTorrentTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync("gui/token.html", cancellationToken);
        const string marker = ">";
        var start = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var end = html.LastIndexOf('<');
        return start >= 0 && end > start ? html[(start + 1)..end].Trim() : string.Empty;
    }

    private static int ParseId(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string JsonElementToString(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();

    private static double JsonElementToDouble(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0;

    private static long JsonElementToInt64(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0;

    private static DownloadClientActionResult MissingAddress(DownloadClientItem client, string queueItemId, string action)
        => new(client.Id, queueItemId, action, false, "Client address is missing.");

    private static DownloadClientActionResult Unsupported(DownloadClientItem client, string queueItemId, string action, string label)
        => new(client.Id, queueItemId, action, false, $"{label} does not support this action.");

    private static DownloadClientActionResult Success(DownloadClientItem client, string queueItemId, string action, string message)
        => new(client.Id, queueItemId, action, true, message);

    private static DownloadTelemetrySummary Summarize(IEnumerable<DownloadQueueItem> queue)
    {
        var items = queue.ToArray();
        return new DownloadTelemetrySummary(
            ActiveCount: items.Count(item => item.Status == "downloading"),
            QueuedCount: items.Count(item => item.Status == "queued"),
            CompletedCount: items.Count(item => item.Status == "completed"),
            StalledCount: items.Count(item => item.Status == "stalled"),
            ProcessingCount: items.Count(item => item.Status is "processing" or "processed" or "processingFailed" or "waitingForProcessor" or "importQueued"),
            ImportReadyCount: items.Count(item => item.Status is "importReady" or "completed"),
            TotalSpeedMbps: Math.Round(items.Sum(item => item.SpeedMbps), 1));
    }

    private static Uri? ResolveEndpoint(DownloadClientItem client)
    {
        if (!string.IsNullOrWhiteSpace(client.EndpointUrl) &&
            Uri.TryCreate(EnsureTrailingSlash(client.EndpointUrl), UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(client.Host))
        {
            return null;
        }

        var scheme = client.Host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? string.Empty : "http://";
        var port = client.Port is > 0 ? $":{client.Port}" : string.Empty;
        return Uri.TryCreate(EnsureTrailingSlash($"{scheme}{client.Host}{port}"), UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith('/') ? value : $"{value}/";

    private static string InferMediaType(DownloadClientItem client, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "movies";
        }

        if (!string.IsNullOrWhiteSpace(client.TvCategory) &&
            string.Equals(category, client.TvCategory, StringComparison.OrdinalIgnoreCase))
        {
            return "tv";
        }

        if (!string.IsNullOrWhiteSpace(client.MoviesCategory) &&
            string.Equals(category, client.MoviesCategory, StringComparison.OrdinalIgnoreCase))
        {
            return "movies";
        }

        var normalized = category.Trim().ToLowerInvariant();
        if (normalized.Contains("sonarr") ||
            normalized.Contains("series") ||
            normalized.Contains("show") ||
            normalized.Contains("tv"))
        {
            return "tv";
        }

        return "movies";
    }

    private static string? NormalizeAction(string action)
        => action.Trim().ToLowerInvariant() switch
        {
            "pause" => "pause",
            "resume" => "resume",
            "remove" or "delete" => "delete",
            "recheck" or "force-recheck" => "recheck",
            _ => null
        };

    private static string NormalizeHealth(string value)
        => value.Equals("healthy", StringComparison.OrdinalIgnoreCase) || value.Equals("ready", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : value.Equals("paused", StringComparison.OrdinalIgnoreCase) || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? "paused"
                : value.Equals("attention", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("degraded", StringComparison.OrdinalIgnoreCase) ||
                  value.Equals("untested", StringComparison.OrdinalIgnoreCase)
                    ? "degraded"
                    : value.Equals("unreachable", StringComparison.OrdinalIgnoreCase)
                        ? "down"
                        : "unknown";

    private static string NormalizeHistoryOutcome(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "completed" or "succeeded" or "success") return "completed";
        if (normalized.Contains("fail") || normalized.Contains("error")) return "failed";
        if (normalized.Contains("import")) return "importReady";
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private static string MapQbitState(string? state, double? progress)
    {
        var normalized = state?.ToLowerInvariant() ?? string.Empty;
        if ((progress ?? 0) >= 1 || normalized.Contains("upload")) return "importReady";
        if (normalized.Contains("pause") || normalized.Contains("queued")) return "queued";
        if (normalized.Contains("error") || normalized.Contains("stalled")) return "stalled";
        return "downloading";
    }

    private static string MapSabStatus(string? status, double progress)
    {
        var normalized = status?.ToLowerInvariant() ?? string.Empty;
        if (progress >= 99.9 || normalized.Contains("complete")) return "importReady";
        if (normalized.Contains("pause") || normalized.Contains("queued")) return "queued";
        if (normalized.Contains("fail") || normalized.Contains("error")) return "stalled";
        return "downloading";
    }

    private static string MapTransmissionStatus(int? status, double? progress, int? error, string? errorString)
    {
        if (error is > 0 || !string.IsNullOrWhiteSpace(errorString)) return "stalled";
        if ((progress ?? 0) >= 1) return "importReady";
        return status switch
        {
            0 => "queued",
            4 => "downloading",
            _ => "queued"
        };
    }

    private static string MapTextStatus(string? status, double? progress)
    {
        var normalized = status?.ToLowerInvariant() ?? string.Empty;
        if ((progress ?? 0) >= 99.9 || normalized.Contains("complete") || normalized.Contains("seeding")) return "importReady";
        if (normalized.Contains("pause") || normalized.Contains("queue")) return "queued";
        if (normalized.Contains("error") || normalized.Contains("fail") || normalized.Contains("stalled")) return "stalled";
        return "downloading";
    }

    private static string? MapQueueError(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized.Contains("fail") ||
               normalized.Contains("error") ||
               normalized.Contains("stall")
            ? status
            : null;
    }

    private static string CleanReleaseTitle(string value)
        => value.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Trim();

    private static string? ChoosePath(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ResolveDownloadPath(string? directory, string? name)
    {
        var cleanDirectory = directory?.Trim();
        var cleanName = name?.Trim();
        if (string.IsNullOrWhiteSpace(cleanDirectory))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return cleanDirectory;
        }

        if (cleanName.Contains('\\') || cleanName.Contains('/'))
        {
            return cleanName;
        }

        var separator = cleanDirectory.Contains('\\') ? "\\" : "/";
        return cleanDirectory.EndsWith('\\') || cleanDirectory.EndsWith('/')
            ? $"{cleanDirectory}{cleanName}"
            : $"{cleanDirectory}{separator}{cleanName}";
    }

    private static DateTimeOffset FromUnix(long? value)
        => value is > 0 ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : DateTimeOffset.UtcNow;

    private static long ParseSabSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var cleaned = value.Trim()
            .Replace("/s", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        var multiplier = 1d;
        if (cleaned.EndsWith("GB", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith("G", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1000d;
        }
        else if (cleaned.EndsWith("KB", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 0.001d;
        }
        else if (cleaned.EndsWith("B", StringComparison.OrdinalIgnoreCase) &&
                 !cleaned.EndsWith("MB", StringComparison.OrdinalIgnoreCase) &&
                 !cleaned.EndsWith("GB", StringComparison.OrdinalIgnoreCase) &&
                 !cleaned.EndsWith("KB", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 0.000001d;
        }

        cleaned = cleaned
            .Replace("GB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("MB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("KB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("G", "", StringComparison.OrdinalIgnoreCase)
            .Replace("M", "", StringComparison.OrdinalIgnoreCase)
            .Replace("K", "", StringComparison.OrdinalIgnoreCase)
            .Replace("B", "", StringComparison.OrdinalIgnoreCase);

        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Convert.ToInt64(parsed * multiplier)
            : 0;
    }

    private static int CalculateEtaSeconds(long remainingBytes, long? bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond is null or <= 0)
        {
            return 0;
        }

        return Convert.ToInt32(Math.Clamp(remainingBytes / (double)bytesPerSecond.Value, 0, int.MaxValue));
    }

    private static long ParseSabHistorySize(JsonElement? value)
    {
        if (value is null)
        {
            return 0;
        }

        var element = value.Value;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(element.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ParseEta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split(':').Select(part => int.TryParse(part, out var parsed) ? parsed : 0).ToArray();
        return parts.Length switch
        {
            3 => parts[0] * 3600 + parts[1] * 60 + parts[2],
            2 => parts[0] * 60 + parts[1],
            _ => 0
        };
    }

    private sealed record QbitTorrentItem(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("progress")] double? Progress,
        [property: JsonPropertyName("dlspeed")] long? DownloadSpeed,
        [property: JsonPropertyName("eta")] long? Eta,
        [property: JsonPropertyName("size")] long? Size,
        [property: JsonPropertyName("downloaded")] long? Downloaded,
        [property: JsonPropertyName("num_seeds")] int? NumSeeds,
        [property: JsonPropertyName("added_on")] long? AddedOn,
        [property: JsonPropertyName("save_path")] string? SavePath,
        [property: JsonPropertyName("content_path")] string? ContentPath);

    private sealed record SabQueueResponse(
        [property: JsonPropertyName("queue")] SabQueue? Queue);

    private sealed record SabQueue(
        [property: JsonPropertyName("speed")] string? Speed,
        [property: JsonPropertyName("slots")] IReadOnlyList<SabSlot>? Slots);

    private sealed record SabSlot(
        [property: JsonPropertyName("nzo_id")] string? NzoId,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("cat")] string? Category,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("mb")] string? Mb,
        [property: JsonPropertyName("mbleft")] string? Mbleft,
        [property: JsonPropertyName("timeleft")] string? TimeLeft);

    private sealed record SabHistoryResponse(
        [property: JsonPropertyName("history")] SabHistory? History);

    private sealed record SabHistory(
        [property: JsonPropertyName("slots")] IReadOnlyList<SabHistorySlot>? Slots);

    private sealed record SabHistorySlot(
        [property: JsonPropertyName("nzo_id")] string? NzoId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("bytes")] JsonElement? Bytes,
        [property: JsonPropertyName("completed")] long? Completed,
        [property: JsonPropertyName("fail_message")] string? FailMessage,
        [property: JsonPropertyName("storage")] string? Storage);

    private sealed record TransmissionRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("arguments")] Dictionary<string, object> Arguments);

    private sealed record TransmissionResponse(
        [property: JsonPropertyName("arguments")] TransmissionArguments? Arguments);

    private sealed record TransmissionArguments(
        [property: JsonPropertyName("torrents")] IReadOnlyList<TransmissionTorrent>? Torrents);

    private sealed record TransmissionTorrent(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("percentDone")] double? PercentDone,
        [property: JsonPropertyName("rateDownload")] long? RateDownload,
        [property: JsonPropertyName("eta")] int? Eta,
        [property: JsonPropertyName("totalSize")] long? TotalSize,
        [property: JsonPropertyName("downloadedEver")] long? DownloadedEver,
        [property: JsonPropertyName("peersConnected")] int? PeersConnected,
        [property: JsonPropertyName("addedDate")] long? AddedDate,
        [property: JsonPropertyName("doneDate")] long? DoneDate,
        [property: JsonPropertyName("downloadDir")] string? DownloadDir,
        [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels,
        [property: JsonPropertyName("error")] int? Error,
        [property: JsonPropertyName("errorString")] string? ErrorString);

    private sealed record DelugeRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params,
        [property: JsonPropertyName("id")] int Id);

    private sealed record DelugeResponse<T>(
        [property: JsonPropertyName("result")] T? Result);

    private sealed record DelugeUpdateResult(
        [property: JsonPropertyName("torrents")] IReadOnlyDictionary<string, DelugeTorrent>? Torrents);

    private sealed record DelugeTorrent(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("progress")] double? Progress,
        [property: JsonPropertyName("download_payload_rate")] double? DownloadPayloadRate,
        [property: JsonPropertyName("eta")] double? Eta,
        [property: JsonPropertyName("total_size")] double? TotalSize,
        [property: JsonPropertyName("total_done")] double? TotalDone,
        [property: JsonPropertyName("num_peers")] int? NumPeers,
        [property: JsonPropertyName("time_added")] double? TimeAdded,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("save_path")] string? SavePath);

    private sealed record NzbGetRequest(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object[] Params);

    private sealed record NzbGetResponse<T>(
        [property: JsonPropertyName("result")] T? Result);

    private sealed record NzbGetQueueItem(
        [property: JsonPropertyName("NZBID")] int NzbId,
        [property: JsonPropertyName("NZBName")] string? NzbName,
        [property: JsonPropertyName("Category")] string? Category,
        [property: JsonPropertyName("Status")] string? Status,
        [property: JsonPropertyName("FileSizeHi")] long FileSizeHi,
        [property: JsonPropertyName("RemainingSizeHi")] long RemainingSizeHi,
        [property: JsonPropertyName("DestDir")] string? DestDir);

    private sealed record NzbGetHistoryItem(
        [property: JsonPropertyName("NZBID")] int NzbId,
        [property: JsonPropertyName("NZBName")] string? NzbName,
        [property: JsonPropertyName("Name")] string? Name,
        [property: JsonPropertyName("Category")] string? Category,
        [property: JsonPropertyName("Status")] string? Status,
        [property: JsonPropertyName("FileSizeHi")] long FileSizeHi,
        [property: JsonPropertyName("HistoryTime")] long? HistoryTime,
        [property: JsonPropertyName("DestDir")] string? DestDir);

    private sealed record NzbGetStatus(
        [property: JsonPropertyName("DownloadRate")] long? DownloadRate);

    private sealed record UTorrentListResponse(
        [property: JsonPropertyName("torrents")] IReadOnlyList<IReadOnlyList<JsonElement>>? Torrents);
}
