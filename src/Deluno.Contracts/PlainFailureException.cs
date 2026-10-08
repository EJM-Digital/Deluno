namespace Deluno.Contracts;

/// <summary>
/// A failure whose message is already the everyday-words reason, such as "The destination file already exists."
/// <see cref="PlainFailure.Describe"/> shows it as it is instead of the generic "Something went wrong while ...".
/// </summary>
public sealed class PlainFailureException(string plainReason) : InvalidOperationException(plainReason);
