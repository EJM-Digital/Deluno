using System.Diagnostics.Metrics;
using Deluno.Quality;

namespace Deluno.Persistence.Tests.Quality;

/// <summary>Each decision adds one to the decision-outcome metric, tagged with the media type, the wanted status, the policy version and whether the title has a file.</summary>
public sealed class TheDecisionOutcomeIsCountedTests
{
    [Fact]
    public void Each_decision_counts_once_tagged_with_media_type_wanted_status_policy_version_and_has_file()
    {
        var engine = new VersionedMediaPolicyEngine();
        var service = new MediaDecisionService(engine);
        var counted = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "deluno.decisions.outcomes")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var seen = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                seen[tag.Key] = tag.Value;
            }

            lock (counted)
            {
                counted.Add((value, seen));
            }
        });
        listener.Start();

        var decision = service.DecideWantedState(new MediaWantedDecisionInput("movies", HasFile: false, CurrentQuality: null, CutoffQuality: "WEBDL-1080p", UpgradeUntilCutoff: true, UpgradeUnknownItems: false));

        var mine = counted.Where(entry => entry.Tags.TryGetValue("wanted.status", out var status) && Equals(status, decision.WantedStatus) && Equals(entry.Tags["has.file"], false)).ToArray();
        var single = Assert.Single(mine);
        Assert.Equal(1, single.Value);
        Assert.Equal(decision.PolicyVersion, single.Tags["policy.version"]);
        Assert.Equal(MediaPolicyCatalog.NormalizeMediaType("movies"), single.Tags["media.type"]);
    }
}
