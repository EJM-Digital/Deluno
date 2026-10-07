using Microsoft.AspNetCore.Http;

namespace Deluno.Media;

/// <summary>
/// The answer to a hand send that needs a decision first (#1175): 409 with what is already
/// downloading and where, so the screen can ask Replace, Send as well or Cancel in plain words.
/// </summary>
public static class ActiveDownloadResponse
{
    public const string Code = "already-downloading";

    public static IResult Conflict(ActiveDownloadConflict conflict)
        => Results.Json(
            new
            {
                code = Code,
                message = $"{conflict.Subject} is already downloading in {conflict.ClientName}. Send this one as well?",
                subject = conflict.Subject,
                clientName = conflict.ClientName,
                releaseName = conflict.ReleaseName,
                duplicateActions = new[] { DuplicateSendActions.Replace, DuplicateSendActions.SendAsWell }
            },
            statusCode: StatusCodes.Status409Conflict);
}
