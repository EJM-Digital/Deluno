using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// A hand-off a processor stopped answering for, reported never having, or
/// held with no change for the library's limit -- held, not imported, until a
/// person decides (#914, #929).
///
/// <para><see cref="Deluno.Worker.Services.ProcessorHandoffWatcher"/> already writes the plain-words
/// reason to the hand-off's own record when it stops waiting
/// (<c>FailureMessage</c>) -- this check reads that back rather than working
/// it out again, so System &#8594; Health says exactly what Activity said.</para>
///
/// <para>Only the three statuses a person has not yet acted on are shown:
/// dismissing, importing the original, or retrying moves a hand-off out of
/// them, which is also how this stops appearing once dealt with.</para>
/// </summary>
public sealed class ProcessorHandoffHoldHealthCheck(IProcessorRepository processors) : IHealthCheck
{
    public string Category => HealthCheckCategories.Processor;

    private static readonly IReadOnlyList<string> HeldStatuses =
    [
        ProcessorHandoffStatuses.Stalled,
        ProcessorHandoffStatuses.TimedOut,
        ProcessorHandoffStatuses.Lost
    ];

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var held = await processors.ListProcessorHandoffsWithStatusAsync(HeldStatuses, cancellationToken);
        if (held.Count == 0)
        {
            return [];
        }

        return held.Select(handoff =>
        {
            var id = $"processor-handoff:{handoff.Id}";
            var name = $"{handoff.ReleaseName} is held with {handoff.ProcessorName ?? "its processor"}";
            var reason = handoff.FailureMessage is { Length: > 0 } message
                ? message
                : $"{handoff.ReleaseName} is being held rather than handed to import.";
            var options = handoff.Status switch
            {
                ProcessorHandoffStatuses.Lost =>
                    "Import the original, hand it to the processor again, or search for another release.",
                _ => "Import the original now, keep waiting, ask the processor again, or give up and search for another release."
            };

            return HealthCheckOutcome.Problem(
                id,
                Category,
                name,
                reason,
                cause: "This shouldn't happen: a hand-off Deluno stopped waiting on, still waiting for you.",
                fix: options,
                detailsJson: $$"""{"handoffId":"{{handoff.Id}}","libraryId":"{{handoff.LibraryId}}","status":"{{handoff.Status}}"}""");
        }).ToArray();
    }
}
