using System.Globalization;
using System.Text.Json;
using Deluno.Platform.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform.Migration;

public sealed class MigrationAssistantService(
    IPlatformSettingsRepository repository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IConnectionsRepository connectionsRepository,
    IIntakeRepository intakeRepository,
    IEnumerable<IMigrationCatalogImporter>? catalogImporters = null) : IMigrationAssistantService
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public async Task<MigrationReport> PreviewAsync(MigrationImportRequest request, CancellationToken cancellationToken)
        => await BuildPreviewAsync(request, redactSensitiveData: true, cancellationToken);

    private async Task<MigrationReport> BuildPreviewAsync(
        MigrationImportRequest request,
        bool redactSensitiveData,
        CancellationToken cancellationToken)
    {
        var sourceKind = NormalizeSourceKind(request.SourceKind);
        var sourceName = NormalizeText(request.SourceName) ?? GetDefaultSourceName(sourceKind);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            errors.Add("Paste a Radarr, Sonarr, Prowlarr, Recyclarr, or compatible JSON export before previewing.");
            return BuildReport(sourceKind, sourceName, [], warnings, errors, 0, 0, 0, redactSensitiveData);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request.PayloadJson, DocumentOptions);
        }
        catch (JsonException ex)
        {
            errors.Add($"The migration payload is not valid JSON: {ex.Message}");
            return BuildReport(sourceKind, sourceName, [], warnings, errors, 0, 0, 0, redactSensitiveData);
        }

        using (document)
        {
            var existing = await ExistingState.LoadAsync(librariesRepository, qualityRepository, connectionsRepository, intakeRepository, cancellationToken);
            var operations = new List<MigrationReportOperation>();
            var contexts = ResolveContexts(document.RootElement, sourceKind).ToArray();

            if (contexts.Length == 0)
            {
                warnings.Add("No supported Radarr/Sonarr-style sections were found. Deluno looked for rootFolders, qualityProfiles, indexers, downloadClients, importLists, movies, and series.");
            }

            foreach (var context in contexts)
            {
                ExtractQualityProfiles(context, existing, operations);
                ExtractLibraries(context, existing, operations);
                ExtractIndexers(context, existing, operations);
                ExtractDownloadClients(context, existing, operations);
                ExtractIntakeSources(context, existing, operations);
            }

            var titleStats = ExtractTitleStats(contexts);
            if (titleStats.TitleCount > 0)
            {
                operations.Add(new MigrationReportOperation(
                    MakeOperationId("titles", sourceKind, "monitored-state"),
                    "catalog",
                    "monitored-state",
                    $"{titleStats.TitleCount.ToString(CultureInfo.InvariantCulture)} imported titles",
                    "report",
                    false,
                    "Deluno detected monitored/wanted state in the export. On apply, Deluno creates deduplicated catalog records only when it can safely map each title to one migrated library; existing files still require a later library scan to reconcile file associations.",
                    new Dictionary<string, string?>
                    {
                        ["titleCount"] = titleStats.TitleCount.ToString(CultureInfo.InvariantCulture),
                        ["monitoredCount"] = titleStats.MonitoredCount.ToString(CultureInfo.InvariantCulture),
                        ["wantedCount"] = titleStats.WantedCount.ToString(CultureInfo.InvariantCulture)
                    },
                    []));
            }

            return BuildReport(sourceKind, sourceName, operations, warnings, errors, titleStats.TitleCount, titleStats.MonitoredCount, titleStats.WantedCount, redactSensitiveData);
        }
    }

    public async Task<MigrationApplyResponse> ApplyAsync(MigrationImportRequest request, CancellationToken cancellationToken)
    {
        var report = await BuildPreviewAsync(request, redactSensitiveData: false, cancellationToken);
        if (!report.Valid)
        {
            return new MigrationApplyResponse(RedactReport(report), []);
        }

        var applied = new List<MigrationAppliedItem>();
        string? stageFailure = null;
        var selectedOperationIds = request.SelectedOperationIds is { Count: > 0 }
            ? request.SelectedOperationIds.ToHashSet(StringComparer.Ordinal)
            : null;
        var isSelected = (MigrationReportOperation operation) =>
            selectedOperationIds is null || selectedOperationIds.Contains(operation.Id);

        foreach (var operation in report.Operations.Where(operation => operation.CanApply && operation.Action == "create" && isSelected(operation)))
        {
            try
            {
            switch (operation.TargetType)
            {
                case "quality-profile":
                {
                    var created = await qualityRepository.CreateQualityProfileAsync(
                        new CreateQualityProfileRequest(
                            GetData(operation, "name"),
                            GetData(operation, "mediaType"),
                            GetData(operation, "cutoffQuality"),
                            GetData(operation, "allowedQualities"),
                            CustomFormatIds: null,
                            UpgradeUntilCutoff: ParseBool(GetData(operation, "upgradeUntilCutoff"), defaultValue: true),
                            UpgradeUnknownItems: ParseBool(GetData(operation, "upgradeUnknownItems"), defaultValue: false)),
                        cancellationToken);
                    applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, created.Id, "created"));
                    break;
                }
                case "library":
                {
                    var mediaType = GetData(operation, "mediaType");
                    var matchingProfiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
                    var profileId = matchingProfiles.FirstOrDefault(profile =>
                        string.Equals(profile.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))?.Id;
                    var created = await librariesRepository.CreateLibraryAsync(
                        new CreateLibraryRequest(
                            GetData(operation, "name"),
                            mediaType,
                            GetData(operation, "purpose"),
                            GetData(operation, "rootPath"),
                            DownloadsPath: null,
                            QualityProfileId: profileId,
                            ImportWorkflow: "standard",
                            ProcessorName: null,
                            ProcessorOutputPath: null,
                            ProcessorTimeoutMinutes: null,
                            ProcessorFailureMode: null,
                            AutoSearchEnabled: true,
                            MissingSearchEnabled: true,
                            UpgradeSearchEnabled: true,
                            SearchIntervalHours: 6,
                            RetryDelayHours: 3,
                            MaxItemsPerRun: 10),
                        cancellationToken);
                    applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, created.Id, "created"));
                    break;
                }
                case "indexer":
                {
                    var created = await connectionsRepository.CreateIndexerAsync(
                        new CreateIndexerRequest(
                            GetData(operation, "name"),
                            GetData(operation, "protocol"),
                            GetData(operation, "privacy"),
                            GetData(operation, "baseUrl"),
                            GetData(operation, "apiKey"),
                            ParseInt(GetData(operation, "priority"), 100),
                            GetData(operation, "categories"),
                            GetData(operation, "tags"),
                            GetData(operation, "mediaScope"),
                            ParseBool(GetData(operation, "isEnabled"), defaultValue: true)),
                        cancellationToken);
                    applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, created.Id, "created"));
                    break;
                }
                case "download-client":
                {
                    var created = await connectionsRepository.CreateDownloadClientAsync(
                        new CreateDownloadClientRequest(
                            GetData(operation, "name"),
                            GetData(operation, "protocol"),
                            GetData(operation, "host"),
                            ParseNullableInt(GetData(operation, "port")),
                            GetData(operation, "username"),
                            GetData(operation, "password"),
                            GetData(operation, "endpointUrl"),
                            GetData(operation, "moviesCategory"),
                            GetData(operation, "tvCategory"),
                            GetData(operation, "categoryTemplate"),
                            ParseInt(GetData(operation, "priority"), 100),
                            ParseBool(GetData(operation, "isEnabled"), defaultValue: true)),
                        cancellationToken);
                    applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, created.Id, "created"));
                    break;
                }
                case "intake-source":
                {
                    var created = await intakeRepository.CreateIntakeSourceAsync(
                        new CreateIntakeSourceRequest(
                            GetData(operation, "name") ?? operation.Name,
                            GetData(operation, "provider") ?? "rss",
                            GetData(operation, "feedUrl") ?? string.Empty,
                            GetData(operation, "mediaType"),
                            LibraryId: null,
                            QualityProfileId: null,
                            RequiredGenres: null,
                            MinimumRating: null,
                            MinimumYear: null,
                            MaximumAgeDays: null,
                            AllowedCertifications: null,
                            Audience: null,
                            SyncIntervalHours: null,
                            SearchOnAdd: ParseBool(GetData(operation, "searchOnAdd"), defaultValue: true),
                            IsEnabled: ParseBool(GetData(operation, "isEnabled"), defaultValue: true)),
                        cancellationToken);
                    applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, created.Id, "created"));
                    break;
                }
            }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, string.Empty, "failed"));
                stageFailure = $"Migration stopped while creating {operation.TargetType}. Items listed as created are already saved; no external media was changed. Review the audit, then retry and Deluno will skip matching saved items.";
                break;
            }
        }

        var catalogWarnings = new List<string>();
        var catalogTitles = ExtractCatalogTitles(request);
        var catalogOperation = report.Operations.FirstOrDefault(operation =>
            operation.Category == "catalog" && operation.TargetType == "monitored-state");
        if (stageFailure is null && catalogTitles.Count > 0 && (catalogOperation is null || isSelected(catalogOperation)))
        {
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var catalogRequest = new MigrationCatalogImportRequest(
                report.SourceKind,
                report.SourceName,
                catalogTitles,
                libraries.Select(library => new MigrationCatalogLibrary(
                    library.Id,
                    library.MediaType,
                    library.RootPath,
                    library.Name)).ToArray());
            foreach (var importer in catalogImporters ?? [])
            {
                try
                {
                    var imported = await importer.ImportAsync(catalogRequest, cancellationToken);
                    applied.AddRange(imported.Applied);
                    catalogWarnings.AddRange(imported.Warnings);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    applied.Add(new MigrationAppliedItem(catalogOperation?.Id ?? "catalog", importer.MediaType, $"{importer.MediaType} catalog", string.Empty, "failed"));
                    stageFailure = $"Migration stopped while importing {importer.MediaType} catalogue records. Items listed as created are already saved; no external media was changed. Review the audit, then retry and Deluno will skip matching saved items.";
                    break;
                }
            }
        }

        var preflight = RedactReport(report);
        var afterApply = await BuildPreviewAsync(request, redactSensitiveData: true, cancellationToken);
        if (catalogWarnings.Count > 0)
        {
            afterApply = afterApply with { Warnings = afterApply.Warnings.Concat(catalogWarnings).ToArray() };
        }
        if (stageFailure is not null)
        {
            afterApply = afterApply with { Errors = afterApply.Errors.Concat([stageFailure]).ToArray() };
        }
        var audit = await repository.RecordMigrationAuditReportAsync(
            new MigrationAuditReport(
                Id: string.Empty,
                SourceKind: preflight.SourceKind,
                SourceName: preflight.SourceName,
                AppliedUtc: DateTimeOffset.MinValue,
                PreflightReport: preflight,
                ResultReport: afterApply,
                Applied: applied),
            cancellationToken);
        return new MigrationApplyResponse(afterApply, applied, audit.Id);
    }

    private static void ExtractQualityProfiles(MigrationContext context, ExistingState existing, List<MigrationReportOperation> operations)
    {
        foreach (var item in EnumerateArrays(context.Root, "qualityProfiles", "profiles"))
        {
            var name = ReadString(item, "name") ?? ReadString(item, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                operations.Add(Unsupported(context, "quality-profile", "Unnamed quality profile", "Quality profile is missing a name."));
                continue;
            }

            var mediaType = context.MediaType;
            var cutoffQuality = ResolveCutoffQuality(item) ?? DefaultCutoff(mediaType);
            var allowedQualities = ResolveAllowedQualities(item);
            if (string.IsNullOrWhiteSpace(allowedQualities))
            {
                allowedQualities = DefaultAllowedQualities(mediaType);
            }

            var data = new Dictionary<string, string?>
            {
                ["name"] = name,
                ["mediaType"] = mediaType,
                ["cutoffQuality"] = cutoffQuality,
                ["allowedQualities"] = allowedQualities,
                ["upgradeUntilCutoff"] = "true",
                ["upgradeUnknownItems"] = "false"
            };

            var nameKey = MakeKey(mediaType, name);
            if (existing.QualityProfilesByKey.TryGetValue(nameKey, out var existingProfile) &&
                (!string.Equals(existingProfile.CutoffQuality, cutoffQuality, StringComparison.OrdinalIgnoreCase) ||
                 !SameCsvValues(existingProfile.AllowedQualities, allowedQualities)))
            {
                operations.Add(Conflict(context, "quality-profile", name, "A quality profile with this name already exists but its cutoff or allowed qualities differ. Deluno will not overwrite it silently.", data));
                continue;
            }

            operations.Add(PlanCreateOrSkip(
                context,
                existing.QualityProfileKeys,
                "quality",
                "quality-profile",
                name,
                nameKey,
                "Quality profile will be mapped into Deluno cutoff and allowed quality policy.",
                "A quality profile with this name and media type already exists.",
                data,
                []));
        }
    }

    private static void ExtractLibraries(MigrationContext context, ExistingState existing, List<MigrationReportOperation> operations)
    {
        foreach (var item in EnumerateArrays(context.Root, "rootFolders", "rootfolders", "rootFolderPaths"))
        {
            var path = ReadString(item, "path") ?? ReadString(item, "rootFolderPath");
            if (string.IsNullOrWhiteSpace(path))
            {
                operations.Add(Unsupported(context, "library", "Unnamed root folder", "Root folder entry is missing a path."));
                continue;
            }

            var name = ReadString(item, "name") ?? $"{MediaTypeLabel(context.MediaType)} - {path}";
            var data = new Dictionary<string, string?>
            {
                ["name"] = name,
                ["mediaType"] = context.MediaType,
                ["purpose"] = $"Migrated from {context.SourceLabel}",
                ["rootPath"] = path
            };

            var libraryNameKey = MakeKey(context.MediaType, name);
            if (existing.LibraryNamesByMedia.TryGetValue(libraryNameKey, out var existingRoot) &&
                !string.Equals(existingRoot, path, StringComparison.OrdinalIgnoreCase))
            {
                operations.Add(Conflict(context, "library", name, "A library with this name already exists but points at a different root folder. Rename the incoming root or review it manually.", data));
                continue;
            }

            operations.Add(PlanCreateOrSkip(
                context,
                existing.LibraryRootKeys,
                "library",
                "library",
                name,
                MakeKey(context.MediaType, path),
                "Root folder will become a Deluno library with safe default automation.",
                "A library with this media type and root path already exists.",
                data,
                IsPathLikelyContainerSpecific(path)
                    ? ["This path may be container-specific. Confirm Docker path mappings before applying."]
                    : []));
        }
    }

    private static void ExtractIndexers(MigrationContext context, ExistingState existing, List<MigrationReportOperation> operations)
    {
        foreach (var item in EnumerateArrays(context.Root, "indexers", "indexerSources"))
        {
            var name = ReadString(item, "name") ?? ReadString(item, "definitionName");
            var baseUrl = ReadString(item, "baseUrl") ?? ReadString(item, "url") ?? ReadString(item, "link");
            if (string.IsNullOrWhiteSpace(name))
            {
                operations.Add(Unsupported(context, "indexer", "Unnamed indexer", "Indexer entry is missing a name."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                operations.Add(Unsupported(context, "indexer", name, "Indexer is missing a base URL. Deluno cannot create a usable source."));
                continue;
            }

            var protocol = NormalizeIndexerProtocol(ReadString(item, "protocol") ?? ReadString(item, "implementation"));
            var mediaScope = context.SourceKind == "prowlarr" ? "both" : context.MediaType;
            var data = new Dictionary<string, string?>
            {
                ["name"] = name,
                ["protocol"] = protocol,
                ["privacy"] = ReadString(item, "privacy") ?? "private",
                ["baseUrl"] = baseUrl,
                ["apiKey"] = ReadFieldValue(item, "apiKey"),
                ["priority"] = ReadInt(item, "priority")?.ToString(CultureInfo.InvariantCulture) ?? "100",
                ["categories"] = ResolveCategories(item, context.MediaType),
                ["tags"] = ResolveTags(item),
                ["mediaScope"] = mediaScope,
                ["isEnabled"] = (!ReadBool(item, "enable").HasValue || ReadBool(item, "enable") == true).ToString(CultureInfo.InvariantCulture)
            };

            if (existing.IndexersByName.TryGetValue(MakeKey(name), out var existingEndpoint) &&
                !string.Equals(existingEndpoint, MakeKey(protocol, baseUrl), StringComparison.Ordinal))
            {
                operations.Add(Conflict(context, "indexer", name, "An indexer with this name already exists but points at a different protocol or URL. Deluno will not guess which one should win.", data));
                continue;
            }

            operations.Add(PlanCreateOrSkip(
                context,
                existing.IndexerKeys,
                "source",
                "indexer",
                name,
                MakeKey(protocol, baseUrl),
                "Indexer will become a Deluno search source with imported categories and scope.",
                "An indexer with this protocol and URL already exists.",
                data,
                string.IsNullOrWhiteSpace(data["apiKey"]) ? ["No API key was present. Deluno will create the source as untested and it must be completed later."] : []));
        }
    }

    private static void ExtractDownloadClients(MigrationContext context, ExistingState existing, List<MigrationReportOperation> operations)
    {
        foreach (var item in EnumerateArrays(context.Root, "downloadClients", "downloadclients", "clients"))
        {
            var name = ReadString(item, "name") ?? ReadString(item, "implementationName");
            if (string.IsNullOrWhiteSpace(name))
            {
                operations.Add(Unsupported(context, "download-client", "Unnamed download client", "Download client entry is missing a name."));
                continue;
            }

            var protocol = NormalizeDownloadProtocol(ReadString(item, "protocol") ?? ReadString(item, "implementation") ?? name);
            var host = ReadFieldValue(item, "host") ?? ExtractFieldValue(item, "host");
            var port = ReadInt(item, "port")?.ToString(CultureInfo.InvariantCulture) ?? ExtractFieldValue(item, "port");
            var endpoint = ReadString(item, "url") ?? ReadString(item, "baseUrl") ?? ReadString(item, "endpointUrl");
            var category = ExtractFieldValue(item, "category") ?? ExtractFieldValue(item, "tvCategory") ?? ExtractFieldValue(item, "movieCategory");

            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(endpoint))
            {
                operations.Add(Unsupported(context, "download-client", name, "Download client is missing host or endpoint URL."));
                continue;
            }

            var data = new Dictionary<string, string?>
            {
                ["name"] = name,
                ["protocol"] = protocol,
                ["host"] = host,
                ["port"] = port,
                ["username"] = ExtractFieldValue(item, "username"),
                ["password"] = ExtractFieldValue(item, "password") ?? ExtractFieldValue(item, "apiKey"),
                ["endpointUrl"] = endpoint,
                ["moviesCategory"] = context.MediaType == "movies" ? category : null,
                ["tvCategory"] = context.MediaType == "tv" ? category : null,
                ["categoryTemplate"] = category,
                ["priority"] = ReadInt(item, "priority")?.ToString(CultureInfo.InvariantCulture) ?? "100",
                ["isEnabled"] = (!ReadBool(item, "enable").HasValue || ReadBool(item, "enable") == true).ToString(CultureInfo.InvariantCulture)
            };

            if (existing.DownloadClientsByName.TryGetValue(MakeKey(name), out var existingEndpoint) &&
                !string.Equals(existingEndpoint, MakeKey(protocol, endpoint ?? host ?? name), StringComparison.Ordinal))
            {
                operations.Add(Conflict(context, "download-client", name, "A download client with this name already exists but points at a different host or endpoint. Deluno will not overwrite the existing client.", data));
                continue;
            }

            operations.Add(PlanCreateOrSkip(
                context,
                existing.DownloadClientKeys,
                "client",
                "download-client",
                name,
                MakeKey(protocol, endpoint ?? host ?? name),
                "Download client will be imported with media-specific category context.",
                "A download client with this protocol and endpoint already exists.",
                data,
                string.IsNullOrWhiteSpace(category) ? ["No category was detected. Deluno will import the client, but routing categories should be reviewed."] : []));
        }
    }

    private static void ExtractIntakeSources(MigrationContext context, ExistingState existing, List<MigrationReportOperation> operations)
    {
        foreach (var item in EnumerateArrays(context.Root, "importLists", "lists", "intakeSources"))
        {
            var name = ReadString(item, "name") ?? ReadString(item, "implementationName");
            if (string.IsNullOrWhiteSpace(name))
            {
                operations.Add(Unsupported(context, "intake-source", "Unnamed intake source", "Intake source is missing a name."));
                continue;
            }

            var provider = NormalizeProvider(ReadString(item, "implementation") ?? ReadString(item, "provider") ?? name);
            var feedUrl = ResolveIntakeFeedUrl(item, provider);
            if (string.IsNullOrWhiteSpace(feedUrl))
            {
                operations.Add(Unsupported(context, "intake-source", name, $"{ProviderLabel(provider)} import list has no supported public URL or stable list identifier."));
                continue;
            }

            var data = new Dictionary<string, string?>
            {
                ["name"] = name,
                ["provider"] = provider,
                ["feedUrl"] = feedUrl,
                ["mediaType"] = context.MediaType,
                ["searchOnAdd"] = (!ReadBool(item, "searchOnAdd").HasValue || ReadBool(item, "searchOnAdd") == true).ToString(CultureInfo.InvariantCulture),
                ["isEnabled"] = (!ReadBool(item, "enable").HasValue || ReadBool(item, "enable") == true).ToString(CultureInfo.InvariantCulture)
            };

            if (existing.IntakeSourcesByName.TryGetValue(MakeKey(context.MediaType, name), out var existingFeed) &&
                !string.Equals(existingFeed, MakeKey(provider, feedUrl), StringComparison.Ordinal))
            {
                operations.Add(Conflict(context, "intake-source", name, "An intake source with this name already exists but uses a different provider or feed. Deluno will not overwrite it.", data));
                continue;
            }

            operations.Add(PlanCreateOrSkip(
                context,
                existing.IntakeSourceKeys,
                "automation",
                "intake-source",
                name,
                MakeKey(context.MediaType, provider, feedUrl),
                "External list source will become a Deluno intake source.",
                "An intake source with this media type, provider, and feed already exists.",
                data,
                []));
        }
    }

    private static TitleStats ExtractTitleStats(IEnumerable<MigrationContext> contexts)
    {
        var titleCount = 0;
        var monitoredCount = 0;
        var wantedCount = 0;

        foreach (var context in contexts)
        {
            foreach (var item in EnumerateArrays(context.Root, "movies", "series", "shows", "titles"))
            {
                titleCount++;
                if (ReadBool(item, "monitored") == true)
                {
                    monitoredCount++;
                }

                if (ReadBool(item, "hasFile") == false || ReadBool(item, "downloaded") == false || ReadBool(item, "missing") == true)
                {
                    wantedCount++;
                }
            }
        }

        return new TitleStats(titleCount, monitoredCount, wantedCount);
    }

    private static IReadOnlyList<MigrationCatalogTitle> ExtractCatalogTitles(MigrationImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(request.PayloadJson, DocumentOptions);
            var sourceKind = NormalizeSourceKind(request.SourceKind);
            var titles = new List<MigrationCatalogTitle>();
            foreach (var context in ResolveContexts(document.RootElement, sourceKind))
            {
                if (context.MediaType is not ("movies" or "tv"))
                {
                    continue;
                }

                foreach (var item in EnumerateArrays(context.Root, "movies", "series", "shows", "titles"))
                {
                    var title = ReadString(item, "title") ?? ReadString(item, "name") ?? ReadString(item, "sortTitle");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var tmdbId = ReadString(item, "tmdbId") ?? ReadString(item, "tmdb_id");
                    titles.Add(new MigrationCatalogTitle(
                        context.MediaType,
                        title,
                        ReadInt(item, "year") ?? ReadInt(item, "releaseYear") ?? ReadInt(item, "startYear"),
                        ReadString(item, "imdbId") ?? ReadString(item, "imdb_id"),
                        string.IsNullOrWhiteSpace(tmdbId) ? null : "tmdb",
                        tmdbId,
                        ReadBool(item, "monitored") ?? true,
                        ReadBool(item, "hasFile") == true,
                        ReadString(item, "rootFolderPath") ?? ReadString(item, "rootPath")));
                }
            }

            return titles;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static MigrationReport BuildReport(
        string sourceKind,
        string sourceName,
        IReadOnlyList<MigrationReportOperation> operations,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        int titleCount,
        int monitoredCount,
        int wantedCount,
        bool redactSensitiveData)
    {
        var summary = new MigrationReportSummary(
            CreateCount: operations.Count(operation => operation.Action == "create"),
            SkipCount: operations.Count(operation => operation.Action == "skip"),
            ConflictCount: operations.Count(operation => operation.Action == "conflict"),
            UnsupportedCount: operations.Count(operation => operation.Action == "unsupported"),
            WarningCount: warnings.Count + operations.Sum(operation => operation.Warnings.Count),
            TitleCount: titleCount,
            MonitoredCount: monitoredCount,
            WantedCount: wantedCount);

        return new MigrationReport(
            sourceKind,
            sourceName,
            errors.Count == 0,
            summary,
            redactSensitiveData ? operations.Select(RedactOperation).ToArray() : operations,
            warnings,
            errors);
    }

    private static MigrationReport RedactReport(MigrationReport report)
        => report with { Operations = report.Operations.Select(RedactOperation).ToArray() };

    private static MigrationReportOperation RedactOperation(MigrationReportOperation operation)
    {
        var data = operation.Data.ToDictionary(
            pair => pair.Key,
            pair => IsSensitiveKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value) ? "[redacted]" : pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return operation with { Data = data };
    }

    private static bool IsSensitiveKey(string key)
        => key.Contains("api", StringComparison.OrdinalIgnoreCase) && key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<MigrationContext> ResolveContexts(JsonElement root, string sourceKind)
    {
        var emitted = false;
        foreach (var propertyName in new[] { "radarr", "sonarr", "prowlarr", "recyclarr" })
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var nested))
            {
                emitted = true;
                yield return new MigrationContext(propertyName, GetMediaType(propertyName), GetDefaultSourceName(propertyName), nested);
            }
        }

        if (!emitted)
        {
            yield return new MigrationContext(sourceKind, GetMediaType(sourceKind), GetDefaultSourceName(sourceKind), root);
        }
    }

    private static IEnumerable<JsonElement> EnumerateArrays(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var acceptedNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (acceptedNames.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    yield return item;
                }
            }
        }
    }

    private static MigrationReportOperation PlanCreateOrSkip(
        MigrationContext context,
        IReadOnlySet<string> existingKeys,
        string category,
        string targetType,
        string name,
        string key,
        string createReason,
        string skipReason,
        IReadOnlyDictionary<string, string?> data,
        IReadOnlyList<string> warnings)
    {
        var exists = existingKeys.Contains(key);
        return new MigrationReportOperation(
            MakeOperationId(targetType, context.SourceKind, key),
            category,
            targetType,
            name,
            exists ? "skip" : "create",
            !exists,
            exists ? skipReason : createReason,
            data,
            warnings);
    }

    private static MigrationReportOperation Unsupported(MigrationContext context, string targetType, string name, string reason)
    {
        return new MigrationReportOperation(
            MakeOperationId(targetType, context.SourceKind, name),
            "validation",
            targetType,
            name,
            "unsupported",
            false,
            reason,
            new Dictionary<string, string?>(),
            []);
    }

    private static MigrationReportOperation Conflict(
        MigrationContext context,
        string targetType,
        string name,
        string reason,
        IReadOnlyDictionary<string, string?> data)
    {
        return new MigrationReportOperation(
            MakeOperationId(targetType, context.SourceKind, name),
            "conflict",
            targetType,
            name,
            "conflict",
            false,
            reason,
            data,
            []);
    }

    private static string? ResolveCutoffQuality(JsonElement item)
    {
        if (ReadString(item, "cutoff") is { Length: > 0 } cutoffText && !int.TryParse(cutoffText, out _))
        {
            return cutoffText;
        }

        var cutoffId = ReadInt(item, "cutoff");
        if (cutoffId is null)
        {
            return ReadString(item, "cutoffQuality");
        }

        return EnumerateQualityItems(item)
            .FirstOrDefault(quality => quality.Id == cutoffId)?.Name;
    }

    private static string ResolveAllowedQualities(JsonElement item)
    {
        var qualities = EnumerateQualityItems(item)
            .Where(quality => quality.Allowed)
            .Select(quality => quality.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (qualities.Length > 0)
        {
            return string.Join(", ", qualities);
        }

        return ReadString(item, "allowedQualities") ?? string.Empty;
    }

    private static IEnumerable<ImportedQuality> EnumerateQualityItems(JsonElement item)
    {
        if (!TryGetPropertyCaseInsensitive(item, "items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var wrapper in items.EnumerateArray())
        {
            var allowed = ReadBool(wrapper, "allowed") ?? true;
            if (TryGetPropertyCaseInsensitive(wrapper, "quality", out var quality))
            {
                yield return new ImportedQuality(
                    ReadInt(quality, "id"),
                    ReadString(quality, "name") ?? string.Empty,
                    allowed);
            }
            else
            {
                yield return new ImportedQuality(
                    ReadInt(wrapper, "id"),
                    ReadString(wrapper, "name") ?? string.Empty,
                    allowed);
            }
        }
    }

    private static string ResolveCategories(JsonElement item, string mediaType)
    {
        if (ReadString(item, "categories") is { Length: > 0 } categories)
        {
            return categories;
        }

        if (TryGetPropertyCaseInsensitive(item, "categories", out var categoryArray) && categoryArray.ValueKind == JsonValueKind.Array)
        {
            return string.Join(",", categoryArray.EnumerateArray().Select(ReadElementAsString).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return mediaType == "tv" ? "5000,5030,5040" : mediaType == "movies" ? "2000,2010,2040" : "2000,5000";
    }

    private static string ResolveTags(JsonElement item)
    {
        if (ReadString(item, "tags") is { Length: > 0 } tags)
        {
            return tags;
        }

        if (TryGetPropertyCaseInsensitive(item, "tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array)
        {
            return string.Join(",", tagArray.EnumerateArray().Select(ReadElementAsString).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return string.Empty;
    }

    private static string? ExtractFieldValue(JsonElement item, string fieldName)
    {
        if (!TryGetPropertyCaseInsensitive(item, "fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (string.Equals(ReadString(field, "name"), fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadFieldValue(field, "value") ?? ReadElementAsString(field);
            }
        }

        return null;
    }

    private static string? ReadFieldValue(JsonElement item, string name)
    {
        if (!TryGetPropertyCaseInsensitive(item, name, out var value))
        {
            return null;
        }

        return ReadElementAsString(value);
    }

    private static string? ReadString(JsonElement item, string name)
    {
        if (!TryGetPropertyCaseInsensitive(item, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? NormalizeText(value.GetString()) : ReadElementAsString(value);
    }

    private static int? ReadInt(JsonElement item, string name)
    {
        if (!TryGetPropertyCaseInsensitive(item, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(ReadElementAsString(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? ReadBool(JsonElement item, string name)
    {
        if (!TryGetPropertyCaseInsensitive(item, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement item, string name, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadElementAsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeText(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string NormalizeSourceKind(string? value)
    {
        var normalized = NormalizeToken(value);
        return normalized is "radarr" or "sonarr" or "prowlarr" or "recyclarr" ? normalized : "custom";
    }

    private static string GetMediaType(string sourceKind)
    {
        return sourceKind switch
        {
            "sonarr" => "tv",
            "radarr" => "movies",
            _ => "both"
        };
    }

    private static string GetDefaultSourceName(string sourceKind)
    {
        return sourceKind switch
        {
            "radarr" => "Radarr",
            "sonarr" => "Sonarr",
            "prowlarr" => "Prowlarr",
            "recyclarr" => "Recyclarr",
            _ => "External stack"
        };
    }

    private static string NormalizeIndexerProtocol(string? value)
    {
        var normalized = NormalizeToken(value);
        return normalized.Contains("usenet", StringComparison.OrdinalIgnoreCase) || normalized.Contains("newznab", StringComparison.OrdinalIgnoreCase)
            ? "usenet"
            : "torrent";
    }

    private static string NormalizeDownloadProtocol(string? value)
    {
        var normalized = NormalizeToken(value);
        if (normalized.Contains("sab", StringComparison.OrdinalIgnoreCase)) return "sabnzbd";
        if (normalized.Contains("nzbget", StringComparison.OrdinalIgnoreCase)) return "nzbget";
        if (normalized.Contains("transmission", StringComparison.OrdinalIgnoreCase)) return "transmission";
        if (normalized.Contains("deluge", StringComparison.OrdinalIgnoreCase)) return "deluge";
        if (normalized.Contains("utorrent", StringComparison.OrdinalIgnoreCase) || normalized.Contains("torrentblackhole", StringComparison.OrdinalIgnoreCase)) return "utorrent";
        return "qbittorrent";
    }

    private static string NormalizeProvider(string? value)
    {
        var normalized = NormalizeToken(value);
        if (normalized.Contains("mdblist", StringComparison.OrdinalIgnoreCase)) return "mdblist";
        if (normalized.Contains("letterboxd", StringComparison.OrdinalIgnoreCase)) return "letterboxd";
        if (normalized.Contains("trakt", StringComparison.OrdinalIgnoreCase)) return "trakt";
        if (normalized.Contains("imdb", StringComparison.OrdinalIgnoreCase)) return "imdb";
        if (normalized.Contains("tmdb", StringComparison.OrdinalIgnoreCase)) return "tmdb";
        if (normalized.Contains("rss", StringComparison.OrdinalIgnoreCase)) return "rss";
        return "url-list";
    }

    private static string? ResolveIntakeFeedUrl(JsonElement item, string provider)
    {
        var address = ReadString(item, "url")
                      ?? ReadString(item, "link")
                      ?? ExtractFieldValue(item, "listUrl")
                      ?? ExtractFieldValue(item, "url");
        if (!string.IsNullOrWhiteSpace(address))
        {
            return address.Trim();
        }

        var listId = ExtractFieldValue(item, "listId")
                     ?? ExtractFieldValue(item, "list")
                     ?? ReadString(item, "listId");
        if (string.IsNullOrWhiteSpace(listId))
        {
            return null;
        }

        var trimmed = listId.Trim();
        return provider switch
        {
            "tmdb" when trimmed.All(char.IsDigit) => trimmed,
            "imdb" when trimmed.Length > 2 && trimmed.StartsWith("ls", StringComparison.OrdinalIgnoreCase) && trimmed[2..].All(char.IsDigit) => trimmed,
            _ => null
        };
    }

    private static string ProviderLabel(string provider)
        => provider switch
        {
            "tmdb" => "TMDb",
            "imdb" => "IMDb",
            "trakt" => "Trakt",
            "mdblist" => "MDbList",
            "letterboxd" => "Letterboxd",
            "rss" => "RSS",
            _ => "This"
        };

    private static string NormalizeToken(string? value)
    {
        return (NormalizeText(value) ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string MakeKey(params string?[] parts)
    {
        return string.Join("|", parts.Select(part => (part ?? string.Empty).Trim().ToLowerInvariant()));
    }

    private static string MakeOperationId(string targetType, string sourceKind, string key)
    {
        var safeKey = new string(MakeKey(sourceKind, key).Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        return $"{targetType}-{safeKey}".Trim('-');
    }

    private static bool SameCsvValues(string left, string right)
    {
        static string[] Split(string value) => value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Split(left).SequenceEqual(Split(right), StringComparer.OrdinalIgnoreCase);
    }

    private static string DefaultCutoff(string mediaType)
    {
        return mediaType == "tv" ? "WEB 1080p" : "WEB 1080p";
    }

    private static string DefaultAllowedQualities(string mediaType)
    {
        return mediaType == "tv"
            ? "WEB 720p, WEB 1080p, HDTV 1080p"
            : "WEB 1080p, Bluray 1080p, WEB 2160p, Bluray 2160p";
    }

    private static string MediaTypeLabel(string mediaType)
    {
        return mediaType == "tv" ? "TV" : mediaType == "movies" ? "Movies" : "Media";
    }

    private static bool IsPathLikelyContainerSpecific(string path)
    {
        return path.StartsWith("/config", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/data", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/media", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetData(MigrationReportOperation operation, string key)
    {
        return operation.Data.TryGetValue(key, out var value) ? value : null;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private sealed record MigrationContext(string SourceKind, string MediaType, string SourceLabel, JsonElement Root);

    private sealed record ImportedQuality(int? Id, string Name, bool Allowed);

    private sealed record TitleStats(int TitleCount, int MonitoredCount, int WantedCount);

    private sealed record ExistingState(
        IReadOnlySet<string> QualityProfileKeys,
        IReadOnlySet<string> LibraryRootKeys,
        IReadOnlySet<string> IndexerKeys,
        IReadOnlySet<string> DownloadClientKeys,
        IReadOnlySet<string> IntakeSourceKeys,
        IReadOnlyDictionary<string, QualityProfileItem> QualityProfilesByKey,
        IReadOnlyDictionary<string, string> LibraryNamesByMedia,
        IReadOnlyDictionary<string, string> IndexersByName,
        IReadOnlyDictionary<string, string> DownloadClientsByName,
        IReadOnlyDictionary<string, string> IntakeSourcesByName)
    {
        public static async Task<ExistingState> LoadAsync(
            ILibrariesRepository repository,
            IQualityRepository qualityRepository,
            IConnectionsRepository connectionsRepository,
            IIntakeRepository intakeRepository,
            CancellationToken cancellationToken)
        {
            var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
            var libraries = await repository.ListLibrariesAsync(cancellationToken);
            var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
            var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
            var intakeSources = await intakeRepository.ListIntakeSourcesAsync(cancellationToken);

            return new ExistingState(
                profiles.Select(profile => MakeKey(profile.MediaType, profile.Name)).ToHashSet(StringComparer.Ordinal),
                libraries.Select(library => MakeKey(library.MediaType, library.RootPath)).ToHashSet(StringComparer.Ordinal),
                indexers.Select(indexer => MakeKey(indexer.Protocol, indexer.BaseUrl)).ToHashSet(StringComparer.Ordinal),
                clients.Select(client => MakeKey(client.Protocol, client.EndpointUrl ?? client.Host ?? client.Name)).ToHashSet(StringComparer.Ordinal),
                intakeSources.Select(source => MakeKey(source.MediaType, source.Provider, source.FeedUrl)).ToHashSet(StringComparer.Ordinal),
                profiles.GroupBy(profile => MakeKey(profile.MediaType, profile.Name)).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
                libraries.GroupBy(library => MakeKey(library.MediaType, library.Name)).ToDictionary(group => group.Key, group => group.First().RootPath, StringComparer.Ordinal),
                indexers.GroupBy(indexer => MakeKey(indexer.Name)).ToDictionary(group => group.Key, group => MakeKey(group.First().Protocol, group.First().BaseUrl), StringComparer.Ordinal),
                clients.GroupBy(client => MakeKey(client.Name)).ToDictionary(group => group.Key, group => MakeKey(group.First().Protocol, group.First().EndpointUrl ?? group.First().Host ?? group.First().Name), StringComparer.Ordinal),
                intakeSources.GroupBy(source => MakeKey(source.MediaType, source.Name)).ToDictionary(group => group.Key, group => MakeKey(group.First().Provider, group.First().FeedUrl), StringComparer.Ordinal));
        }
    }
}
