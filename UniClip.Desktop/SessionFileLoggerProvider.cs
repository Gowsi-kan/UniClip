using System;
using Microsoft.Extensions.Logging;
using UniClip.Services;

namespace UniClip.Desktop;

internal sealed class SessionFileLoggerProvider : ILoggerProvider
{
    private static readonly string[] LoggedCategories =
    [
        "UniClip.Rendezvous.Services.LanDiscoveryService",
        "UniClip.Rendezvous.Endpoints.PairingEndpoints"
    ];

    public ILogger CreateLogger(string categoryName) =>
        new SessionFileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class SessionFileLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            SessionDiagnostics.Enabled &&
            Array.IndexOf(LoggedCategories, categoryName) >= 0 &&
            logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                SessionDiagnostics.Write(
                    categoryName,
                    $"{logLevel} EventId={eventId.Id}: {formatter(state, exception)}",
                    exception);
            }
        }
    }
}
