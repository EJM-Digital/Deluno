namespace Deluno.Platform.Contracts;

public sealed record DeferAutomationRequest(string? LibraryId, int? Hours = null);
