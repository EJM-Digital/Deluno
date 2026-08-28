using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Every kind of work Deluno does has words a person can read.
///
/// <para>Activity said <c>library.import.existing</c> to somebody, in that
/// exact form, because three separate switch statements each had to list every
/// job type and none of them listed that one. The lists are one table now; this
/// is what keeps the table complete.</para>
///
/// <para>It is the same defect as the sidebar area with no name in the topbar
/// list, and the same fix: derive the check from the thing itself rather than
/// from a second list that also has to be maintained. The job types here come
/// from the handlers that are actually registered, so a new handler with no
/// words fails this without anybody remembering to add it.</para>
/// </summary>
public sealed class JobTypeWordsTests
{
    /// <summary>
    /// Found by reflection rather than written down, because a hand-written list
    /// of job types would be a third list to keep in step — which is the thing
    /// that went wrong.
    /// </summary>
    private static IEnumerable<string> HandlerJobTypes =>
        typeof(SubtitleSyncJobHandler).Assembly
            .GetTypes()
            .Where(type => typeof(IJobHandler).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
            .Select(type => (IJobHandler?)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type))
            .Where(handler => handler is not null)
            .Select(handler => handler!.JobType)
            .Where(jobType => !NamedElsewhere(jobType))
            .Distinct();

    /// <summary>
    /// A library search is named from its payload — "Started checking Films" —
    /// rather than from the table, because the library's name is the useful half
    /// and the table cannot see it. So the two library-search types are meant to
    /// be absent from <c>JobTypeWords</c>, and both tests here have to know
    /// that. Stated once, for the reason this whole file exists.
    /// </summary>
    private static bool NamedElsewhere(string jobType) => LibrarySearchJobTypes.IsLibrarySearch(jobType);

    [Fact]
    public void Every_job_type_is_named()
    {
        var nameless = HandlerJobTypes
            .Where(jobType => !SqliteJobStore.JobTypeWords.ContainsKey(jobType))
            .ToArray();

        Assert.True(
            nameless.Length == 0,
            $"Activity would show these job types as raw strings: {string.Join(", ", nameless)}. " +
            "Add a row to SqliteJobStore.JobTypeWords.");
    }

    [Fact]
    public void No_words_are_written_for_a_job_type_that_no_longer_exists()
    {
        // The other direction, and the reason it matters is upkeep rather than
        // correctness: a row for a job nothing enqueues is a sentence nobody
        // will ever see being kept in step with sentences people do.
        var handled = HandlerJobTypes.ToHashSet(StringComparer.Ordinal);

        var orphans = SqliteJobStore.JobTypeWords.Keys
            .Where(jobType => !handled.Contains(jobType) && !NamedElsewhere(jobType))
            .ToArray();

        Assert.True(orphans.Length == 0, $"Words are kept for job types nothing runs: {string.Join(", ", orphans)}.");
    }

    /// <summary>
    /// The queued phrasing is a noun phrase because of where it is spliced in.
    /// Getting this wrong reads as "Added Started a subtitle search to the
    /// queue", which is the sort of thing that ships.
    /// </summary>
    [Fact]
    public void The_three_phrasings_are_each_the_shape_their_sentence_needs()
    {
        foreach (var (jobType, words) in SqliteJobStore.JobTypeWords)
        {
            Assert.False(words.Queued.EndsWith('.'), $"{jobType}'s queued phrasing is spliced mid-sentence, so it cannot end in a full stop.");
            Assert.StartsWith("Started ", words.Started, StringComparison.Ordinal);
            Assert.EndsWith(".", words.Started, StringComparison.Ordinal);
            Assert.False(words.Title.EndsWith('.'), $"{jobType}'s title is a label, not a sentence.");
        }
    }
}
