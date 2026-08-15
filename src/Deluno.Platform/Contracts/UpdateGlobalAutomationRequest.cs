namespace Deluno.Platform.Contracts;

/// <summary>
/// Controls whether Deluno's background worker may process queued work.
/// It never pauses, removes, or otherwise changes items in an external
/// download client.
/// </summary>
public sealed record UpdateGlobalAutomationRequest(bool IsEnabled);
