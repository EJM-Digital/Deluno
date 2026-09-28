using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

/// <summary>
/// Gives tags to titles for the sources that add them (#1046). The tag
/// definitions live in the platform database and the assignments beside the
/// title, so this joins the two the way the title's own tag editor does.
/// </summary>
public sealed class TitleTagService(
    IPlatformSettingsRepository platformSettings,
    IMediaTagStore mediaTagStore) : ITitleTagService
{
    public async Task<IReadOnlyList<string>> ListNamesAsync(MediaKind kind, string mediaId, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(mediaId)
            ? []
            : (await mediaTagStore.ListAsync(kind, mediaId, cancellationToken)).Select(item => item.Name).ToArray();

    public async Task<IReadOnlyList<string>> GiveAsync(
        MediaKind kind,
        string mediaId,
        IEnumerable<string> tagNames,
        CancellationToken cancellationToken)
    {
        var wanted = tagNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (wanted.Length == 0 || string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        var current = (await mediaTagStore.ListAsync(kind, mediaId, cancellationToken)).ToList();
        var definitions = (await platformSettings.ListTagsAsync(cancellationToken)).ToList();
        var given = new List<string>();
        foreach (var name in wanted)
        {
            if (current.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var definition = definitions.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                definition = await platformSettings.CreateTagAsync(new CreateTagRequest(name, null, null), cancellationToken);
                definitions.Add(definition);
            }

            current.Add(new MediaTagAssignment(definition.Id, definition.Name));
            given.Add(definition.Name);
        }

        if (given.Count > 0)
        {
            await mediaTagStore.ReplaceAsync(kind, mediaId, current, cancellationToken);
        }

        return given;
    }
}
