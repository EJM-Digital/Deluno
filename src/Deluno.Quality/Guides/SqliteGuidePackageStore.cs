using System.Data.Common;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Quality.ReleasePreferences;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Quality.Guides;

/// <summary>
/// Persists owner-approved guide packages without making the mutable package a
/// runtime dependency on a network fetch. The embedded package is the safe
/// bootstrap value; once an update is approved, the active row becomes the
/// source used by API, worker and UI compilation.
/// </summary>
public sealed class SqliteGuidePackageStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IGuidePackageStore
{
    private static readonly JsonSerializerOptions JsonOptions = ReleasePreferenceJson.Options;

    public async Task<StoredGuidePackage> GetCurrentAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT package_json, is_active, stored_utc FROM guide_package_versions WHERE is_active = 1 ORDER BY stored_utc DESC LIMIT 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadStored(reader)
            : Bootstrap();
    }

    public async Task<IReadOnlyList<StoredGuidePackage>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT package_json, is_active, stored_utc FROM guide_package_versions ORDER BY stored_utc DESC, package_id ASC, package_version DESC;";
        var result = new List<StoredGuidePackage>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadStored(reader));
        }

        if (result.Count == 0)
        {
            result.Add(Bootstrap());
        }

        return result;
    }

    public async Task<StoredGuidePackage?> GetAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId) || version <= 0)
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT package_json, is_active, stored_utc FROM guide_package_versions WHERE package_id = @packageId AND package_version = @version LIMIT 1;";
        AddParameter(command, "@packageId", packageId.Trim());
        AddParameter(command, "@version", version);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStored(reader) : null;
    }

    public async Task<GuidePackageUpdatePreview> PreviewAsync(
        GuidePackageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var current = await GetCurrentAsync(cancellationToken);
        return BuildPreview(current, request);
    }

    public async Task<StoredGuidePackage> ApplyAsync(
        GuidePackageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var current = await GetCurrentAsync(cancellationToken);
        var preview = BuildPreview(current, request);
        if (!preview.CanApply)
        {
            throw new ArgumentException(
                string.Join(" | ", preview.Errors.Concat(preview.Warnings)),
                nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedCurrentIntegritySha256)
            && !string.Equals(
                request.ExpectedCurrentIntegritySha256.Trim(),
                current.IntegritySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The active guide package changed after this preview. Refresh the package diff before applying it.");
        }

        var proposed = preview.Proposed;
        var json = JsonSerializer.Serialize(proposed, JsonOptions);
        var existing = await GetAsync(proposed.Id, proposed.Version, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.IntegritySha256, preview.ProposedIntegritySha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Guide package '{proposed.Id}' version {proposed.Version} is immutable and already contains a different definition.");
            }

            if (existing.IsActive)
            {
                return existing;
            }
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE guide_package_versions SET is_active = 0 WHERE is_active = 1;";
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        if (existing is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO guide_package_versions (package_id, package_version, integrity_sha256, package_json, source_revision, is_active, stored_utc) VALUES (@packageId, @version, @integrity, @json, @sourceRevision, 1, @storedUtc);";
            AddParameter(insert, "@packageId", proposed.Id);
            AddParameter(insert, "@version", proposed.Version);
            AddParameter(insert, "@integrity", preview.ProposedIntegritySha256);
            AddParameter(insert, "@json", json);
            AddParameter(insert, "@sourceRevision", proposed.Source.UpstreamRevision);
            AddParameter(insert, "@storedUtc", timeProvider.GetUtcNow().ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            using var activate = connection.CreateCommand();
            activate.Transaction = transaction;
            activate.CommandText =
                "UPDATE guide_package_versions SET is_active = 1 WHERE package_id = @packageId AND package_version = @version;";
            AddParameter(activate, "@packageId", proposed.Id);
            AddParameter(activate, "@version", proposed.Version);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new StoredGuidePackage(proposed, true, timeProvider.GetUtcNow());
    }

    public async Task<StoredGuidePackage> ActivateAsync(
        string packageId,
        int version,
        CancellationToken cancellationToken)
    {
        // GetAsync re-validates the stored definition and its integrity hash,
        // so a row edited outside Deluno throws here rather than becoming the
        // active package.
        var stored = await GetAsync(packageId, version, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Guide package '{packageId}' version {version} is not retained.");
        if (stored.IsActive)
        {
            return stored;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE guide_package_versions SET is_active = 0 WHERE is_active = 1;";
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText =
                "UPDATE guide_package_versions SET is_active = 1 WHERE package_id = @packageId AND package_version = @version;";
            AddParameter(activate, "@packageId", stored.Package.Id);
            AddParameter(activate, "@version", stored.Package.Version);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return stored with { IsActive = true };
    }

    private static StoredGuidePackage Bootstrap()
        => new(GuidePackageCatalog.Current, true, DateTimeOffset.MinValue);

    private static StoredGuidePackage ReadStored(DbDataReader reader)
    {
        var json = reader.GetString(0);
        var package = JsonSerializer.Deserialize<GuidePackage>(json, JsonOptions)
            ?? throw new InvalidDataException("Stored guide package was empty.");
        var errors = GuidePackageCatalog.Validate(package);
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Stored guide package is invalid: {string.Join(" | ", errors)}");
        }

        var computed = GuidePackageCatalog.ComputeIntegritySha256(package);
        if (!string.Equals(computed, package.IntegritySha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Stored guide package integrity does not match its content.");
        }

        return new StoredGuidePackage(
            package,
            reader.GetInt32(1) == 1,
            ParseTimestamp(reader.GetString(2)));
    }

    private static GuidePackageUpdatePreview BuildPreview(
        StoredGuidePackage current,
        GuidePackageUpdateRequest request)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (request.Package is null)
        {
            errors.Add("A guide package is required.");
            return new GuidePackageUpdatePreview(
                current,
                current.Package,
                current.IntegritySha256,
                GuideCapabilityInventoryBuilder.Build(current.Package),
                [],
                errors,
                warnings,
                false);
        }

        var proposed = request.Package with { IntegritySha256 = null };
        errors.AddRange(GuidePackageCatalog.Validate(proposed));
        if (proposed.Version > current.Package.Version && proposed.SourceInventory is null)
        {
            errors.Add("A newly applied guide package must carry the pinned upstream source inventory. Historical schema-v1 packages remain readable but cannot be copied forward without it.");
        }
        var proposedHash = GuidePackageCatalog.ComputeIntegritySha256(proposed);
        proposed = proposed with { IntegritySha256 = proposedHash };

        if (proposed.Version < current.Package.Version)
        {
            errors.Add($"Guide package version {proposed.Version} is older than the active version {current.Package.Version}.");
        }
        else if (proposed.Version == current.Package.Version
            && !string.Equals(proposedHash, current.IntegritySha256, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("A changed guide package must use a new package version.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedCurrentIntegritySha256)
            && !string.Equals(request.ExpectedCurrentIntegritySha256.Trim(), current.IntegritySha256, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The expected active guide package does not match the current package.");
        }

        var inventory = GuideCapabilityInventoryBuilder.Build(proposed);
        errors.AddRange(inventory.Unaccounted.Select(item => $"Unaccounted guide capability: {item}"));
        var diffs = BuildProfileDiffs(current.Package, proposed, warnings);
        return new GuidePackageUpdatePreview(
            current,
            proposed,
            proposedHash,
            inventory,
            diffs,
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            errors.Count == 0);
    }

    private static IReadOnlyList<GuideProfileUpdateDiff> BuildProfileDiffs(
        GuidePackage current,
        GuidePackage proposed,
        ICollection<string> warnings)
    {
        var currentProfiles = current.QualityProfiles.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var proposedProfiles = proposed.QualityProfiles.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var diffs = new List<GuideProfileUpdateDiff>();
        foreach (var profileId in currentProfiles.Keys.Concat(proposedProfiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            currentProfiles.TryGetValue(profileId, out var oldProfile);
            proposedProfiles.TryGetValue(profileId, out var newProfile);
            GuideProfileCompilation? oldCompilation = null;
            GuideProfileCompilation? newCompilation = null;
            try
            {
                if (oldProfile is not null) oldCompilation = GuidePlanCompiler.Compile(profileId, oldProfile.MediaType, current);
            }
            catch (Exception exception)
            {
                warnings.Add($"Could not compile current guide profile '{profileId}': {exception.Message}");
            }

            try
            {
                if (newProfile is not null) newCompilation = GuidePlanCompiler.Compile(profileId, newProfile.MediaType, proposed);
            }
            catch (Exception exception)
            {
                warnings.Add($"Could not compile proposed guide profile '{profileId}': {exception.Message}");
            }

            var changes = new List<string>();
            if (oldProfile is null) changes.Add("profile added");
            if (newProfile is null) changes.Add("profile removed");
            if (oldCompilation?.Plan.PlanHash != newCompilation?.Plan.PlanHash) changes.Add("compiled typed plan changed");
            if (oldCompilation?.AdvancedRules.Count != newCompilation?.AdvancedRules.Count) changes.Add("Advanced rule count changed");
            if (oldCompilation?.RequiresReview != newCompilation?.RequiresReview) changes.Add("review requirement changed");
            diffs.Add(new GuideProfileUpdateDiff(
                profileId,
                newProfile?.Name ?? oldProfile?.Name ?? profileId,
                oldCompilation?.Plan.PlanHash,
                newCompilation?.Plan.PlanHash,
                oldCompilation?.AdvancedRules.Count ?? 0,
                newCompilation?.AdvancedRules.Count ?? 0,
                changes,
                (oldCompilation?.Warnings ?? []).Concat(newCompilation?.Warnings ?? []).Distinct(StringComparer.Ordinal).ToArray()));
        }

        return diffs;
    }
}
