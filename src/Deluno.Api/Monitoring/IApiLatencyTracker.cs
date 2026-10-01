namespace Deluno.Api.Monitoring;

/// <summary>How quickly requests were answered over a window: the typical one (median) and the slowest 5%.</summary>
public sealed record ApiAnswerTimes(int RequestCount, double TypicalMs, double SlowestFivePercentMs);

/// <summary>Requests and server errors counted since the day began, or since Deluno started when that is later.</summary>
public sealed record ApiDayTotals(long Requests, long Errors, DateTimeOffset SinceUtc);

public interface IApiLatencyTracker
{
    void Record(string path, double durationMs, int statusCode);
    ApiLatencySnapshot GetSnapshot(TimeSpan? window = null);

    ApiAnswerTimes GetAnswerTimes(TimeSpan window);

    ApiDayTotals GetDayTotals();
}
