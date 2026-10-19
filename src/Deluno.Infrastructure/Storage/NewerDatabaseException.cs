namespace Deluno.Infrastructure.Storage;

/// <summary>
/// A database that a newer Deluno has already updated. Starting on it would run old code on a newer layout, so the start stops,
/// in plain words, with the data untouched (#1361).
/// </summary>
public sealed class NewerDatabaseException(string message) : InvalidOperationException(message);
