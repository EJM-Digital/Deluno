namespace Deluno.Api.Monitoring;

public interface IApiLatencyTracker
{
    void Record(string path, double durationMs, int statusCode);
    ApiLatencySnapshot GetSnapshot(TimeSpan? window = null);
}
