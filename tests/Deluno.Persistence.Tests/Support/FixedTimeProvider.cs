namespace Deluno.Persistence.Tests.Support;

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset current = utcNow;

    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration) => current = current.Add(duration);
}
