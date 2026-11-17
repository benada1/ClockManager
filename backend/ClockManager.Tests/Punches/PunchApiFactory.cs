using System.Collections.Concurrent;
using ClockManager.Api.Application.Time;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClockManager.Tests.Punches;

/// <summary>
/// The real API host (default: Development, so the <c>X-Dev-Employee</c> header works) on the test class's throwaway
/// database, with <see cref="ITrustedClock"/> replaced by a <see cref="FakeTrustedClock"/>. No network. Every log entry
/// is captured so a test can check what was (not) logged.
/// </summary>
internal sealed class PunchApiFactory(MigratedDatabaseFixture database, FakeTrustedClock clock, string environment = "Development")
    : WebApplicationFactory<Program>
{
    public ConcurrentQueue<LogEntry> Logs { get; } = new();

    /// <summary>One entry per request when the server has completely finished it (after the error handler).</summary>
    public ConcurrentQueue<CompletedRequest> Completed { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClockManager"] = database.ConnectionString,
            ["Database:MigrateAndSeedOnStartup"] = "false", // the fixture already migrated; tests add their own employees
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITrustedClock>(clock); // last registration wins
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs));
            services.AddSingleton<IStartupFilter>(new TrackCompletedRequests(Completed));
        });
    }

    public sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

    public sealed record CompletedRequest(int Status, bool Aborted);

    /// <summary>Outermost middleware: records each request once the whole pipeline has returned.</summary>
    private sealed class TrackCompletedRequests(ConcurrentQueue<CompletedRequest> sink) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                try
                {
                    await inner(context);
                }
                finally
                {
                    sink.Enqueue(new CompletedRequest(context.Response.StatusCode, context.RequestAborted.IsCancellationRequested));
                }
            });
            next(app);
        };
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<LogEntry> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink, categoryName);

        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<LogEntry> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception));
            }
        }
    }
}
