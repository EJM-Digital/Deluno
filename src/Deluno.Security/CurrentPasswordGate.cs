using Deluno.Security.Data;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security;

/// <summary>
/// Changes that could hand the account to someone else ask for the current password (#1433), also from a device the
/// home-network skip let in without one: creating or deleting an API key, making a new set of recovery codes and
/// changing what the skip allows. The password travels in the <see cref="Header"/> header. A caller that is an API key
/// rather than a person is not asked: a key is already a credential of its own and has no password to give.
/// </summary>
public static class CurrentPasswordGate
{
    public const string Header = "X-Current-Password";

    /// <summary>Null when the change may go ahead; otherwise the plain refusal to send back.</summary>
    public static async Task<IResult?> RequireAsync(
        HttpContext httpContext,
        ISecurityRepository repository,
        CancellationToken cancellationToken)
    {
        if (UserAuthorization.TryReadApiKey(httpContext, out _))
        {
            return null;
        }

        UserAuthorization.TryReadUser(httpContext, out var user);
        user ??= await repository.GetSoleUserAsync(cancellationToken);
        if (user is null)
        {
            return null;
        }

        var supplied = httpContext.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(supplied))
        {
            return Refuse("Enter your current password to make this change.");
        }

        return await repository.VerifyUserPasswordAsync(user.Id, supplied, cancellationToken)
            ? null
            : Refuse("Current password is not correct.");
    }

    private static IResult Refuse(string message)
        => Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = [message] });
}
