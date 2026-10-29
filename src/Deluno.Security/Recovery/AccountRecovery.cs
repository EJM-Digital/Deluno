using Deluno.Security.Contracts;
using Deluno.Security.Data;

namespace Deluno.Security.Recovery;

public enum RecoveryResult
{
    Done,
    /// <summary>The code did not work. The answer never says which part was wrong.</summary>
    Refused,
    /// <summary>Too many wrong tries in a row.</summary>
    Locked
}

/// <summary>
/// The way back into an account whose username or password is forgotten (#1392): a recovery code from the person's own
/// keeping. With no password and no codes there is no other way in.
/// </summary>
public sealed class AccountRecovery(
    ISecurityRepository repository,
    RecoveryAttempts attempts,
    IAccountResetReporter reporter)
{
    public const string RefusedMessage = "That did not work. Check it and try again.";
    public const string LockedMessage = "Too many tries. Wait 10 minutes and try again.";

    /// <summary>Shows the username for a working code, using nothing up.</summary>
    public async Task<(RecoveryResult Result, string? Username)> CheckAsync(
        RecoveryProofRequest request,
        CancellationToken cancellationToken)
    {
        if (attempts.IsLocked())
        {
            return (RecoveryResult.Locked, null);
        }

        var user = string.IsNullOrWhiteSpace(request.Code)
            ? null
            : await repository.FindUserByRecoveryCodeAsync(request.Code, cancellationToken);
        return Answer(user);
    }

    /// <summary>Sets the new password, uses the code up, signs every session out and tells Activity.</summary>
    public async Task<RecoveryResult> ResetAsync(
        RecoveryResetRequest request,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (attempts.IsLocked())
        {
            return RecoveryResult.Locked;
        }

        var user = string.IsNullOrWhiteSpace(request.Code)
            ? null
            : await repository.ResetPasswordWithRecoveryCodeAsync(request.Code, newPassword, cancellationToken);

        var answer = Answer(user).Result;
        if (answer == RecoveryResult.Done)
        {
            await reporter.ReportResetAsync(cancellationToken);
        }

        return answer;
    }

    private (RecoveryResult Result, string? Username) Answer(UserItem? user)
    {
        if (user is null)
        {
            attempts.Failed();
            return (RecoveryResult.Refused, null);
        }

        attempts.Succeeded();
        return (RecoveryResult.Done, user.Username);
    }
}
