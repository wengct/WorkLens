using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WorkLens.Logging;

public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object writeLock = new();
    private readonly string logDirectory;
    private bool disposed;

    public LocalFileLoggerProvider(string logDirectory)
    {
        this.logDirectory = ResolveDirectory(logDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        new LocalFileLogger(categoryName, this);

    internal bool IsEnabled(LogLevel logLevel) =>
        !disposed && logLevel != LogLevel.None;

    internal void Write(
        LogLevel logLevel,
        string categoryName,
        EventId eventId,
        string message,
        Exception? exception)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(logDirectory);
            var now = DateTimeOffset.Now;
            var path = Path.Combine(logDirectory, $"worklens-{now:yyyy-MM-dd}.log");
            var eventName = eventId.Name is null
                ? eventId.Id.ToString(CultureInfo.InvariantCulture)
                : $"{eventId.Id}:{eventId.Name}";
            var entry = new StringBuilder()
                .Append(now.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [")
                .Append(logLevel)
                .Append("] ")
                .Append(categoryName)
                .Append(" (")
                .Append(eventName)
                .Append("): ")
                .AppendLine(message);

            if (exception is not null)
            {
                entry.AppendLine(exception.ToString());
            }

            lock (writeLock)
            {
                File.AppendAllText(path, entry.ToString(), Utf8NoBom);
            }
        }
        catch (Exception loggingException) when (
            loggingException is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            // Logging must never become the reason the application stops.
            Debug.WriteLine($"WorkLens local log write failed: {loggingException.Message}");
        }
    }

    public void Dispose()
    {
        disposed = true;
    }

    private static string ResolveDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Path.Combine(Path.GetTempPath(), "WorkLens", "logs");
        }

        try
        {
            return Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"WorkLens local log path is invalid: {exception.Message}");
            return Path.Combine(Path.GetTempPath(), "WorkLens", "logs");
        }
    }

    private sealed class LocalFileLogger(
        string categoryName,
        LocalFileLoggerProvider provider) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            try
            {
                provider.Write(logLevel, categoryName, eventId, formatter(state, exception), exception);
            }
            catch (Exception loggingException) when (
                loggingException is InvalidOperationException or
                ArgumentException)
            {
                // A malformed log state must not affect the request or background task.
                Debug.WriteLine($"WorkLens local log formatting failed: {loggingException.Message}");
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}

public static class LocalFileLoggerExtensions
{
    public static ILoggingBuilder AddLocalFile(
        this ILoggingBuilder builder,
        string logDirectory)
    {
        builder.AddProvider(new LocalFileLoggerProvider(logDirectory));
        return builder;
    }
}
