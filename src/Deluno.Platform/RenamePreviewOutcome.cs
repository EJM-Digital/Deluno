namespace Deluno.Platform;

/// <summary>
/// What a single title's rename preview says (#997), in one place for both
/// engines so the wording and the decision cannot drift between them.
///
/// <para>The preview is read-only. It names the folder Deluno would rename and
/// what it would become, or gives the plain reason it would leave it alone.
/// Only the folder is ever renamed, never a file inside it, and nothing is
/// moved to another library.</para>
/// </summary>
public sealed record RenamePreviewOutcome(
    string Outcome,
    string? CurrentFolder,
    string ProposedFolder,
    bool WillChange,
    string Message)
{
    /// <summary>
    /// The preview for a title whose folder the shared guard refused to touch
    /// (#1067): says in plain words why, and that nothing would change.
    /// </summary>
    public static RenamePreviewOutcome Refused(TitleFolderTarget refusal, string proposedFolder) =>
        new(refusal.Code, null, proposedFolder, false, refusal.Message);

    public static RenamePreviewOutcome Describe(
        string? currentFolder,
        string proposedFolder,
        bool hasFolder,
        bool destinationExists)
    {
        if (!hasFolder || string.IsNullOrWhiteSpace(currentFolder))
        {
            return new RenamePreviewOutcome(
                "no-files", currentFolder, proposedFolder, false,
                "There is no file on record for this title yet, so there is no folder to rename.");
        }

        if (string.Equals(currentFolder, proposedFolder, StringComparison.OrdinalIgnoreCase))
        {
            return new RenamePreviewOutcome(
                "already-named", currentFolder, proposedFolder, false,
                "The folder already has the name your naming settings give it. Nothing would change.");
        }

        if (destinationExists)
        {
            return new RenamePreviewOutcome(
                "name-taken", currentFolder, proposedFolder, false,
                $"A folder named \"{proposedFolder}\" already exists, so Deluno would leave this one alone.");
        }

        return new RenamePreviewOutcome(
            "will-rename", currentFolder, proposedFolder, true,
            $"Deluno would rename the folder \"{currentFolder}\" to \"{proposedFolder}\". The files inside keep their names and nothing is deleted.");
    }
}
