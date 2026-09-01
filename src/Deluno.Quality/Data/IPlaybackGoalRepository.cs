using Deluno.Quality.Playback;

namespace Deluno.Quality.Data;

public interface IPlaybackGoalRepository
{
    Task<IReadOnlyList<PlaybackDeviceProfile>> ListDeviceProfilesAsync(CancellationToken cancellationToken);
    Task<PlaybackDeviceProfile?> GetDeviceProfileAsync(string id, CancellationToken cancellationToken);
    Task<PlaybackDeviceProfile> CreateDeviceProfileAsync(CreatePlaybackDeviceProfileRequest request, CancellationToken cancellationToken);
    Task<PlaybackDeviceProfile?> UpdateDeviceProfileAsync(string id, UpdatePlaybackDeviceProfileRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteDeviceProfileAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaybackDeviceGroup>> ListDeviceGroupsAsync(CancellationToken cancellationToken);
    Task<PlaybackDeviceGroup?> GetDeviceGroupAsync(string id, CancellationToken cancellationToken);
    Task<PlaybackDeviceGroup> CreateDeviceGroupAsync(CreatePlaybackDeviceGroupRequest request, CancellationToken cancellationToken);
    Task<PlaybackDeviceGroup?> UpdateDeviceGroupAsync(string id, UpdatePlaybackDeviceGroupRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteDeviceGroupAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaybackGoalItem>> ListGoalsAsync(CancellationToken cancellationToken);
    Task<PlaybackGoalItem?> GetGoalAsync(string id, CancellationToken cancellationToken);
    Task<PlaybackGoalItem> CreateGoalAsync(CreatePlaybackGoalRequest request, CancellationToken cancellationToken);
    Task<PlaybackGoalItem?> UpdateGoalAsync(string id, UpdatePlaybackGoalRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteGoalAsync(string id, CancellationToken cancellationToken);
}
