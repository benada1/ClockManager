using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// Asserts "the host refuses to start because of options validation" without a race.
/// <para>
/// Why not just catch what <c>CreateClient()</c> throws (the old pattern, flaky under CPU load, found in plan 01
/// step 4): with minimal hosting, <c>WebApplicationFactory</c> runs <c>Program</c> on its own thread. When
/// <c>ValidateOnStart</c> throws there, <c>app.Run()</c> disposes the host. If the test thread's
/// <c>DeferredHost.StartAsync</c> runs after that, it fails with
/// <c>ObjectDisposedException: 'IServiceProvider'</c> instead of the <see cref="OptionsValidationException"/>,
/// so the old assertion saw no validation error. The host always logs "Hosting failed to start" with the
/// real exception BEFORE disposing, so that log entry is a deterministic source.
/// </para>
/// </summary>
internal static class HostStartup
{
    /// <summary>Starts the host and returns the options validation error it failed with, or null if there was none.</summary>
    public static OptionsValidationException? FindStartupValidationFailure(WebApplicationFactory<Program> factory)
    {
        var logged = new ConcurrentQueue<Exception>();
        using var capturing = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<ILoggerProvider>(new ExceptionCapturingLoggerProvider(logged))));

        var thrown = Record.Exception(() => capturing.CreateClient());

        return Find(thrown) ?? logged.Select(Find).FirstOrDefault(e => e is not null);
    }

    private static OptionsValidationException? Find(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException ove) return ove;
            if (current is AggregateException agg)
            {
                return agg.InnerExceptions.Select(Find).FirstOrDefault(e => e is not null);
            }
        }
        return null;
    }

    private sealed class ExceptionCapturingLoggerProvider(ConcurrentQueue<Exception> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink);

        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<Exception> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is not null) sink.Enqueue(exception);
            }
        }
    }
}
