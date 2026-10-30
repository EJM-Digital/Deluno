using System.Collections;
using System.Reflection;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;
using Deluno.Security.Contracts;
using Deluno.Series.Contracts;
using Deluno.Worker.Automation;

namespace Deluno.Persistence.Tests.RecordContracts;

/// <summary>
/// #1268: the request and reply records the API serialises. Each one is built through its
/// positional constructor with a distinct value per parameter, every property is read back,
/// the record is deconstructed and copied with <c>with</c>, so a renamed or reordered
/// parameter, or a property that no longer returns what it was given, fails here.
/// </summary>
public sealed class RecordContractCoverageTests
{
    public static IEnumerable<object[]> Records() =>
        new[]
        {
            typeof(ConnectionItem),
            typeof(DownloadThroughputWindow),
            typeof(TraktConnectionStatus),
            typeof(SearchRetryWindowItem),
            typeof(UpdateDestinationRuleRequest),
            typeof(UpdateLibraryAutomationRequest),
            typeof(BulkMovieRequest),
            typeof(BulkMovieResponse),
            typeof(BulkMovieItemResult),
            typeof(CreateSharingRuleRequest),
            typeof(UpdateSharingRuleRequest),
            typeof(UpdateCustomFormatRequest),
            typeof(ReleasePreferencePreviewRequest),
            typeof(ReleasePreferencePreview),
            typeof(ChangePasswordRequest),
            typeof(BulkSeriesRequest),
            typeof(BulkSeriesResponse),
            typeof(BulkSeriesItemResult),
            typeof(EpisodeWorkflowDecision),
            typeof(UpdateSeriesMonitoringRequest),
            typeof(BulkCatalogueAddRequest),
            typeof(BulkCatalogueAddItem),
            typeof(BulkCatalogueEpisode),
            typeof(BulkCatalogueItemResult),
            typeof(BulkCatalogueAddResponse),
            typeof(BulkSeriesEpisodeRequest),
            typeof(BulkSeriesEpisodeItem),
            typeof(BulkSeriesEpisodeItemResult),
            typeof(BulkSeriesEpisodeResponse),
            typeof(AutomationSummaryResponse),
            typeof(AutomationReadinessSummary),
            typeof(AutomationQueueSummary),
            typeof(AutomationImportSummary),
            typeof(AutomationAttentionItem),
        }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Records))]
    public void Record_round_trips_every_parameter_through_its_properties(Type type)
    {
        var ctor = type.GetConstructors().Single(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType != type);
        var parameters = ctor.GetParameters();
        var args = parameters.Select((p, i) => Sample(p.ParameterType, i + 1)).ToArray();

        var instance = ctor.Invoke(args);

        foreach (var (parameter, arg) in parameters.Zip(args))
        {
            var property = type.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);
            Assert.Equal(arg, property!.GetValue(instance));
        }

        var deconstruct = type.GetMethod("Deconstruct");
        Assert.NotNull(deconstruct);
        var outs = new object?[parameters.Length];
        deconstruct!.Invoke(instance, outs);
        Assert.Equal(args, outs);

        var copy = type.GetMethod("<Clone>$")!.Invoke(instance, null);
        Assert.Equal(instance, copy);
        Assert.NotSame(instance, copy);
        Assert.Contains(parameters[0].Name!, instance.ToString());
    }

    [Fact]
    public void Staleness_window_is_two_weeks_with_a_day_between_attempts()
    {
        Assert.Equal(TimeSpan.FromDays(14), MetadataStalenessWindow.StaleAfter);
        Assert.Equal(TimeSpan.FromHours(24), MetadataStalenessWindow.AttemptCooldown);
    }

    [Fact]
    public void Automation_batch_limits_are_the_documented_sizes()
    {
        Assert.Equal(250, AutomationBatchLimits.MaxCatalogueItems);
        Assert.Equal(1000, AutomationBatchLimits.MaxEpisodeItems);
        Assert.Equal(200, AutomationBatchLimits.MaxIdempotencyKeyLength);
    }

    private static object? Sample(Type type, int seed)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return "value" + seed;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(int)) return seed;
        if (underlying == typeof(long)) return (long)seed;
        if (underlying == typeof(double)) return seed + 0.5;
        if (underlying == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(seed);
        if (underlying.IsEnum) return Enum.GetValues(underlying).GetValue(Math.Min(1, Enum.GetValues(underlying).Length - 1));
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(underlying)!;
            dictionary.Add(Sample(underlying.GetGenericArguments()[0], seed)!, Sample(underlying.GetGenericArguments()[1], seed));
            return dictionary;
        }
        if (underlying.IsGenericType &&
            (underlying.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) || underlying.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var element = underlying.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            list.Add(Sample(element, seed));
            return list;
        }
        var ctor = underlying.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select((p, i) => Sample(p.ParameterType, seed + i)).ToArray());
    }
}
