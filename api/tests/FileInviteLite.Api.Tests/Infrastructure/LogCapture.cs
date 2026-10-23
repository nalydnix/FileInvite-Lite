using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FileInviteLite.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps every log entry in memory, at every level, so a test
/// can assert that a secret never reached a log (S2.13). Each entry is stored as one string holding
/// the formatted message, the exception (type, message and stack) and every structured value and
/// scope — the places a token could hide.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    /// <summary>Attaches this provider at Trace, overriding any configured filter for it.</summary>
    public void AddTo(ILoggingBuilder logging)
    {
        logging.SetMinimumLevel(LogLevel.Trace);
        logging.AddProvider(this);
        logging.AddFilter<LogCapture>(category: null, LogLevel.Trace);
    }

    /// <summary>Fails if any captured entry contains <paramref name="secret"/>.</summary>
    public void AssertNoneContain(string secret, string what)
    {
        Assert.False(string.IsNullOrEmpty(secret), $"Test bug: empty {what} would match every line.");

        var leaks = _entries.Where(e => e.Contains(secret, StringComparison.Ordinal)).ToList();

        // The entry text is reported without the secret itself so a failing run does not
        // reproduce it into the test output a second time.
        Assert.True(leaks.Count == 0,
            $"The {what} appeared in {leaks.Count} log entr{(leaks.Count == 1 ? "y" : "ies")}. " +
            $"First category: {leaks.FirstOrDefault()?.Split('\n')[0]}");
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new Scope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = new StringBuilder()
                .Append('[').Append(logLevel).Append("] ").Append(category).Append('\n')
                .Append(formatter(state, exception));

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    text.Append('\n').Append(key).Append('=').Append(value);
                }
            }

            if (exception is not null)
            {
                text.Append('\n').Append(exception);
            }

            foreach (var scope in Scope.Current())
            {
                text.Append("\nscope=").Append(scope);
            }

            sink.Enqueue(text.ToString());
        }
    }

    /// <summary>Scopes are recorded too: a token in a scope value is still a token in a log.</summary>
    private sealed class Scope : IDisposable
    {
        private static readonly AsyncLocal<ImmutableStack?> Stack = new();
        private readonly ImmutableStack? _previous;

        public Scope(object? state)
        {
            _previous = Stack.Value;
            Stack.Value = new ImmutableStack(RenderScope(state), _previous);
        }

        public static IEnumerable<string> Current()
        {
            for (var node = Stack.Value; node is not null; node = node.Next)
            {
                yield return node.Value;
            }
        }

        public void Dispose() => Stack.Value = _previous;

        private static string RenderScope(object? state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                return string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            return state?.ToString() ?? string.Empty;
        }

        private sealed record ImmutableStack(string Value, ImmutableStack? Next);
    }
}
