namespace Deluno.Jobs.Contracts;

public sealed record JobQueueItem(
    string Id,
    string JobType,
    string Source,
    string Status,
    string? PayloadJson,
    int Attempts,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ScheduledUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? LeasedUntilUtc,
    string? WorkerId,
    string? LastError,
    string? RelatedEntityType,
    string? RelatedEntityId,
    string? IdempotencyKey,
    string? DedupeKey,
    int MaxAttempts,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextAttemptUtc)
{
    /// <summary>What went wrong. An older row's raw exception text or ISO retry time reads in plain words (#1095).</summary>
    public string? LastError { get; init; } = LastError is null ? null : Deluno.Contracts.PlainFailure.Rewrite(LastError, LastAttemptUtc ?? CreatedUtc);

    /// <summary>The job in plain words (#1086); screens show this, never <see cref="JobType"/>.</summary>
    public string Label => Deluno.Contracts.JobTypeLabels.For(JobType);

    /// <summary>What started the job in plain words (#1086); screens show this, never <see cref="Source"/>.</summary>
    public string SourceLabel => Deluno.Contracts.JobSourceLabels.For(Source);

    /// <summary>
    /// The one thing the job is about, in words, such as "Severance S02E10" (#1180). Read from the job's own episode and
    /// series when the list is served; null for a job that is not about an episode.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>The show an episode job belongs to, so a show-wide search can be read as one entry (#1180).</summary>
    public string? ShowId { get; init; }

    /// <summary>The show's title, when the job is about one of its episodes (#1180).</summary>
    public string? ShowTitle { get; init; }
}
