namespace Deluno.Quality.Contracts;

public sealed record CustomFormatDryRunRequest(string ReleaseName, string? MediaType = null);
