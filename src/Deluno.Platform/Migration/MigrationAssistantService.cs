using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Platform.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform.Migration;

public sealed class MigrationAssistantService(
    IMigrationAuditRepository repository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IConnectionsRepository connectionsRepository,
    IIntakeRepository intakeRepository,
    IEnumerable<IMigrationCatalogImporter>? catalogImporters = null,
    IMigrationBackupService? backupService = null,
    IGuidePackageStore? guidePackageStore = null,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null) : IMigrationAssistantService
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
            var existing = await ExistingState.LoadAsync(
                librariesRepository,
                qualityRepository,
                connectionsRepository,
                intakeRepository,
                releasePreferencePlanRepository,
                cancellationToken);
            var guidePackage = guidePackageStore is null
                ? GuidePackageCatalog.Current
                : (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package;
            var operations = new List<MigrationReportOperation>();
            var contexts = ResolveContexts(document.RootElement, sourceKind).ToArray();

            if (contexts.Length == 0)
            {
                warnings.Add("No supported Radarr/Sonarr-style sections were found. Deluno looked for rootFolders, qualityProfiles, indexers, downloadClients, importLists, movies, and series.");
            }

            foreach (var context in contexts)
            {
                ExtractCustomFormats(context, existing, operations, guidePackage, request.AllowAdvancedLegacyRules);
                ExtractQualityProfiles(
                    context,
                    existing,
                    operations,
                    guidePackage,
                    request.AllowAdvancedLegacyRules,
                    releasePreferencePlanRepository is not null);
                ExtractLibraries(context, existing, operations);
                ExtractIndexers(context, existing, operations);
                ExtractDownloadClients(context, existing, operations);
                ExtractIntakeSources(context, existing, operations);
            }

            var contextTitleStats = contexts
                .Select(context => (Context: context, Stats: ExtractTitleStats([context])))
                .ToArray();
            foreach (var (context, stats) in contextTitleStats.Where(item => item.Stats.TitleCount > 0))
            {
                operations.Add(new MigrationReportOperation(
                    MakeOperationId("titles", context.SourceKind, context.MediaType),
                    "catalog",
                    "monitored-state",
                    $"{stats.TitleCount.ToString(CultureInfo.InvariantCulture)} imported {context.MediaType} titles",
                    "report",
                    false,
                    "Deluno inventoried monitored state, source-reported files, assignments, probed facts, and matched-format history. On apply, Deluno creates deduplicated catalog records only when it can safely map each title to one migrated library; existing files still require a later library scan to reconcile file associations.",
                    new Dictionary<string, string?>
                    {
                        ["sourceLabel"] = context.SourceLabel,
                        ["mediaType"] = context.MediaType,
                        ["titleCount"] = stats.TitleCount.ToString(CultureInfo.InvariantCulture),
                        ["monitoredCount"] = stats.MonitoredCount.ToString(CultureInfo.InvariantCulture),
                        ["wantedCount"] = stats.WantedCount.ToString(CultureInfo.InvariantCulture),
                        ["installedFileCount"] = stats.InstalledFileCount.ToString(CultureInfo.InvariantCulture),
                        ["qualityProfileAssignmentCount"] = stats.QualityProfileAssignmentCount.ToString(CultureInfo.InvariantCulture),
                        ["libraryAssignmentCount"] = stats.LibraryAssignmentCount.ToString(CultureInfo.InvariantCulture),
                        ["probedMediaFactsCount"] = stats.ProbedMediaFactsCount.ToString(CultureInfo.InvariantCulture),
                        ["matchedFormatHistoryCount"] = stats.MatchedFormatHistoryCount.ToString(CultureInfo.InvariantCulture)
                    },
                    []));
            }

            var titleStats = contextTitleStats
                .Select(item => item.Stats)
                .Aggregate(TitleStats.Empty, static (total, next) => total.Add(next));
            var inventory = BuildInventory(contexts, operations);
            return BuildReport(sourceKind, sourceName, operations, warnings, errors, titleStats.TitleCount, titleStats.MonitoredCount, titleStats.WantedCount, redactSensitiveData, inventory);
        }
    }

    public async Task<MigrationApplyResponse> ApplyAsync(MigrationImportRequest request, CancellationToken cancellationToken)
    {
        var report = await BuildPreviewAsync(request, redactSensitiveData: false, cancellationToken);
        if (!report.Valid)
        {
            return new MigrationApplyResponse(RedactReport(report), []);
        }

        MigrationBackupReceipt? backup = null;
        if (backupService is null)
        {
            // Unit-level callers can intentionally omit the host backup
            // adapter, but production application wiring must make the
            // missing protection visible instead of implying a safe apply.
            report = report with
            {
                Warnings = report.Warnings.Concat(["No verified backup service is configured for this migration execution."]).ToArray()
            };
        }
        else
        {
            try
            {
                backup = await backupService.CreateVerifiedBackupAsync("pre-migration", cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var blocked = report with
                {
                    Valid = false,
                    Errors = report.Errors.Concat([$"Migration was blocked because the automatic verified backup failed: {exception.Message}"]).ToArray()
                };
                return new MigrationApplyResponse(RedactReport(blocked), [], Backup: null);
            }
        }

        var applied = new List<MigrationAppliedItem>();
        string? stageFailure = null;
        var selectedOperationIds = request.SelectedOperationIds is { Count: > 0 }
            ? request.SelectedOperationIds.ToHashSet(StringComparer.Ordinal)
            : null;
        var isSelected = (MigrationReportOperation operation) =>
            selectedOperationIds is null || selectedOperationIds.Contains(operation.Id);
        var importedCustomFormatIds = report.Operations
            .Where(operation => operation.TargetType == "custom-format"
                && operation.Action is "create" or "skip"
                && (operation.Action == "skip" || isSelected(operation))
                && !string.IsNullOrWhiteSpace(GetData(operation, "id")))
            .ToDictionary(
                operation => GetData(operation, "id")!,
                operation => GetData(operation, "existingId") ?? GetData(operation, "id")!,
                StringComparer.OrdinalIgnoreCase);

        foreach (var operation in report.Operations.Where(operation => operation.CanApply && operation.Action == "create" && isSelected(operation)))
        {
            try
            {
            switch (operation.TargetType)
            {
                case "release-preference-plan":
                {
                    if (releasePreferencePlanRepository is null)
                    {
                        throw new InvalidOperationException(
                            "The migration release-preference plan store is not configured; the typed plan must be persisted before activation.");
                    }

                    var planJson = GetData(operation, "planJson")
                        ?? throw new InvalidOperationException("The migration release-preference plan operation has no plan definition.");
                    var plan = ReleasePreferencePlanCodec.Deserialize(planJson);
                    var stored = await releasePreferencePlanRepository.SaveAsync(plan, cancellationToken);
                    applied.Add(new MigrationAppliedItem(
                        operation.Id,
                        operation.TargetType,
                        operation.Name,
                        stored.Plan.Id,
                        "created"));
                    break;
                }
                case "quality-profile":
                {
                    var planOperationId = GetData(operation, "releasePreferencePlanOperationId");
                    if (!string.IsNullOrWhiteSpace(planOperationId))
                    {
                        var planOperation = report.Operations.FirstOrDefault(candidate =>
                            string.Equals(candidate.Id, planOperationId, StringComparison.Ordinal));
                        var planWasPersisted = planOperation is not null
                            && (planOperation.Action == "skip"
                                || applied.Any(item =>
                                    string.Equals(item.OperationId, planOperation.Id, StringComparison.Ordinal)
                                    && item.Result == "created"));
                        if (!planWasPersisted)
                        {
                            throw new InvalidOperationException(
                                "The quality profile's typed release-preference plan was not selected or persisted; Deluno will not activate a profile with a dangling plan reference.");
                        }
                    }

                    var customFormatIds = ResolveImportedCustomFormatIds(
                        GetData(operation, "customFormatIds"),
                        importedCustomFormatIds);
                    var releasePreferencePlan = string.IsNullOrWhiteSpace(GetData(operation, "releasePreferencePlanId"))
                        ? null
                        : new ReleasePreferencePlanReference(
                            GetData(operation, "releasePreferencePlanId")!,
                            GetData(operation, "releasePreferencePlanVersion") ?? string.Empty,
                            GetData(operation, "releasePreferencePlanHash") ?? string.Empty);
                    var created = await qualityRepository.CreateQualityProfileAsync(
                        new CreateQualityProfileRequest(
                            GetData(operation, "name"),
                            GetData(operation, "mediaType"),
                            GetData(operation, "cutoffQuality"),
                            GetData(operation, "allowedQualities"),
                            CustomFormatIds: customFormatIds,
                            UpgradeUntilCutoff: ParseBool(GetData(operation, "upgradeUntilCutoff"), defaultValue: true),
                            UpgradeUnknownItems: ParseBool(GetData(operation, "upgradeUnknownItems"), defaultValue: false),
                            ReleasePreferencePlan: releasePreferencePlan),
                        cancellationToken);
                    applied.Add(new MigrationAppliedItem(operation.Id, operation.TargetType, operation.Name, created.Id, "created"));
                    break;
                }
                case "custom-format":
                {
                    var stableId = GetData(operation, "id")
                        ?? throw new InvalidOperationException("The migration custom-format operation has no stable source id.");
                    var created = await qualityRepository.CreateCustomFormatAsync(
                        new CreateCustomFormatRequest(
                            GetData(operation, "name") ?? operation.Name,
                            GetData(operation, "mediaType"),
                            ParseInt(GetData(operation, "score"), 0),
                            GetData(operation, "trashId"),
                            GetData(operation, "conditions"),
                            ParseBool(GetData(operation, "upgradeAllowed"), defaultValue: true)),
                        cancellationToken,
                        preferredId: stableId);
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
                    var privacy = GetData(operation, "privacy");

                    // The one job the privacy field has (#288). A tracker the
                    // old app called private polices sharing, and a migration
                    // that carried the label but not the obligation would let
                    // Deluno reclaim after three days on a site that bans for
                    // it. Nobody migrating from Prowlarr should have to know
                    // that and go back through every source by hand.
                    var strict = IndexerPrivacy.ExpectsSharing(privacy);

                    var created = await connectionsRepository.CreateIndexerAsync(
                        new CreateIndexerRequest(
                            GetData(operation, "name"),
                            GetData(operation, "protocol"),
                            privacy,
                            GetData(operation, "baseUrl"),
                            GetData(operation, "apiKey"),
                            ParseInt(GetData(operation, "priority"), 100),
                            GetData(operation, "categories"),
                            GetData(operation, "tags"),
                            GetData(operation, "mediaScope"),
                            ParseBool(GetData(operation, "isEnabled"), defaultValue: true),
                            RequestIntervalSeconds: null,
                            SharingMode: strict ? SharingPolicy.Strict.Mode : null,
                            SharingForHours: strict ? SharingPolicy.Strict.ForHours : null,
                            SharingUntilRatio: strict ? SharingPolicy.Strict.UntilRatio : null,
                            SharingStuckAction: strict ? SharingPolicy.Strict.StuckAction : null,
                            SharingStuckAfterDays: strict ? SharingPolicy.Strict.StuckAfterDays : null),
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
                Applied: applied,
                Backup: backup),
            cancellationToken);
        return new MigrationApplyResponse(afterApply, applied, audit.Id, backup);
    }

    private static void ExtractQualityProfiles(
        MigrationContext context,
        ExistingState existing,
        List<MigrationReportOperation> operations,
        GuidePackage guidePackage,
        bool allowAdvancedLegacyRules,
        bool canPersistTypedPlans)
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
                ["sourceId"] = ReadString(item, "id") ?? MakeKey(context.SourceKind, context.MediaType, name),
                ["name"] = name,
                ["mediaType"] = mediaType,
                ["cutoffQuality"] = cutoffQuality,
                ["allowedQualities"] = allowedQualities,
                ["upgradeUntilCutoff"] = "true",
                ["upgradeUnknownItems"] = "false",
                ["customFormatIds"] = ResolveCustomFormatIds(item)
            };

            // Radarr/Sonarr store a custom-format assignment score on the
            // profile, not necessarily on the global custom-format row. Read
            // the profile-scoped values before compiling the typed plan so a
            // migration cannot silently make two profiles equivalent.
            var importedCustomFormats = ReadImportedCustomFormats(context, item);

            var nameKey = MakeKey(mediaType, name);
            if (existing.QualityProfilesByKey.TryGetValue(nameKey, out var existingProfile) &&
                (!string.Equals(existingProfile.CutoffQuality, cutoffQuality, StringComparison.OrdinalIgnoreCase) ||
                 !SameCsvValues(existingProfile.AllowedQualities, allowedQualities)))
            {
                operations.Add(Conflict(context, "quality-profile", name, "A quality profile with this name already exists but its cutoff or allowed qualities differ. Deluno will not overwrite it silently.", data));
                continue;
            }

            var profileOperation = PlanCreateOrSkip(
                context,
                existing.QualityProfileKeys,
                "quality",
                "quality-profile",
                name,
                nameKey,
                "Quality profile will be mapped into Deluno cutoff and allowed quality policy.",
                "A quality profile with this name and media type already exists.",
                data,
                []);

            // The typed compiler keeps the entire legacy profile explainable,
            // but an opaque custom-format matcher is not safe to activate on
            // a migration preview. Keep the proposed plan in the report and
            // make the profile operation explicitly review-only until #350's
            // reviewed mapping catalogue has classified each rule.
            var importedProfile = CreateImportedProfile(
                context,
                item,
                name,
                cutoffQuality,
                allowedQualities,
                data["customFormatIds"] ?? string.Empty);
            var translation = CompileImportedProfile(importedProfile, item.GetRawText(), importedCustomFormats, guidePackage);
            var serializedPlan = ReleasePreferencePlanCodec.Serialize(translation.Plan);
            var planKey = MakeKey(translation.Plan.Id, translation.Plan.Version);
            var existingPlan = existing.ReleasePreferencePlansByKey.GetValueOrDefault(planKey);
            var hasInvalidReference = translation.AdvancedRules.Any(rule => rule.Kind == LegacyPreferenceRuleKind.Invalid);
            var planAction = "report";
            var planCanApply = false;
            var planReason = translation.RequiresReview
                ? "The deterministic typed plan is ready for review; unresolved legacy matchers remain Advanced and do not drive automatic decisions."
                : "The deterministic typed plan is ready for use.";
            var planWarnings = translation.Warnings.ToArray();
            if (existingPlan is not null)
            {
                if (string.Equals(existingPlan.PlanHash, translation.Plan.PlanHash, StringComparison.OrdinalIgnoreCase))
                {
                    planAction = "skip";
                    planReason = "This immutable typed release-preference plan version is already stored.";
                }
                else
                {
                    planAction = "conflict";
                    planReason = "The same typed release-preference plan id and version already exist with a different definition; Deluno will not overwrite them silently.";
                    planWarnings = planWarnings
                        .Concat(["The stored plan is immutable. Create a new version after reviewing the imported profile differences."])
                        .ToArray();
                }
            }
            else if (canPersistTypedPlans && (!translation.RequiresReview || (allowAdvancedLegacyRules && !hasInvalidReference)))
            {
                planAction = "create";
                planCanApply = true;
                planReason = translation.RequiresReview
                    ? "The typed portion will be stored as an immutable plan; opaque matcher rows remain Advanced input because that option was explicitly selected."
                    : "The typed plan will be stored as an immutable version before the migrated profile is activated.";
            }
            else if (!canPersistTypedPlans)
            {
                planWarnings = planWarnings
                    .Concat(["The typed plan is report-only because no release-preference plan store is configured for this execution."])
                    .ToArray();
            }
            var planOperationId = MakeOperationId("release-preference-plan", context.SourceKind, name);
            operations.Add(new MigrationReportOperation(
                planOperationId,
                "quality",
                "release-preference-plan",
                $"{name} release preferences",
                planAction,
                planCanApply,
                planReason,
                new Dictionary<string, string?>
                {
                    ["profileId"] = importedProfile.Id,
                    ["planId"] = translation.Plan.Id,
                    ["planVersion"] = translation.Plan.Version,
                    ["planHash"] = translation.Plan.PlanHash,
                    ["planJson"] = serializedPlan,
                    ["advancedRuleCount"] = translation.AdvancedRules.Count.ToString(CultureInfo.InvariantCulture),
                    ["requiresReview"] = translation.RequiresReview.ToString(CultureInfo.InvariantCulture),
                    ["warningCount"] = planWarnings.Length.ToString(CultureInfo.InvariantCulture)
                },
                planWarnings));

            if (translation.RequiresReview && (!allowAdvancedLegacyRules || hasInvalidReference))
            {
                profileOperation = profileOperation with
                {
                    CanApply = false,
                    Reason = "The profile contains legacy custom-format rules that need an explicit typed mapping review. Its compiled plan is shown separately in this preview."
                };
            }
            else if (translation.RequiresReview)
            {
                profileOperation = profileOperation with
                {
                    Reason = "The profile contains opaque matcher rows that will be retained as Advanced legacy input because that option was explicitly selected. They do not contribute numeric values to typed decisions.",
                    Warnings = profileOperation.Warnings
                        .Concat(["Advanced legacy rules are stored for review/export and remain outside the typed decision contract."])
                        .ToArray()
                };
            }

            if (canPersistTypedPlans && planAction == "conflict")
            {
                profileOperation = profileOperation with
                {
                    CanApply = false,
                    Reason = "The profile's immutable typed release-preference plan conflicts with a stored definition; Deluno will not activate the profile until a new reviewed plan version is created."
                };
            }

            if (canPersistTypedPlans && (planAction is "create" or "skip"))
            {
                var profileData = new Dictionary<string, string?>(profileOperation.Data)
                {
                    ["releasePreferencePlanOperationId"] = planOperationId,
                    ["releasePreferencePlanId"] = translation.Plan.Id,
                    ["releasePreferencePlanVersion"] = translation.Plan.Version,
                    ["releasePreferencePlanHash"] = translation.Plan.PlanHash
                };
                profileOperation = profileOperation with { Data = profileData };
            }

            operations.Add(profileOperation);
        }
    }

    /// <summary>
    /// Preserves every imported custom-format row. Reviewed guide mappings may
    /// be applied with their stable source id. Opaque matchers remain
    /// report-only unless the owner explicitly opts into Advanced legacy
    /// storage. The raw matcher/specification JSON is retained because
    /// flattening a regex, negation, required flag, or nested condition into a
    /// name would violate the migration granularity guarantee. Numeric scores
    /// remain provenance; they never become typed intent here.
    /// </summary>
    private static void ExtractCustomFormats(
        MigrationContext context,
        ExistingState existing,
        List<MigrationReportOperation> operations,
        GuidePackage guidePackage,
        bool allowAdvancedLegacyRules)
    {
        foreach (var item in EnumerateArrays(context.Root, "customFormats", "custom-formats", "formats"))
        {
            var sourceId = ReadString(item, "id") ?? ReadString(item, "formatId");
            var name = ReadString(item, "name") ?? ReadString(item, "displayName") ?? sourceId;
            if (string.IsNullOrWhiteSpace(name))
            {
                operations.Add(Unsupported(context, "custom-format", "Unnamed custom format", "Custom format is missing both a name and a stable id."));
                continue;
            }

            var conditions = ReadString(item, "conditions")
                            ?? ReadRawJson(item, "specifications")
                            ?? ReadRawJson(item, "condition");
            var stableId = sourceId ?? MakeStableCustomFormatId(context, name!, conditions);
            var guideMapped = !string.IsNullOrWhiteSpace(ReadString(item, "trashId") ?? ReadString(item, "trashGuid"))
                && IsReviewedGuideMapping(
                    guidePackage,
                    ReadString(item, "trashId") ?? ReadString(item, "trashGuid"));
            var classification = string.IsNullOrWhiteSpace(conditions)
                ? LegacyPreferenceRuleKind.Invalid.ToString()
                : guideMapped
                    ? LegacyPreferenceRuleKind.GuideMapped.ToString()
                    : LegacyPreferenceRuleKind.UnmappedAdvanced.ToString();
            var reason = string.IsNullOrWhiteSpace(conditions)
                ? "The custom format has no matcher/specification payload, so it is preserved as invalid and cannot affect decisions."
                : guideMapped
                    ? "The matcher has a reviewed TRaSH mapping and will be retained with its stable source id; the original score remains provenance only."
                    : "The matcher is preserved as Advanced input. Numeric score and checkbox placement are not enough to infer required, forbidden, ranked, or tie-break intent.";
            var mediaType = ReadString(item, "mediaType") ?? context.MediaType;
            var trashId = ReadString(item, "trashId") ?? ReadString(item, "trashGuid");
            var existingFormat = FindExistingCustomFormat(
                existing.CustomFormatsByIdentity,
                mediaType,
                stableId,
                trashId,
                name,
                conditions);
            var isExisting = existingFormat is not null;
            var existingMatches = existingFormat is not null
                && string.Equals(existingFormat.Name, name, StringComparison.Ordinal)
                && string.Equals(existingFormat.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
                && existingFormat.Score == (ReadInt(item, "score") ?? 0)
                && string.Equals(existingFormat.TrashId, trashId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingFormat.Conditions, conditions ?? string.Empty, StringComparison.Ordinal)
                && existingFormat.UpgradeAllowed == (ReadBool(item, "upgradeAllowed") ?? true);
            var canApply = !string.IsNullOrWhiteSpace(conditions)
                && (guideMapped || allowAdvancedLegacyRules);
            var action = string.IsNullOrWhiteSpace(conditions)
                ? "report"
                : isExisting
                    ? existingMatches ? "skip" : "conflict"
                    : canApply ? "create" : "report";

            var data = new Dictionary<string, string?>
            {
                ["id"] = stableId,
                ["sourceId"] = sourceId,
                ["name"] = name,
                ["mediaType"] = mediaType,
                ["trashId"] = trashId,
                ["score"] = ReadInt(item, "score")?.ToString(CultureInfo.InvariantCulture) ?? "0",
                ["upgradeAllowed"] = (ReadBool(item, "upgradeAllowed") ?? true).ToString(CultureInfo.InvariantCulture),
                ["conditions"] = conditions,
                ["classification"] = classification,
                ["requiresReview"] = (!string.IsNullOrWhiteSpace(conditions)).ToString(CultureInfo.InvariantCulture),
                ["activation"] = guideMapped ? "typed" : "advanced-legacy",
                ["existingId"] = existingFormat?.Id,
                ["rawJson"] = item.GetRawText()
            };

            operations.Add(new MigrationReportOperation(
                MakeOperationId("custom-format", context.SourceKind, stableId),
                action == "report" ? "inventory" : "quality",
                "custom-format",
                name,
                action,
                action is "create",
                reason,
                data,
                action == "conflict"
                    ? ["A row with the same stable identity exists but its matcher, score, or upgrade flag differs; review before applying."]
                    : string.IsNullOrWhiteSpace(conditions)
                        ? ["No matcher was found; the original row is retained for repair/export but will not be evaluated."]
                        : guideMapped
                            ? []
                            : ["Owner review is required before this legacy matcher can become typed intent."]));
        }
    }

    private static QualityProfileItem CreateImportedProfile(
        MigrationContext context,
        JsonElement item,
        string name,
        string cutoffQuality,
        string allowedQualities,
        string customFormatIds)
    {
        var sourceId = ReadString(item, "id") ?? MakeKey(context.SourceKind, context.MediaType, name);
        return new QualityProfileItem(
            sourceId,
            name,
            context.MediaType,
            cutoffQuality,
            allowedQualities,
            customFormatIds,
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            AllowLowerQualityReplacements: false,
            PresetId: null,
            PresetVersion: null,
            PresetDrifted: false,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);
    }

    private static LegacyReleasePreferenceTranslation CompileImportedProfile(
        QualityProfileItem profile,
        string sourceJson,
        IReadOnlyList<CustomFormatItem>? customFormats = null,
        GuidePackage? guidePackage = null)
    {
        var runtimeCompilation = ReleasePreferencePlanFactory.CompileProfile(profile, customFormats, guidePackage);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", profile.Id, profile.Name, profile.MediaType, profile.CutoffQuality,
                profile.AllowedQualities, profile.CustomFormatIds, sourceJson))));
        var plan = runtimeCompilation.Plan with
        {
            Version = $"{LegacyReleasePreferenceTranslator.MappingVersion}/import-{fingerprint[..24]}"
        };
        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);
        return new LegacyReleasePreferenceTranslation(
            profile.Id,
            plan.Version,
            plan,
            runtimeCompilation.AdvancedRules,
            runtimeCompilation.Warnings,
            runtimeCompilation.RequiresReview);
    }

    private static IReadOnlyList<CustomFormatItem> ReadImportedCustomFormats(
        MigrationContext context,
        JsonElement? profile = null)
    {
        var profileScores = profile is { } profileElement
            ? ReadProfileCustomFormatScores(profileElement)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var formats = new List<CustomFormatItem>();
        foreach (var item in EnumerateArrays(context.Root, "customFormats", "custom-formats", "formats"))
        {
            var sourceId = ReadString(item, "id") ?? ReadString(item, "formatId");
            var name = ReadString(item, "name") ?? ReadString(item, "displayName") ?? sourceId;
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var score = profileScores.TryGetValue(sourceId, out var assignedScore)
                ? assignedScore
                : ReadInt(item, "score") ?? 0;
            formats.Add(new CustomFormatItem(
                sourceId,
                name,
                ReadString(item, "mediaType") ?? context.MediaType,
                score,
                ReadString(item, "trashId") ?? ReadString(item, "trashGuid"),
                ReadString(item, "conditions")
                    ?? ReadRawJson(item, "specifications")
                    ?? ReadRawJson(item, "condition")
                    ?? string.Empty,
                ReadBool(item, "upgradeAllowed") ?? true,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch));
        }

        return formats;
    }

    private static IReadOnlyDictionary<string, int> ReadProfileCustomFormatScores(JsonElement profile)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPropertyCaseInsensitive(profile, "customFormats", out var customFormats)
            || customFormats.ValueKind != JsonValueKind.Array)
        {
            return scores;
        }

        foreach (var item in customFormats.EnumerateArray())
        {
            var sourceId = ReadString(item, "id") ?? ReadString(item, "formatId");
            var score = ReadInt(item, "score");
            if (!string.IsNullOrWhiteSpace(sourceId) && score is not null)
            {
                scores[sourceId] = score.Value;
            }
        }

        return scores;
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
                // No default. This used to fall back to "private", which was a
                // harmless mislabel while nothing read it and is now a claim
                // that would put a strict sharing rule on an open index.
                ["privacy"] = ReadString(item, "privacy"),
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
        var installedFileCount = 0;
        var qualityProfileAssignmentCount = 0;
        var libraryAssignmentCount = 0;
        var probedMediaFactsCount = 0;
        var matchedFormatHistoryCount = 0;

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

                if (ReadBool(item, "hasFile") == true
                    || ReadBool(item, "downloaded") == true
                    || ContainsNonEmptyProperty(item, "movieFile", "episodeFile", "episodeFiles"))
                {
                    installedFileCount++;
                }

                if (ReadInt(item, "qualityProfileId") is not null
                    || ContainsNonEmptyProperty(item, "qualityProfile"))
                {
                    qualityProfileAssignmentCount++;
                }

                if (!string.IsNullOrWhiteSpace(ReadString(item, "rootFolderPath") ?? ReadString(item, "rootPath"))
                    || ContainsNonEmptyProperty(item, "rootFolder"))
                {
                    libraryAssignmentCount++;
                }

                if (ContainsNonEmptyProperty(item, "mediaInfo", "mediaFacts", "probeResult"))
                {
                    probedMediaFactsCount++;
                }

                if (ContainsNonEmptyProperty(item, "customFormats", "matchedFormats", "matchedFormatIds"))
                {
                    matchedFormatHistoryCount++;
                }
            }
        }

        return new TitleStats(
            titleCount,
            monitoredCount,
            wantedCount,
            installedFileCount,
            qualityProfileAssignmentCount,
            libraryAssignmentCount,
            probedMediaFactsCount,
            matchedFormatHistoryCount);
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
                        ReadString(item, "rootFolderPath") ?? ReadString(item, "rootPath"),
                        SeriesType: ReadString(item, "seriesType") ?? ReadString(item, "series_type"),
                        NumberingScheme: ReadString(item, "numberingScheme") ?? ReadString(item, "numbering_scheme"),
                        NumberingSource: ReadString(item, "numberingSource") ?? ReadString(item, "numbering_source")));
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
        bool redactSensitiveData,
        MigrationReportInventory? inventory = null)
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
            errors,
            inventory ?? MigrationReportInventory.Empty);
    }

    private static MigrationReportInventory BuildInventory(
        IReadOnlyList<MigrationContext> contexts,
        IReadOnlyList<MigrationReportOperation> operations)
    {
        var entries = new List<MigrationInventoryEntry>();
        foreach (var context in contexts)
        {
            AddInventoryEntry(context, "quality", "quality-profile", ["qualityProfiles", "profiles"], operations, entries);
            AddInventoryEntry(context, "quality", "custom-format", ["customFormats", "custom-formats", "formats"], operations, entries);
            AddInventoryEntry(context, "libraries", "library", ["rootFolders", "rootfolders", "rootFolderPaths"], operations, entries);
            AddInventoryEntry(context, "connections", "indexer", ["indexers", "indexerSources"], operations, entries);
            AddInventoryEntry(context, "connections", "download-client", ["downloadClients", "downloadclients", "clients"], operations, entries);
            AddInventoryEntry(context, "automation", "intake-source", ["importLists", "importlists", "lists", "intakeSources"], operations, entries);

            var titleCount = EnumerateArrays(context.Root, "movies", "series", "shows", "titles").Count();
            if (titleCount > 0)
            {
                var titleOperations = operations
                    .Where(operation => operation.TargetType == "monitored-state"
                        && IsOperationForContext(operation, context))
                    .ToArray();
                var accounted = titleOperations.Sum(operation => ParseInt(operation.Data.GetValueOrDefault("titleCount"), 0));
                var classifications = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                AddClassification(classifications, "source-reports-installed-file", titleOperations.Sum(operation => ParseInt(operation.Data.GetValueOrDefault("installedFileCount"), 0)));
                AddClassification(classifications, "quality-profile-assigned", titleOperations.Sum(operation => ParseInt(operation.Data.GetValueOrDefault("qualityProfileAssignmentCount"), 0)));
                AddClassification(classifications, "library-assigned", titleOperations.Sum(operation => ParseInt(operation.Data.GetValueOrDefault("libraryAssignmentCount"), 0)));
                AddClassification(classifications, "probed-media-facts", titleOperations.Sum(operation => ParseInt(operation.Data.GetValueOrDefault("probedMediaFactsCount"), 0)));
                AddClassification(classifications, "matched-format-history", titleOperations.Sum(operation => ParseInt(operation.Data.GetValueOrDefault("matchedFormatHistoryCount"), 0)));
                AddClassification(classifications, "quality-profile-unassigned", titleCount - classifications.GetValueOrDefault("quality-profile-assigned"));
                AddClassification(classifications, "library-unassigned", titleCount - classifications.GetValueOrDefault("library-assigned"));
                AddInventoryEntry(
                    context,
                    "catalog",
                    "monitored-state",
                    titleCount,
                    accounted,
                    titleOperations,
                    classifications,
                    entries);
            }
        }

        var inputCount = entries.Sum(entry => entry.InputRowCount);
        var accountedCount = entries.Sum(entry => Math.Min(entry.InputRowCount, entry.AccountedRowCount));
        return new MigrationReportInventory(
            inputCount,
            accountedCount,
            Math.Max(0, inputCount - accountedCount),
            entries);
    }

    private static void AddInventoryEntry(
        MigrationContext context,
        string category,
        string targetType,
        IReadOnlyList<string> arrayNames,
        IReadOnlyList<MigrationReportOperation> operations,
        ICollection<MigrationInventoryEntry> entries)
    {
        var inputCount = EnumerateArrays(context.Root, arrayNames.ToArray()).Count();
        if (inputCount == 0)
        {
            return;
        }

        var matching = operations
            .Where(operation => operation.TargetType == targetType
                && IsOperationForContext(operation, context))
            .ToArray();
        AddInventoryEntry(
            context,
            category,
            targetType,
            inputCount,
            matching.Length,
            matching,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            entries);
    }

    private static bool IsOperationForContext(MigrationReportOperation operation, MigrationContext context)
    {
        var operationKey = operation.TargetType == "monitored-state" ? "titles" : operation.TargetType;
        var prefix = MakeOperationId(operationKey, context.SourceKind, string.Empty);
        return operation.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (operation.Id.Length == prefix.Length || operation.Id[prefix.Length] == '-');
    }

    private static void AddInventoryEntry(
        MigrationContext context,
        string category,
        string targetType,
        int inputCount,
        int accountedCount,
        IReadOnlyList<MigrationReportOperation> operations,
        IReadOnlyDictionary<string, int> classifications,
        ICollection<MigrationInventoryEntry> entries)
    {
        var actionCounts = operations
            .GroupBy(operation => operation.Action, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var classificationCounts = classifications.Count > 0
            ? classifications
            : operations
                .Select(operation => operation.Data.GetValueOrDefault("classification"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        if (accountedCount < inputCount)
        {
            warnings.Add($"{inputCount - accountedCount} legacy {targetType} row(s) were not mapped to a report operation.");
        }

        entries.Add(new MigrationInventoryEntry(
            context.SourceKind,
            context.MediaType,
            targetType,
            inputCount,
            accountedCount,
            actionCounts,
            classificationCounts,
            warnings));
    }

    private static void AddClassification(IDictionary<string, int> classifications, string name, int count)
    {
        if (count > 0)
        {
            classifications[name] = count;
        }
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

    private static string ResolveCustomFormatIds(JsonElement item)
    {
        var scalar = ReadString(item, "customFormatIds") ?? ReadString(item, "customFormats");
        if (!string.IsNullOrWhiteSpace(scalar))
        {
            return string.Join(", ", scalar
                .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (!TryGetPropertyCaseInsensitive(item, "customFormatIds", out var ids)
            && !TryGetPropertyCaseInsensitive(item, "customFormats", out ids))
        {
            return string.Empty;
        }

        if (ids.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var values = ids.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Object
                ? ReadString(value, "id") ?? ReadString(value, "formatId")
                : ReadElementAsString(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join(", ", values);
    }

    private static string? ReadRawJson(JsonElement item, string name)
        => TryGetPropertyCaseInsensitive(item, name, out var value)
            && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.GetRawText()
            : null;

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

    private static bool ContainsNonEmptyProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                    && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && property.Value.GetRawText() is not ("{}" or "[]" or "\"\""))
                {
                    return true;
                }

                if (ContainsNonEmptyProperty(property.Value, names))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsNonEmptyProperty(item, names))
                {
                    return true;
                }
            }
        }

        return false;
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

    private static string? ResolveImportedCustomFormatIds(
        string? sourceIds,
        IReadOnlyDictionary<string, string> importedIds)
    {
        var ids = (sourceIds ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return null;
        }

        var unresolved = ids
            .Where(id => !importedIds.ContainsKey(id))
            .ToArray();
        if (unresolved.Length > 0)
        {
            throw new InvalidOperationException(
                $"The quality profile references custom format(s) that were not selected for migration: {string.Join(", ", unresolved)}.");
        }

        return string.Join(", ", ids.Select(id => importedIds[id]));
    }

    private static CustomFormatItem? FindExistingCustomFormat(
        IReadOnlyDictionary<string, CustomFormatItem> formats,
        string mediaType,
        string stableId,
        string? trashId,
        string name,
        string? conditions)
    {
        if (formats.TryGetValue(MakeKey("id", mediaType, stableId), out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(trashId)
            && formats.TryGetValue(MakeKey("trash", mediaType, trashId), out var byTrashId))
        {
            return byTrashId;
        }

        return formats.TryGetValue(MakeKey("content", mediaType, name, conditions), out var byContent)
            ? byContent
            : null;
    }

    private static bool IsReviewedGuideMapping(GuidePackage guidePackage, string? trashId)
    {
        if (string.IsNullOrWhiteSpace(trashId))
        {
            return false;
        }

        var guideFormat = guidePackage.CustomFormats.FirstOrDefault(format =>
            string.Equals(format.TrashId, trashId, StringComparison.OrdinalIgnoreCase));
        if (guideFormat is null
            || guideFormat.MappingStatus != GuideMappingStatus.Reviewed
            || guideFormat.MappedTraitIds is not { Count: > 0 })
        {
            return false;
        }

        return guideFormat.MappedTraitIds.All(traitId =>
            PreferenceTraitRegistry.Current.TryResolve(traitId, out var trait)
            && !trait.Transient);
    }

    private static string MakeStableCustomFormatId(
        MigrationContext context,
        string name,
        string? conditions)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            MakeKey(context.SourceKind, context.MediaType, name, conditions))));
        return $"migration-{fingerprint[..24]}";
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

    private sealed record TitleStats(
        int TitleCount,
        int MonitoredCount,
        int WantedCount,
        int InstalledFileCount,
        int QualityProfileAssignmentCount,
        int LibraryAssignmentCount,
        int ProbedMediaFactsCount,
        int MatchedFormatHistoryCount)
    {
        public static TitleStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

        public TitleStats Add(TitleStats other) => new(
            TitleCount + other.TitleCount,
            MonitoredCount + other.MonitoredCount,
            WantedCount + other.WantedCount,
            InstalledFileCount + other.InstalledFileCount,
            QualityProfileAssignmentCount + other.QualityProfileAssignmentCount,
            LibraryAssignmentCount + other.LibraryAssignmentCount,
            ProbedMediaFactsCount + other.ProbedMediaFactsCount,
            MatchedFormatHistoryCount + other.MatchedFormatHistoryCount);
    }

    private sealed record ExistingState(
        IReadOnlySet<string> QualityProfileKeys,
        IReadOnlySet<string> LibraryRootKeys,
        IReadOnlySet<string> IndexerKeys,
        IReadOnlySet<string> DownloadClientKeys,
        IReadOnlySet<string> IntakeSourceKeys,
        IReadOnlyDictionary<string, CustomFormatItem> CustomFormatsByIdentity,
        IReadOnlyDictionary<string, QualityProfileItem> QualityProfilesByKey,
        IReadOnlyDictionary<string, StoredReleasePreferencePlan> ReleasePreferencePlansByKey,
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
            IReleasePreferencePlanRepository? releasePreferencePlanRepository,
            CancellationToken cancellationToken)
        {
            var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
            var libraries = await repository.ListLibrariesAsync(cancellationToken);
            var indexers = await connectionsRepository.ListIndexersAsync(cancellationToken);
            var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
            var intakeSources = await intakeRepository.ListIntakeSourcesAsync(cancellationToken);
            var customFormats = await qualityRepository.ListCustomFormatsAsync(cancellationToken);
            var releasePreferencePlans = new Dictionary<string, StoredReleasePreferencePlan>(StringComparer.Ordinal);
            if (releasePreferencePlanRepository is not null)
            {
                foreach (var storedPlan in await releasePreferencePlanRepository.ListAsync(null, cancellationToken))
                {
                    releasePreferencePlans[MakeKey(storedPlan.Plan.Id, storedPlan.Plan.Version)] = storedPlan;
                }
            }
            var customFormatsByIdentity = new Dictionary<string, CustomFormatItem>(StringComparer.Ordinal);
            foreach (var format in customFormats)
            {
                AddCustomFormatIdentity(customFormatsByIdentity, MakeKey("id", format.MediaType, format.Id), format);
                if (!string.IsNullOrWhiteSpace(format.TrashId))
                {
                    AddCustomFormatIdentity(customFormatsByIdentity, MakeKey("trash", format.MediaType, format.TrashId), format);
                }

                AddCustomFormatIdentity(
                    customFormatsByIdentity,
                    MakeKey("content", format.MediaType, format.Name, format.Conditions),
                    format);
            }

            return new ExistingState(
                profiles.Select(profile => MakeKey(profile.MediaType, profile.Name)).ToHashSet(StringComparer.Ordinal),
                libraries.Select(library => MakeKey(library.MediaType, library.RootPath)).ToHashSet(StringComparer.Ordinal),
                indexers.Select(indexer => MakeKey(indexer.Protocol, indexer.BaseUrl)).ToHashSet(StringComparer.Ordinal),
                clients.Select(client => MakeKey(client.Protocol, client.EndpointUrl ?? client.Host ?? client.Name)).ToHashSet(StringComparer.Ordinal),
                intakeSources.Select(source => MakeKey(source.MediaType, source.Provider, source.FeedUrl)).ToHashSet(StringComparer.Ordinal),
                customFormatsByIdentity,
                profiles.GroupBy(profile => MakeKey(profile.MediaType, profile.Name)).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
                releasePreferencePlans,
                libraries.GroupBy(library => MakeKey(library.MediaType, library.Name)).ToDictionary(group => group.Key, group => group.First().RootPath, StringComparer.Ordinal),
                indexers.GroupBy(indexer => MakeKey(indexer.Name)).ToDictionary(group => group.Key, group => MakeKey(group.First().Protocol, group.First().BaseUrl), StringComparer.Ordinal),
                clients.GroupBy(client => MakeKey(client.Name)).ToDictionary(group => group.Key, group => MakeKey(group.First().Protocol, group.First().EndpointUrl ?? group.First().Host ?? group.First().Name), StringComparer.Ordinal),
                intakeSources.GroupBy(source => MakeKey(source.MediaType, source.Name)).ToDictionary(group => group.Key, group => MakeKey(group.First().Provider, group.First().FeedUrl), StringComparer.Ordinal));
        }

        private static void AddCustomFormatIdentity(
            IDictionary<string, CustomFormatItem> identities,
            string key,
            CustomFormatItem format)
        {
            if (!identities.ContainsKey(key))
            {
                identities[key] = format;
            }
        }
    }
}
