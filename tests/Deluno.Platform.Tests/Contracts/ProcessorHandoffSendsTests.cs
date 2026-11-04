using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests.Contracts;

public sealed class ProcessorHandoffSendsTests
{
    [Fact]
    public void A_hand_off_being_sent_cannot_be_started_a_second_time_until_the_first_ends()
    {
        var id = "handoff-" + Guid.NewGuid().ToString("N");

        var first = ProcessorHandoffSends.TryBegin(id);

        Assert.NotNull(first);
        Assert.True(ProcessorHandoffSends.IsSending(id));
        Assert.Null(ProcessorHandoffSends.TryBegin(id));
        Assert.Null(ProcessorHandoffSends.TryBegin(id.ToUpperInvariant()));

        first.Dispose();

        Assert.False(ProcessorHandoffSends.IsSending(id));
        using var second = ProcessorHandoffSends.TryBegin(id);
        Assert.NotNull(second);
    }

    [Fact]
    public void Ending_a_send_twice_does_not_end_the_next_one()
    {
        var id = "handoff-" + Guid.NewGuid().ToString("N");
        var first = ProcessorHandoffSends.TryBegin(id)!;
        first.Dispose();
        using var next = ProcessorHandoffSends.TryBegin(id);

        first.Dispose();

        Assert.True(ProcessorHandoffSends.IsSending(id));
    }

    [Fact]
    public void Different_hand_offs_are_sent_independently()
    {
        using var one = ProcessorHandoffSends.TryBegin("a-" + Guid.NewGuid().ToString("N"));
        using var two = ProcessorHandoffSends.TryBegin("b-" + Guid.NewGuid().ToString("N"));

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.False(ProcessorHandoffSends.IsSending("never-started"));
    }
}
