namespace Deluno.Security.Contracts;

/// <summary>What the Account screen and the first-sign-in prompt show: how many codes are left, and whether any were ever made (#1392).</summary>
public sealed record RecoveryCodesStatus(int Remaining, bool Made);

/// <summary>A new set of recovery codes, in the clear exactly once; only their hashes are kept.</summary>
public sealed record RecoveryCodesMade(IReadOnlyList<string> Codes);

/// <summary>Whether this install can offer the one-time PIN: only a host running in a container can.</summary>
public sealed record RecoveryOptions(bool PinAvailable);

/// <summary>Proof of who is asking: a recovery code, or a one-time PIN read from the container.</summary>
public sealed record RecoveryProofRequest(string? Code, string? Pin);

public sealed record RecoveryResetRequest(string? Code, string? Pin, string? NewPassword);

public sealed record RecoveryUsername(string Username);

public sealed record RecoveryPinMade(string Pin, DateTimeOffset ExpiresUtc);

/// <summary>The first-run answer: the signed-in session plus the account's first recovery codes, shown once.</summary>
public sealed record BootstrapResponse(
    string AccessToken,
    DateTimeOffset ExpiresUtc,
    UserItem User,
    IReadOnlyList<string> RecoveryCodes);
