using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace Deluno.Integrations.Search;

public sealed class MlNetReleaseRankingModelService(
    IConfiguration configuration,
    IReleaseRankingTrainingDataSource trainingDataSource,
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider)
    : IReleaseRankingModelService, IReleaseRankingModelAdminService
{
    private readonly MLContext _ml = new(seed: 4210);
    private readonly object _stateLock = new();
    private ModelRuntimeState _state = ModelRuntimeState.Empty;
    private bool _initialized;

    public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked)
    {
        var status = GetStatus();
        if (!status.Enabled)
        {
            return new ReleaseRankingBoostResult(false, false, 0, "ML ranking is disabled.");
        }

        if (hardBlocked)
        {
            return new ReleaseRankingBoostResult(true, false, 0, "Hard safety blocks override model boost.");
        }

        EnsureInitialized();
        ModelRuntimeState snapshot;
        lock (_stateLock)
        {
            snapshot = _state;
        }

        if (snapshot.Model is null || snapshot.Metadata is null)
        {
            return new ReleaseRankingBoostResult(true, false, 0, "No trained ML model is loaded yet.");
        }

        var probability = PredictProbability(snapshot.Model, features);
        var boundedBoost = ComputeBoundedBoost(probability, status.MaxAbsoluteBoost);
        if (!status.AutoDispatchImpactEnabled)
        {
            return new ReleaseRankingBoostResult(
                Enabled: true,
                Applied: false,
                BoostPoints: 0,
                Explanation: $"ML model {snapshot.Metadata.Version} evaluated probability {probability.ToString("0.000", CultureInfo.InvariantCulture)} in offline mode.");
        }

        if (boundedBoost == 0)
        {
            return new ReleaseRankingBoostResult(
                Enabled: true,
                Applied: false,
                BoostPoints: 0,
                Explanation: $"ML model {snapshot.Metadata.Version} predicted {probability.ToString("0.000", CultureInfo.InvariantCulture)}; no score change.");
        }

        return new ReleaseRankingBoostResult(
            Enabled: true,
            Applied: true,
            BoostPoints: boundedBoost,
            Explanation: $"ML model {snapshot.Metadata.Version} predicted {probability.ToString("0.000", CultureInfo.InvariantCulture)} and applied {boundedBoost:+#;-#;0} boost.");
    }

    public RankingModelStatus GetStatus()
    {
        EnsureInitialized();
        var baseStatus = ReadStatus();
        ModelRuntimeState snapshot;
        lock (_stateLock)
        {
            snapshot = _state;
        }

        return baseStatus with
        {
            ModelLoaded = snapshot.Model is not null,
            ActiveModelVersion = snapshot.Metadata?.Version,
            LastTrainedUtc = snapshot.Metadata?.TrainedUtc,
            TrainingSampleCount = snapshot.Metadata?.SampleCount ?? 0,
            LastAuc = snapshot.Metadata?.Auc,
            LastAccuracy = snapshot.Metadata?.Accuracy,
            AvailableVersions = ListAvailableVersions()
        };
    }

    public async Task<RankingModelTrainingResult> TrainAsync(string reason, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var status = ReadStatus();
        if (!status.Enabled)
        {
            return new RankingModelTrainingResult(
                Success: false,
                Message: "Ranking model is disabled in configuration.",
                ModelVersion: null,
                SampleCount: 0,
                Auc: null,
                Accuracy: null,
                CompletedUtc: timeProvider.GetUtcNow());
        }

        var minSamples = Math.Clamp(configuration.GetValue("Deluno:RankingModel:MinTrainingSamples", 60), 20, 5000);
        var lookbackDays = Math.Clamp(configuration.GetValue("Deluno:RankingModel:TrainingLookbackDays", 120), 14, 730);
        var maxRows = Math.Clamp(configuration.GetValue("Deluno:RankingModel:MaxTrainingRows", 15000), 500, 50000);
        var sinceUtc = timeProvider.GetUtcNow().AddDays(-lookbackDays);
        var rows = await trainingDataSource.ListTrainingRowsAsync(maxRows, sinceUtc, cancellationToken);

        if (rows.Count < minSamples)
        {
            return new RankingModelTrainingResult(
                Success: false,
                Message: $"Not enough labeled training rows ({rows.Count}/{minSamples}).",
                ModelVersion: null,
                SampleCount: rows.Count,
                Auc: null,
                Accuracy: null,
                CompletedUtc: timeProvider.GetUtcNow());
        }

        var examples = rows.Select(ToExample).ToArray();
        var data = _ml.Data.LoadFromEnumerable(examples);
        var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 4210);

        var pipeline = BuildPipeline();
        var model = pipeline.Fit(split.TrainSet);
        var scored = model.Transform(split.TestSet);
        var metrics = _ml.BinaryClassification.Evaluate(scored);

        var modelVersion = $"rm-{timeProvider.GetUtcNow():yyyyMMddHHmmss}-{Guid.CreateVersion7().ToString("N")[..6]}";
        var metadata = new ModelMetadata(
            Version: modelVersion,
            TrainedUtc: timeProvider.GetUtcNow(),
            SampleCount: rows.Count,
            Auc: metrics.AreaUnderRocCurve,
            Accuracy: metrics.Accuracy,
            Reason: reason);

        PersistModel(model, split.TrainSet.Schema, metadata);
        lock (_stateLock)
        {
            _state = new ModelRuntimeState(model, metadata);
        }

        return new RankingModelTrainingResult(
            Success: true,
            Message: $"Trained ranking model {modelVersion} with {rows.Count} samples.",
            ModelVersion: modelVersion,
            SampleCount: rows.Count,
            Auc: metrics.AreaUnderRocCurve,
            Accuracy: metrics.Accuracy,
            CompletedUtc: metadata.TrainedUtc);
    }

    public bool TryRollback(string version, out string message)
    {
        EnsureInitialized();
        var normalized = (version ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            message = "A target version is required.";
            return false;
        }

        var modelPath = GetVersionModelPath(normalized);
        var metadataPath = GetVersionMetadataPath(normalized);
        if (!File.Exists(modelPath) || !File.Exists(metadataPath))
        {
            message = $"Model version '{normalized}' was not found.";
            return false;
        }

        var metadata = LoadMetadata(metadataPath);
        var model = LoadModel(modelPath);
        if (metadata is null || model is null)
        {
            message = $"Model version '{normalized}' could not be loaded.";
            return false;
        }

        lock (_stateLock)
        {
            _state = new ModelRuntimeState(model, metadata);
        }

        WriteActiveVersion(normalized);
        message = $"Rolled back ranking model to {normalized}.";
        return true;
    }

    private IEstimator<ITransformer> BuildPipeline()
    {
        return _ml.Transforms.Categorical.OneHotEncoding(new[]
            {
                new InputOutputColumnPair("DecisionStatusEncoded", nameof(ModelTrainingExample.DecisionStatus)),
                new InputOutputColumnPair("QualityEncoded", nameof(ModelTrainingExample.DecisionQuality)),
                new InputOutputColumnPair("ReleaseGroupEncoded", nameof(ModelTrainingExample.ReleaseGroup))
            })
            .Append(_ml.Transforms.Concatenate(
                "Features",
                nameof(ModelTrainingExample.Seeders),
                nameof(ModelTrainingExample.SizeGb),
                nameof(ModelTrainingExample.QualityDelta),
                nameof(ModelTrainingExample.CustomFormatScore),
                nameof(ModelTrainingExample.SeederScore),
                nameof(ModelTrainingExample.SizeScore),
                nameof(ModelTrainingExample.RuleScore),
                nameof(ModelTrainingExample.EstimatedBitrateMbps),
                nameof(ModelTrainingExample.ReleaseAgeHours),
                nameof(ModelTrainingExample.OverrideUsed),
                "DecisionStatusEncoded",
                "QualityEncoded",
                "ReleaseGroupEncoded"))
            .Append(_ml.BinaryClassification.Trainers.FastTree(
                labelColumnName: nameof(ModelTrainingExample.Label),
                featureColumnName: "Features",
                numberOfLeaves: 32,
                numberOfTrees: 200,
                minimumExampleCountPerLeaf: 8,
                learningRate: 0.08));
    }

    private static ModelTrainingExample ToExample(ReleaseRankingTrainingRow row)
    {
        var ageHours = row.CreatedUtc is null || row.GrabAttemptedUtc is null
            ? 0f
            : (float)Math.Max(0, (row.GrabAttemptedUtc.Value - row.CreatedUtc.Value).TotalHours);

        return new ModelTrainingExample
        {
            Seeders = row.Seeders ?? 0,
            SizeGb = row.SizeBytes is null ? 0f : (float)(row.SizeBytes.Value / 1024d / 1024d / 1024d),
            QualityDelta = row.QualityDelta ?? 0,
            CustomFormatScore = row.CustomFormatScore ?? 0,
            SeederScore = row.SeederScore ?? 0,
            SizeScore = row.SizeScore ?? 0,
            RuleScore = row.DecisionScore ?? 0,
            EstimatedBitrateMbps = (float)(row.EstimatedBitrateMbps ?? 0),
            ReleaseAgeHours = ageHours,
            DecisionStatus = row.DecisionStatus ?? "unknown",
            DecisionQuality = row.DecisionQuality ?? "unknown",
            ReleaseGroup = row.ReleaseGroup ?? "unknown",
            OverrideUsed = row.OverrideUsed ? 1f : 0f,
            Label = row.Label
        };
    }

    private float PredictProbability(ITransformer model, ReleaseRankingFeatures features)
    {
        var row = new ModelTrainingExample
        {
            Seeders = features.Seeders ?? 0,
            SizeGb = features.SizeBytes is null ? 0f : (float)(features.SizeBytes.Value / 1024d / 1024d / 1024d),
            QualityDelta = features.QualityDelta,
            CustomFormatScore = features.CustomFormatScore,
            SeederScore = features.Seeders ?? 0,
            SizeScore = features.SizeBytes is null ? 0f : (float)Math.Clamp(features.SizeBytes.Value / 1024d / 1024d / 1024d, 0, 40),
            RuleScore = features.QualityDelta * 18 + (int)Math.Round(features.CustomFormatScore * 0.1, MidpointRounding.AwayFromZero),
            EstimatedBitrateMbps = (float)(features.EstimatedBitrateMbps ?? 0),
            ReleaseAgeHours = (float)(features.ReleaseAgeHours ?? 0),
            DecisionStatus = "candidate",
            DecisionQuality = "runtime",
            ReleaseGroup = "runtime",
            OverrideUsed = 0f
        };

        var dv = _ml.Data.LoadFromEnumerable([row]);
        var scored = model.Transform(dv);
        return _ml.Data.CreateEnumerable<ModelPrediction>(scored, reuseRowObject: false).First().Probability;
    }

    private static int ComputeBoundedBoost(float probability, int maxAbsoluteBoost)
    {
        var centered = (probability - 0.5d) * 2d;
        var raw = centered * maxAbsoluteBoost;
        var bounded = (int)Math.Round(Math.Clamp(raw, -maxAbsoluteBoost, maxAbsoluteBoost), MidpointRounding.AwayFromZero);
        return Math.Abs(bounded) < 1 ? 0 : bounded;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_initialized)
            {
                return;
            }

            var activeVersion = ReadActiveVersion();
            if (!string.IsNullOrWhiteSpace(activeVersion))
            {
                var metadata = LoadMetadata(GetVersionMetadataPath(activeVersion));
                var model = LoadModel(GetVersionModelPath(activeVersion));
                if (metadata is not null && model is not null)
                {
                    _state = new ModelRuntimeState(model, metadata);
                }
            }

            _initialized = true;
        }
    }

    private RankingModelStatus ReadStatus()
    {
        var enabled = configuration.GetValue("Deluno:RankingModel:Enabled", false);
        var autoDispatchImpactEnabled = configuration.GetValue("Deluno:RankingModel:AutoDispatchImpactEnabled", false);
        var maxAbsoluteBoost = Math.Clamp(configuration.GetValue("Deluno:RankingModel:MaxAbsoluteBoost", 20), 1, 60);
        var mode = configuration["Deluno:RankingModel:Mode"] ?? "offline";
        var notes = autoDispatchImpactEnabled
            ? "ML inference is active with bounded boost. Deterministic blocks always win."
            : "ML inference runs in offline mode with zero dispatch impact.";

        return new RankingModelStatus(
            Enabled: enabled,
            AutoDispatchImpactEnabled: autoDispatchImpactEnabled,
            MaxAbsoluteBoost: maxAbsoluteBoost,
            Mode: mode,
            Notes: notes);
    }

    private void PersistModel(ITransformer model, DataViewSchema schema, ModelMetadata metadata)
    {
        var directory = GetVersionsRoot();
        Directory.CreateDirectory(directory);
        var versionDir = Path.Combine(directory, metadata.Version);
        Directory.CreateDirectory(versionDir);

        var modelPath = GetVersionModelPath(metadata.Version);
        var metadataPath = GetVersionMetadataPath(metadata.Version);
        using (var stream = File.Create(modelPath))
        {
            _ml.Model.Save(model, schema, stream);
        }

        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
        WriteActiveVersion(metadata.Version);
    }

    private IReadOnlyList<string> ListAvailableVersions()
    {
        var versionsRoot = GetVersionsRoot();
        if (!Directory.Exists(versionsRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(versionsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderDescending(StringComparer.Ordinal)
            .ToArray()!;
    }

    private ITransformer? LoadModel(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(modelPath);
            return _ml.Model.Load(stream, out _);
        }
        catch
        {
            return null;
        }
    }

    private ModelMetadata? LoadMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<ModelMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    private string? ReadActiveVersion()
    {
        var markerPath = GetActiveVersionMarkerPath();
        if (!File.Exists(markerPath))
        {
            return null;
        }

        var value = File.ReadAllText(markerPath).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void WriteActiveVersion(string version)
    {
        Directory.CreateDirectory(GetModelsRoot());
        File.WriteAllText(GetActiveVersionMarkerPath(), version.Trim());
    }

    private string GetModelsRoot()
        => Path.Combine(storageOptions.Value.DataRoot, "models", "release-ranking");

    private string GetVersionsRoot()
        => Path.Combine(GetModelsRoot(), "versions");

    private string GetVersionModelPath(string version)
        => Path.Combine(GetVersionsRoot(), version, "model.zip");

    private string GetVersionMetadataPath(string version)
        => Path.Combine(GetVersionsRoot(), version, "metadata.json");

    private string GetActiveVersionMarkerPath()
        => Path.Combine(GetModelsRoot(), "active-version.txt");

    private sealed record ModelRuntimeState(ITransformer? Model, ModelMetadata? Metadata)
    {
        public static readonly ModelRuntimeState Empty = new(null, null);
    }

    private sealed record ModelMetadata(
        string Version,
        DateTimeOffset TrainedUtc,
        int SampleCount,
        double? Auc,
        double? Accuracy,
        string Reason);

    private sealed class ModelTrainingExample
    {
        public float Seeders { get; set; }
        public float SizeGb { get; set; }
        public float QualityDelta { get; set; }
        public float CustomFormatScore { get; set; }
        public float SeederScore { get; set; }
        public float SizeScore { get; set; }
        public float RuleScore { get; set; }
        public float EstimatedBitrateMbps { get; set; }
        public float ReleaseAgeHours { get; set; }
        public float OverrideUsed { get; set; }
        public string DecisionStatus { get; set; } = "unknown";
        public string DecisionQuality { get; set; } = "unknown";
        public string ReleaseGroup { get; set; } = "unknown";
        public bool Label { get; set; }
    }

    private sealed class ModelPrediction
    {
        [ColumnName("Probability")]
        public float Probability { get; set; }
    }
}
