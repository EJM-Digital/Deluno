namespace Deluno.Security.Contracts;

/// <summary>What the Account screen and the first-sign-in prompt show: how many codes are left, and whether any were ever made (#1392).</summary>
public sealed record RecoveryCodesStatus(int Remaining, bool Made);

/// <summary>A new set of recovery codes, in the clear exactly once; only their hashes are kept.</summary>
public sealed record RecoveryCodesMade(IReadOnlyList<string> Codes);

/// <summary>A recovery code, typed by a person who has forgotten their username or password.</summary>
public sealed record RecoveryProofRequest(string? Code);

public sealed record RecoveryResetRequest(string? Code, string? NewPassword);

public sealed record RecoveryUsername(string Username);

/// <summary>The first-run answer: the signed-in session plus the account's first recovery codes, shown once.</summary>
public sealed record BootstrapResponse(
    string AccessToken,
    DateTimeOffset ExpiresUtc,
    UserItem User,
    IReadOnlyList<string> RecoveryCodes);
