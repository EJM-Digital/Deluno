using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Migrations;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Deluno.Persistence.Tests.Quality;

public sealed class ReleasePreferencePersistenceTests
{
    [Fact]
    public async Task Compiled_plans_are_canonical_immutable_and_retrievable_by_version()
    {
        using var storage = TestStorage.Create();
        var now = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        var migrator = new SqliteDatabaseMigrator(storage.Factory, clock);
        await new PlatformSchemaInitializer(storage.Factory, migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteReleasePreferencePlanRepository(storage.Factory, clock);
        var plan = Plan("1") with
        {
            DimensionOrder = ["quality", "audio"]
        };

        var saved = await repository.SaveAsync(plan, CancellationToken.None);
        var equivalentInput = plan with { Families = [plan.Families[1], plan.Families[0]] };
        var savedAgain = await repository.SaveAsync(equivalentInput, CancellationToken.None);

        Assert.Equal(saved.PlanHash, savedAgain.PlanHash);
        Assert.Equal(ReleasePreferencePlanCodec.Serialize(plan), ReleasePreferencePlanCodec.Serialize(savedAgain.Plan));
        Assert.Equal(saved.CreatedUtc, (await repository.GetAsync(plan.Id, plan.Version, CancellationToken.None))!.CreatedUtc);

        using var json = JsonDocument.Parse(ReleasePreferencePlanCodec.Serialize(saved.Plan));
        var root = json.RootElement;
        Assert.False(root.TryGetProperty("planHash", out _));
        Assert.False(root.TryGetProperty("orderedFamilies", out _));
        Assert.False(root.GetProperty("families")[0].TryGetProperty("orderedLevels", out _));
        Assert.Equal("ranked", root.GetProperty("families")[0].GetProperty("intent").GetString());

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(
            plan with { Scenario = "different" },
            CancellationToken.None));

        var versionTwo = await repository.SaveAsync(plan with { Version = "2" }, CancellationToken.None);
        Assert.Equal(2, (await repository.ListAsync("movies", CancellationToken.None)).Count);
        Assert.NotEqual(saved.PlanHash, versionTwo.PlanHash);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Snapshot_round_trips_canonically_and_retains_prior_plan_versions(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var now = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        await InitializeSchemaAsync(storage, clock, kind);
        var mediaId = await AddMediaAsync(storage, clock, kind);
        var repository = new SqliteMediaStateRepository(storage.Factory, clock);
        var plan = Plan("1");
        var facts = new[]
        {
            new PreferenceFact("quality.web-1080p", PreferenceFactState.Present,
                new PreferenceEvidence("file-probe", Confidence: 1, DetectionRule: "fixture", DetectionVersion: "1")),
            new PreferenceFact("quality.bluray-1080p", PreferenceFactState.Absent,
                new PreferenceEvidence("file-probe", Confidence: 1))
        };
        var snapshot = new PreferenceEvaluationSnapshot(
            mediaId,
            "main",
            "/media/title/title.mkv",
            "/media/title/title.mkv",
            1234,
            plan.Id,
            plan.Version,
            plan.PlanHash,
            facts,
            ReleasePreferenceEvaluator.Evaluate(plan, facts),
            ["legacy-rule-b", "legacy-rule-a"],
            now,
            "import");

        await repository.SavePreferenceEvaluationSnapshotAsync(kind, snapshot, CancellationToken.None);
        var read = await repository.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            "main",
            snapshot.FileIdentity,
            CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(snapshot.MediaId, read.MediaId);
        Assert.Equal(["legacy-rule-a", "legacy-rule-b"], read.MatchedRuleIds);
        Assert.Equal(
            ReleasePreferenceSnapshotCodec.Serialize(snapshot),
            ReleasePreferenceSnapshotCodec.Serialize(read));

        var otherFile = snapshot with
        {
            FilePath = "/media/title/other.mkv",
            FileIdentity = "preference-file/v1:other"
        };
        await repository.SavePreferenceEvaluationSnapshotAsync(kind, otherFile, CancellationToken.None);
        var pathMatch = await repository.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            "main",
            fileIdentity: null,
            CancellationToken.None,
            filePath: snapshot.FilePath);
        var pathMiss = await repository.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            "main",
            fileIdentity: null,
            CancellationToken.None,
            filePath: "/media/title/missing.mkv");
        var exactFileMatch = await repository.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            "main",
            fileIdentity: null,
            CancellationToken.None,
            filePath: snapshot.FilePath,
            fileSizeBytes: snapshot.FileSizeBytes);
        var replacedInPlace = await repository.GetLatestPreferenceEvaluationSnapshotAsync(
            kind,
            mediaId,
            "main",
            fileIdentity: null,
            CancellationToken.None,
            filePath: snapshot.FilePath,
            fileSizeBytes: snapshot.FileSizeBytes + 1);

        Assert.NotNull(pathMatch);
        Assert.Equal(snapshot.FilePath, pathMatch.FilePath);
        Assert.Null(pathMiss);
        Assert.NotNull(exactFileMatch);
        Assert.Null(replacedInPlace);

        await repository.SavePreferenceEvaluationSnapshotAsync(kind, snapshot, CancellationToken.None);
        var nextPlan = Plan("2");
        await repository.SavePreferenceEvaluationSnapshotAsync(
            kind,
            snapshot with
            {
                PlanVersion = nextPlan.Version,
                PlanHash = nextPlan.PlanHash,
                Evaluation = ReleasePreferenceEvaluator.Evaluate(nextPlan, facts)
            },
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(
            kind == MediaKind.Movie ? DelunoDatabaseNames.Movies : DelunoDatabaseNames.Series);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM media_preference_evaluations WHERE media_id = @mediaId;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@mediaId";
        parameter.Value = mediaId;
        command.Parameters.Add(parameter);
        Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public void Legacy_translation_keeps_scores_advanced_and_builds_a_best_first_quality_family()
    {
        var timestamp = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.Zero);
        var profile = new QualityProfileItem(
            "profile-1",
            "Movies",
            "movies",
            "WEB 1080p",
            "WEB 720p, WEB 1080p, Bluray 1080p",
            "cf-1",
            true,
            true,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp);
        var format = new CustomFormatItem(
            "cf-1",
            "Prefer source",
            "movies",
            9999,
            "trash-1",
            "source=web-dl",
            true,
            timestamp,
            timestamp);

        var translation = LegacyReleasePreferenceTranslator.Translate(profile, [format]);

        Assert.True(translation.RequiresReview);
        var qualityFamily = translation.Plan.OrderedFamilies[0];

        // Best first, and every rank distinct: this is the property the family
        // has to hold, rather than one hard-coded top tier. The family is
        // deliberately wider than the profile's allowed list - it has to be
        // able to place a held file better than anything the profile would
        // grab, or migration would ask to downgrade it.
        Assert.Equal(0, qualityFamily.OrderedLevels[0].Rank);
        Assert.Equal(
            qualityFamily.OrderedLevels.Select((level, index) => index).ToArray(),
            qualityFamily.OrderedLevels.Select(level => level.Rank).ToArray());
        Assert.Equal("web-1080p", qualityFamily.TargetLevelId);
        Assert.Contains(qualityFamily.OrderedLevels, level => level.Id == "bluray-1080p");
        Assert.Contains(qualityFamily.OrderedLevels, level => level.Id == "web-720p");
        Assert.Contains(qualityFamily.OrderedLevels, level => level.Id == "bluray-2160p");
        Assert.True(
            qualityFamily.OrderedLevels.Single(level => level.Id == "bluray-2160p").Rank
                < qualityFamily.OrderedLevels.Single(level => level.Id == "web-1080p").Rank,
            "A tier above the cutoff must rank better than the cutoff itself.");
        var advanced = Assert.Single(translation.AdvancedRules);
        Assert.Equal(LegacyPreferenceRuleKind.UnmappedAdvanced, advanced.Kind);
        Assert.Equal(9999, advanced.OriginalScore);
        Assert.Null(advanced.ProposedIntent);
        var source = Assert.Single(translation.Plan.Sources!, item => item.SourceId == "trash-1");
        Assert.Equal("9999", source.OriginalScore);
        Assert.Equal("9999", source.AssignedScore);
    }

    /// <summary>
    /// #351 line 3: exact mappings preserve golden-fixture decisions.
    ///
    /// <para>Quality tiers are the one part of a legacy score profile that
    /// translates without judgement - allowed tiers become a ranked family,
    /// the cutoff becomes the target, and <c>UpgradeUntilCutoff</c> decides
    /// whether the family drives upgrades. Every custom format stays Advanced
    /// and cannot affect this. So the migrated plan has to agree with the
    /// engine it replaces on every square of the grid, not on one example:
    /// if it disagreed anywhere, a library would silently change its mind
    /// about which held files are finished the moment the plan activated.</para>
    /// </summary>
    [Theory]
    [InlineData("WEB 720p, WEB 1080p, Bluray 1080p", "WEB 1080p", true)]
    [InlineData("WEB 720p, WEB 1080p, Bluray 1080p", "WEB 1080p", false)]
    [InlineData("WEB 720p, WEB 1080p, Bluray 1080p", "Bluray 1080p", true)]
    [InlineData("WEB 1080p, Bluray 1080p, Bluray 2160p", "Bluray 2160p", true)]
    [InlineData("WEB 720p, WEB 1080p", "WEB 720p", true)]
    public void Exact_tier_translation_agrees_with_the_engine_it_replaces_on_every_installed_quality(
        string allowedQualities,
        string cutoffQuality,
        bool upgradeUntilCutoff)
    {
        var timestamp = new DateTimeOffset(2026, 9, 3, 3, 0, 0, TimeSpan.Zero);
        var profile = new QualityProfileItem(
            "profile-golden",
            "Golden",
            "movies",
            cutoffQuality,
            allowedQualities,
            string.Empty,
            upgradeUntilCutoff,
            true,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp);

        var translation = LegacyReleasePreferenceTranslator.Translate(profile);
        var family = Assert.Single(translation.Plan.OrderedFamilies);

        // Every tier the profile names, plus one it does not, so the grid
        // includes the case a plan has no opinion about.
        var installedTiers = allowedQualities
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append("Bluray 2160p")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var installed in installedTiers)
        {
            var legacy = LibraryQualityDecider.Decide(
                "movie",
                hasFile: true,
                currentQuality: installed,
                cutoffQuality: cutoffQuality,
                upgradeUntilCutoff: upgradeUntilCutoff,
                upgradeUnknownItems: true);

            // The facts Deluno actually records for an installed file, not a
            // hand-built one: the quality classifier is closed-world, so it
            // names the selected tier and marks every other tier absent.
            var selectedTraitId = InstalledPreferenceEvaluationFactory.QualityTraitId(installed);
            var facts = family.OrderedLevels
                .SelectMany(level => level.TraitIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(traitId => new PreferenceFact(
                    traitId,
                    string.Equals(traitId, selectedTraitId, StringComparison.OrdinalIgnoreCase)
                        ? PreferenceFactState.Present
                        : PreferenceFactState.Absent))
                .Append(new PreferenceFact(selectedTraitId, PreferenceFactState.Present))
                .ToArray();

            var typed = ReleasePreferenceEvaluator.Evaluate(translation.Plan, facts);

            // The decision that has to survive migration is "does this library
            // want to replace this file". `QualityCutoffMet` is a different
            // question - it reports the tier comparison even when the profile
            // has upgrades switched off, where the answer to the decision is
            // still no.
            var legacyWantsAnUpgrade = legacy.WantedStatus == WantedStatuses.Upgrade;
            var typedWantsAnUpgrade = typed.Status == PreferenceEvaluationStatus.BelowGoal;

            Assert.True(
                legacyWantsAnUpgrade == typedWantsAnUpgrade,
                $"'{installed}' under cutoff '{cutoffQuality}' (upgradeUntilCutoff={upgradeUntilCutoff}): "
                + $"the engine being replaced said {legacy.WantedStatus}, "
                + $"the migrated plan said {typed.Status}.");
        }
    }

    [Fact]
    public void Installed_factory_records_typed_quality_and_filename_evidence_without_a_public_score()
    {
        var timestamp = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.Zero);
        var profile = new QualityProfileItem(
            "profile-1",
            "Movies",
            "movies",
            "WEB 1080p",
            "WEB 720p, WEB 1080p, Bluray 1080p",
            string.Empty,
            true,
            true,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp);

        var snapshot = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            @"C:\media\Arrival.2016.WEB.1080p.x264-GROUP.mkv",
            42_000,
            "WEB 1080p",
            timestamp,
            "test");

        Assert.NotNull(snapshot);
        Assert.Equal("movie-1", snapshot.MediaId);
        Assert.Equal("library-1", snapshot.LibraryId);
        Assert.StartsWith("preference-file/v1:", snapshot.FileIdentity, StringComparison.Ordinal);
        Assert.Contains(snapshot.Facts, fact => fact.TraitId == "quality.web-1080p" && fact.State == PreferenceFactState.Present);
        Assert.Contains(snapshot.Facts, fact => fact.TraitId == "video.codec.h264");
        Assert.Contains(snapshot.Facts, fact => fact.TraitId == "source.webdl");
        Assert.Contains(snapshot.Facts, fact => fact.TraitId == "release-group.unclassified");
        Assert.Equal(PreferenceEvaluationStatus.MeetsPlan, snapshot.Evaluation.Status);
        Assert.Equal("quality-profile/profile-1", snapshot.PlanId);
        Assert.DoesNotContain("\"score\":", ReleasePreferenceSnapshotCodec.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quality_profile_round_trip_keeps_plan_reference_until_policy_changes()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteQualityRepository(storage.Factory, clock);
        var reference = new ReleasePreferencePlanReference(
            "quality-profile/imported",
            "legacy-import/v1",
            "ABCDEF1234567890");
        var created = await repository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Imported profile",
                "movies",
                "WEB 1080p",
                "WEB 720p, WEB 1080p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                ReleasePreferencePlan: reference),
            CancellationToken.None);

        var listed = Assert.Single(await repository.ListQualityProfilesAsync(CancellationToken.None));
        Assert.Equal(reference with { PlanHash = reference.PlanHash.ToLowerInvariant() }, listed.ReleasePreferencePlan);

        var renamed = await repository.UpdateQualityProfileAsync(
            created.Id,
            new UpdateQualityProfileRequest(
                "Imported profile renamed",
                "WEB 1080p",
                "WEB 720p, WEB 1080p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false),
            CancellationToken.None);
        Assert.NotNull(renamed);
        Assert.Equal(listed.ReleasePreferencePlan, renamed!.ReleasePreferencePlan);

        var changed = await repository.UpdateQualityProfileAsync(
            created.Id,
            new UpdateQualityProfileRequest(
                "Imported profile renamed",
                "WEB 1080p",
                "WEB 1080p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false),
            CancellationToken.None);
        Assert.NotNull(changed);
        Assert.Null(changed!.ReleasePreferencePlan);
    }

    [Fact]
    public async Task Quality_profile_resolver_returns_the_persisted_plan_and_rejects_missing_or_tampered_references()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-31T03:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var qualityRepository = new SqliteQualityRepository(storage.Factory, clock);
        var planRepository = new SqliteReleasePreferencePlanRepository(storage.Factory, clock);
        var plan = Plan("1");
        var stored = await planRepository.SaveAsync(plan, CancellationToken.None);
        var profile = await qualityRepository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Pinned profile",
                "movies",
                "WEB 1080p",
                "WEB 720p, WEB 1080p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                ReleasePreferencePlan: new ReleasePreferencePlanReference(
                    plan.Id,
                    plan.Version,
                    stored.PlanHash)),
            CancellationToken.None);

        var resolved = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            planRepository,
            profile.Id,
            CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(ReleasePreferencePlanCodec.Serialize(plan), ReleasePreferencePlanCodec.Serialize(resolved!));

        var missingProfile = await qualityRepository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Missing plan profile",
                "movies",
                "WEB 1080p",
                "WEB 1080p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                ReleasePreferencePlan: new ReleasePreferencePlanReference(
                    "missing-plan",
                    "v1",
                    "0123456789abcdef")),
            CancellationToken.None);

        var missing = await Assert.ThrowsAsync<InvalidDataException>(() =>
            QualityProfileResolver.ResolveReleasePreferencePlanAsync(
                qualityRepository,
                planRepository,
                missingProfile.Id,
                CancellationToken.None));
        Assert.Contains("missing", missing.Message, StringComparison.OrdinalIgnoreCase);

        var tamperedProfile = await qualityRepository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Tampered plan profile",
                "movies",
                "WEB 1080p",
                "WEB 1080p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                ReleasePreferencePlan: new ReleasePreferencePlanReference(
                    plan.Id,
                    plan.Version,
                    "ffffffffffffffff")),
            CancellationToken.None);

        var tampered = await Assert.ThrowsAsync<InvalidDataException>(() =>
            QualityProfileResolver.ResolveReleasePreferencePlanAsync(
                qualityRepository,
                planRepository,
                tamperedProfile.Id,
                CancellationToken.None));
        Assert.Contains("hash", tampered.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unpinned_profile_resolves_one_plan_identity_for_probe_import_and_search()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-01T01:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var qualityRepository = new SqliteQualityRepository(storage.Factory, clock);
        var planRepository = new SqliteReleasePreferencePlanRepository(storage.Factory, clock);
        var profile = await qualityRepository.CreateQualityProfileAsync(
            new CreateQualityProfileRequest(
                "Runtime profile",
                "movies",
                "WEB 2160p",
                "WEB 1080p, WEB 2160p",
                string.Empty,
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false),
            CancellationToken.None);
        var formats = await qualityRepository.ListCustomFormatsAsync(CancellationToken.None);

        var probePlan = ReleasePreferencePlanFactory.CreateQualityPlan(profile, formats);
        var searchPlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            planRepository,
            profile.Id,
            CancellationToken.None,
            formats);
        var snapshot = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-1",
            "library-1",
            @"C:\media\Movie.2026.2160p.WEB-DL.mkv",
            1_000,
            "WEB 2160p",
            clock.GetUtcNow(),
            "library-media-probe",
            formats,
            preferencePlan: probePlan);

        Assert.NotNull(searchPlan);
        Assert.NotNull(snapshot);
        Assert.Equal(probePlan.Id, searchPlan!.Id);
        Assert.Equal(probePlan.Version, searchPlan.Version);
        Assert.Equal(probePlan.PlanHash, searchPlan.PlanHash);
        Assert.Equal(searchPlan.Id, snapshot!.PlanId);
        Assert.Equal(searchPlan.Version, snapshot.PlanVersion);
        Assert.Equal(searchPlan.PlanHash, snapshot.PlanHash);
    }

    [Fact]
    public void Installed_factory_uses_matcher_snapshot_from_an_immutable_plan()
    {
        var timestamp = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.Zero);
        var profile = new QualityProfileItem(
            "profile-pinned",
            "Pinned",
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            "format-1",
            true,
            true,
            false,
            null,
            null,
            false,
            timestamp,
            timestamp);
        var format = new CustomFormatItem(
            "format-1",
            "Immutable remux matcher",
            "movies",
            999,
            "trash-remux",
            "[{\"type\":\"releaseTitle\",\"value\":\"REMUX\",\"required\":true}]",
            true,
            timestamp,
            timestamp);
        var plan = Plan("1") with
        {
            Sources = [new PreferencePlanProvenance(
                "trash-custom-format",
                "trash-remux",
                "guide-v1",
                MappedTraitIds: ["audio.channels.5-1"],
                MatcherDefinition: format.Conditions)]
        };

        var snapshot = InstalledPreferenceEvaluationFactory.Create(
            profile,
            "movie-pinned",
            "library-1",
            @"C:\media\Film.2026.REMUX.1080p.5-1-GROUP.mkv",
            1_000,
            "WEB 1080p",
            timestamp,
            "test",
            [format],
            guidePackage: null,
            preferencePlan: plan);

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.MatchedRuleIds, id => id == "format-1");
        Assert.Contains(snapshot.Facts, fact => fact.TraitId == "audio.channels.5-1");
        Assert.Equal(plan.PlanHash, snapshot.PlanHash);
    }

    private static ReleasePreferencePlan Plan(string version)
        => new(
            "test/movies",
            version,
            "movies",
            [new PreferenceFamily(
                "quality",
                "Quality",
                1,
                PreferenceIntent.Ranked,
                [
                    new PreferenceFamilyLevel("bluray", 0, ["quality.bluray-1080p"]),
                    new PreferenceFamilyLevel("web", 1, ["quality.web-1080p"])
                ],
                "bluray"),
            new PreferenceFamily(
                "audio",
                "Audio",
                2,
                PreferenceIntent.Ranked,
                [
                    new PreferenceFamilyLevel("5.1", 0, ["audio.channels.5-1"]),
                    new PreferenceFamilyLevel("stereo", 1, ["audio.channels.2-0"])
                ],
                "5.1")],
            DimensionOrder: ["quality", "audio"]);

    private static async Task InitializeSchemaAsync(TestStorage storage, TimeProvider clock, MediaKind kind)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, clock);
        if (kind == MediaKind.Movie)
        {
            await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance)
                .StartAsync(CancellationToken.None);
        }
        else
        {
            await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance)
                .StartAsync(CancellationToken.None);
        }
    }

    private static async Task<string> AddMediaAsync(TestStorage storage, TimeProvider clock, MediaKind kind)
    {
        if (kind == MediaKind.Movie)
        {
            return (await new SqliteMovieCatalogRepository(storage.Factory, clock).AddAsync(
                new CreateMovieRequest("Snapshot movie", 2026, "tt0000001"),
                CancellationToken.None)).Id;
        }

        return (await new SqliteSeriesCatalogRepository(storage.Factory, clock).AddAsync(
            new CreateSeriesRequest("Snapshot series", 2026, "tt0000002"),
            CancellationToken.None)).Id;
    }
}
