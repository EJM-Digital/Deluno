namespace Deluno.Security.Data;

/// <summary>
/// The first account was asked for when one already exists, including when another first-run request made it a moment
/// earlier. The bootstrap route answers it with 409 in plain words.
/// </summary>
public sealed class DelunoAlreadyConfiguredException() : InvalidOperationException("Deluno has already been configured.");
