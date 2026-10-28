namespace Deluno.Contracts;

/// <summary>How often an import list may be checked. The API's refusal and the sync planner's clamp both read this, and a test ties the screen's longest preset to it.</summary>
public static class IntakeSyncLimits
{
    public const int MinHours = 1;
    public const int MaxHours = 168;
}
