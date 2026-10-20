using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RemoteConnect.Desktop.Services;

/// <summary>Daily rolling log files (14 days). Callers never log secrets, clipboard text or screen data.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(4096);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Cleanup();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "RemoteConnect log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line)
    {
        _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"remoteconnect-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }

    private void Cleanup()
    {
        foreach (var f in Directory.GetFiles(_directory, "remoteconnect-*.log"))
        {
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14))
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel.ToString()[..4].ToUpperInvariant()}] {shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Enqueue(line + Environment.NewLine);
        }
    }
}
