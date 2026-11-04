using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Tests.Support;

/// <summary>Keeps every line a service logs, as "Level: message", and the exception that came with it, so a test can say what was logged.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) { _entries.Add((logLevel, formatter(state, exception), exception)); }
    }
}
