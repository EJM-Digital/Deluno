namespace Deluno.Jobs.Contracts;

/// <summary>How many Activity rows of one category were written in one hour of the day (#1096).</summary>
public sealed record ActivityHourCount(int Hour, string Category, int Count);
