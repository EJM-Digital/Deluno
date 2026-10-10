namespace Deluno.Platform.Contracts;

/// <summary>
/// Told when a processor hand-off is dismissed, whoever dismissed it: a person on Transfers, Activity or the bell, or
/// Deluno itself when the title had its file (#1228). The records that mirror the hand-off (the import issue raised
/// for it, and the System health row for that issue) close with it, so one dismissal clears the problem on every
/// screen that counts it. Implemented by the modules that own those records.
/// </summary>
public interface IHandoffClosedListener
{
    Task HandoffClosedAsync(string handoffId, CancellationToken cancellationToken);
}
