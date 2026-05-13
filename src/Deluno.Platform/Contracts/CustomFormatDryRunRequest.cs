namespace Deluno.Platform.Contracts;

public sealed record CustomFormatDryRunRequest(string ReleaseName, string? MediaType = null);
