using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// The decision table from DESIGN-007, walked row by row.
///
/// <para>The point of writing a table down is that it can be checked. James
/// asked for every case to be assessed individually rather than grouped — so
/// every reason is named here with the answer that was settled for it, and a
/// reason nobody decided is a failure rather than a silent default.</para>
///
/// <para>These are shipped defaults. The Failure and blocklist console changes
/// them per installation; what this guards is that the defaults are the ones
/// that were agreed.</para>
/// </summary>
public sealed class TheImportFailureTableTests
{
    /// <summary>
    /// Deluno has read the file and knows what it is. A second attempt fetches
    /// the same bytes.
    /// </summary>
    [Theory]
    [InlineData(ImportFailurePolicy.NoVideoStream)]
    [InlineData(ImportFailurePolicy.LikelySample)]
    [InlineData(ImportFailurePolicy.UnsupportedFile)]
    [InlineData(ImportFailurePolicy.MediaProbeRejected)]
    [InlineData(ImportFailurePolicy.ReplacementRejected)]
    public void A_release_Deluno_has_judged_is_refused_at_once(string reason)
    {
        Assert.Equal(BlockDecision.Immediately, ImportFailurePolicy.BlockFor(reason));
        Assert.True(ImportFailurePolicy.ShouldBlock(reason, priorFailuresOfSameRelease: 0));
    }

    /// <summary>
    /// Where Deluno cannot say whose fault it was, it proves it twice before
    /// refusing anything permanently.
    /// </summary>
    [Theory]
    [InlineData(ImportFailurePolicy.ImportFailed)]
    [InlineData(ImportFailurePolicy.MediaProbeUnreadable)]
    public void An_uncertain_failure_gets_exactly_one_more_try(string reason)
    {
        Assert.Equal(BlockDecision.AfterOneRetry, ImportFailurePolicy.BlockFor(reason));
        Assert.False(ImportFailurePolicy.ShouldBlock(reason, priorFailuresOfSameRelease: 0));
        Assert.True(ImportFailurePolicy.ShouldBlock(reason, priorFailuresOfSameRelease: 1));
    }

    /// <summary>
    /// These say something about this installation, not about the release.
    /// Refusing here is how a blocklist fills with things that were never the
    /// file's fault — the complaint that started all of this.
    /// </summary>
    [Theory]
    [InlineData(ImportFailurePolicy.MissingLibraryRoot)]
    [InlineData(ImportFailurePolicy.MissingSource)]
    [InlineData(ImportFailurePolicy.Permission)]
    [InlineData(ImportFailurePolicy.HardlinkUnavailable)]
    [InlineData(ImportFailurePolicy.HardlinkFailed)]
    [InlineData(ImportFailurePolicy.SamePath)]
    [InlineData(ImportFailurePolicy.Conflict)]
    [InlineData(ImportFailurePolicy.ReplacementOwnershipMismatch)]
    [InlineData(ImportFailurePolicy.Unmatched)]
    [InlineData(ImportFailurePolicy.IoError)]
    public void An_environment_or_naming_problem_never_refuses_the_release(string reason)
    {
        Assert.Equal(BlockDecision.Never, ImportFailurePolicy.BlockFor(reason));
        Assert.False(ImportFailurePolicy.ShouldBlock(reason, priorFailuresOfSameRelease: 5));
    }

    /// <summary>
    /// Two reasons justify deleting what was downloaded, and a third is the
    /// surplus copy from a rejected replacement. Everything else might be
    /// environmental, and deleting on a guess cannot be undone.
    /// </summary>
    [Fact]
    public void Only_a_file_Deluno_knows_is_wrong_gets_deleted()
    {
        var deleted = ImportFailurePolicy.KnownReasons
            .Where(ImportFailurePolicy.ShouldDeletePayload)
            .ToArray();

        Assert.Equal(
            [ImportFailurePolicy.NoVideoStream, ImportFailurePolicy.LikelySample, ImportFailurePolicy.ReplacementRejected],
            deleted);
    }

    /// <summary>
    /// Exactly one import failure is the client's fault: it said the download
    /// was complete and the file was not there. Counting a bad file against the
    /// client is how a healthy client gets blamed, and eventually remediated,
    /// for somebody else's rubbish.
    /// </summary>
    [Fact]
    public void Only_a_vanished_source_counts_against_the_download_client()
    {
        var striking = ImportFailurePolicy.KnownReasons
            .Where(ImportFailurePolicy.CountsAsClientStrike)
            .ToArray();

        Assert.Equal([ImportFailurePolicy.MissingSource], striking);
    }

    /// <summary>
    /// A missing root or a permission problem fails identically for every
    /// title. Carrying on is how you get a hundred failed imports and one root
    /// cause.
    /// </summary>
    [Fact]
    public void A_configuration_problem_stops_the_library_rather_than_repeating_itself()
    {
        var stopping = ImportFailurePolicy.KnownReasons
            .Where(ImportFailurePolicy.StopsSearching)
            .ToArray();

        Assert.Equal(
            [
                ImportFailurePolicy.MissingLibraryRoot,
                ImportFailurePolicy.Permission,
                ImportFailurePolicy.HardlinkUnavailable,
                ImportFailurePolicy.HardlinkFailed
            ],
            stopping);
    }

    /// <summary>
    /// Keeping the copy already held is the guard working. Filing it as a
    /// failure made the dashboard report a fault every time Deluno did the
    /// right thing.
    /// </summary>
    [Fact]
    public void Keeping_the_copy_you_already_have_is_not_a_failure()
    {
        Assert.False(ImportFailurePolicy.IsFailure(ImportFailurePolicy.ReplacementRejected));
        Assert.All(
            ImportFailurePolicy.KnownReasons.Where(reason => reason != ImportFailurePolicy.ReplacementRejected),
            reason => Assert.True(ImportFailurePolicy.IsFailure(reason)));
    }

    /// <summary>
    /// Every reason is filed under whose fault it was, and the filing has to
    /// agree with the decision. A reason refused on sight cannot be filed under
    /// "your setup", because that would put a row on the rules screen whose
    /// heading contradicts its own answer.
    /// </summary>
    [Fact]
    public void What_a_failure_is_filed_under_agrees_with_what_is_done_about_it()
    {
        Assert.All(ImportFailurePolicy.KnownReasons, reason =>
        {
            var category = ImportFailurePolicy.CategoryFor(reason);
            var decision = ImportFailurePolicy.BlockFor(reason);

            Assert.Contains(
                category,
                new[]
                {
                    FailureCategories.BadFile,
                    FailureCategories.CannotSay,
                    FailureCategories.YourSetup,
                    FailureCategories.NotAFailure
                });

            if (category == FailureCategories.YourSetup)
            {
                Assert.Equal(BlockDecision.Never, decision);
            }

            if (category == FailureCategories.BadFile)
            {
                Assert.Equal(BlockDecision.Immediately, decision);
            }
        });
    }

    /// <summary>
    /// Nothing ships asking to be asked. "Ask me" is only ever something the
    /// user chose, which is why a fresh install never stops to consult
    /// anybody.
    /// </summary>
    [Fact]
    public void Deluno_never_ships_a_rule_that_stops_and_asks()
    {
        Assert.All(
            ImportFailurePolicy.KnownReasons,
            reason => Assert.NotEqual(BlockDecision.AskMe, ImportFailurePolicy.BlockFor(reason)));
    }

    /// <summary>
    /// The user's answer wins, in both directions — which is the whole point
    /// of the console, and the reason none of the rows above is law.
    /// </summary>
    [Fact]
    public void An_answer_of_your_own_beats_the_shipped_one_either_way()
    {
        var harsher = new Dictionary<string, BlockDecision>
        {
            [ImportFailurePolicy.MissingSource] = BlockDecision.Immediately
        };
        var softer = new Dictionary<string, BlockDecision>
        {
            [ImportFailurePolicy.NoVideoStream] = BlockDecision.Never
        };

        Assert.Equal(
            BlockDecision.Immediately,
            ImportFailurePolicy.BlockFor(ImportFailurePolicy.MissingSource, harsher));
        Assert.Equal(
            BlockDecision.Never,
            ImportFailurePolicy.BlockFor(ImportFailurePolicy.NoVideoStream, softer));

        // And a reason nobody has an opinion about is untouched by somebody
        // else's opinion.
        Assert.Equal(
            ImportFailurePolicy.BlockFor(ImportFailurePolicy.LikelySample),
            ImportFailurePolicy.BlockFor(ImportFailurePolicy.LikelySample, harsher));
    }

    /// <summary>
    /// And the guard that makes the rest of this table trustworthy: a reason
    /// the import pipeline can actually produce, which nobody has decided
    /// about, fails here rather than quietly taking the "never refuse"
    /// default.
    /// </summary>
    [Fact]
    public void Every_reason_the_import_pipeline_records_has_been_decided()
    {
        // Read off the pipeline itself, so a new failure reason added there
        // cannot slip past this table.
        var source = File.ReadAllText(PipelineSourcePath());
        // Anchored on the call's shape rather than "the first quoted thing
        // after the method name" — the failure *message* is also a string, and
        // a lazy match found the word "io" inside one of them.
        var recorded = System.Text.RegularExpressions.Regex
            .Matches(
                source,
                @"RecordImportFailureAsync\(\s*request,\s*request\.Preview,\s*(?:""(?<literal>[a-zA-Z]+)""|ImportFailurePolicy\.(?<named>[A-Za-z]+))",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(match => match.Groups["literal"].Success
                ? match.Groups["literal"].Value
                : ReasonForConstant(match.Groups["named"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(recorded);

        var undecided = recorded
            .Where(reason => !ImportFailurePolicy.KnownReasons.Contains(reason, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            undecided.Length == 0,
            $"The import pipeline records {string.Join(", ", undecided)}, which the failure table has never decided about. "
            + "Add the reason to ImportFailurePolicy and to DESIGN-007 rather than letting it take the default.");
    }

    /// <summary>
    /// The pipeline names most reasons through the policy's own constants now,
    /// so the guard has to resolve them back to their values.
    /// </summary>
    private static string ReasonForConstant(string constantName)
        => typeof(ImportFailurePolicy)
            .GetField(constantName)?
            .GetValue(null) as string
           ?? constantName;

    private static string PipelineSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Deluno.Filesystem", "ImportPipelineService.cs");
    }
}
