using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Moq;

namespace Deluno.Platform.Tests.Processing;

/// <summary>
/// Refs #1529. The two questions the missing-output mark asks of the stores behind the hand-off interfaces have a safe answer
/// for an implementation that does not know them: no, the title is not waiting for its file, and no, nothing was newly kept.
/// With "no" a processor report naming an absent output is never turned into a failure by a store that cannot judge it.
/// </summary>
public sealed class AnImplementationThatDoesNotKnowAboutMissingOutputsSaysNoTests
{
    [Fact]
    public async Task A_title_is_not_said_to_wait_for_its_file_unless_the_store_can_tell()
    {
        // The import queue's default member; the arguments are not read by the default, so a bare mock supplies them.
        var queue = new Mock<IProcessorOutputImportQueue> { CallBase = true };

        var waits = await queue.Object.TitleWaitsForItsFileAsync(null!, null!, "out", CancellationToken.None);

        Assert.False(waits);
    }

    [Fact]
    public async Task A_missing_output_is_not_said_to_be_newly_kept_unless_the_store_keeps_it()
    {
        var repository = new Mock<IProcessorRepository> { CallBase = true };

        var kept = await repository.Object.MarkMissingOutputAsync("handoff-1", "out", CancellationToken.None);

        Assert.False(kept);
    }
}
