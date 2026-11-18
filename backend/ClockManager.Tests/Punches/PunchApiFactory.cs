using System.Collections.Concurrent;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Punches;

/// <summary>
/// The real API host on the test class's throwaway
/// database, with <see cref="ITrustedClock"/> replaced by a <see cref="FakeTrustedClock"/>. No network. Every log entry
/// is captured so a test can check what was (not) logged. A request with the test-only header <c>X-Test-User: name</c> gets a
/// REAL signed login cookie for that employee (see <see cref="SignInTestUser"/>), so the real JWT check and ICurrentEmployee run.
/// </summary>
internal sealed class PunchApiFactory(
    MigratedDatabaseFixture database,
    FakeTrustedClock clock,
    string environment = "Development",
    IElapsedTimer? timer = null)
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
            ["RateLimits:AuthPermitLimit"] = "100000", // limits never get in the way, except in RateLimitingTests
            ["RateLimits:PunchPermitLimit"] = "100000",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITrustedClock>(clock); // last registration wins
            if (timer is not null) services.AddSingleton(timer); // a hand-moved monotonic timer: the status cache TTL and ages
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs));
            services.AddSingleton<IStartupFilter>(new TrackCompletedRequests(Completed));
            services.AddSingleton<IStartupFilter>(new SignInTestUser());
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

    public const string TestUserHeader = "X-Test-User";

    /// <summary>
    /// Test login helper: turns <c>X-Test-User</c> into the login cookie the real <c>ITokenIssuer</c> would give that employee
    /// (no password step; login itself is covered by AuthApiTests). An unknown name gets no cookie, so the request is anonymous.
    /// </summary>
    private sealed class SignInTestUser : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                if (context.Request.Headers.TryGetValue(TestUserHeader, out var names) && names.Count == 1 && !string.IsNullOrEmpty(names[0]))
                {
                    var services = context.RequestServices;
                    var name = names[0]!;
                    var found = await services.GetRequiredService<ClockManagerDbContext>().Employees.AsNoTracking()
                        .Where(e => e.Username == name)
                        .Select(e => new { e.Id, e.Role })
                        .SingleOrDefaultAsync();
                    if (found is not null)
                    {
                        var token = services.GetRequiredService<ITokenIssuer>().Issue(found.Id, found.Role);
                        context.Request.Headers.Cookie = $"{services.GetRequiredService<IOptions<AuthOptions>>().Value.CookieName}={token.Value}";
                    }
                }
                await inner(context);
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
                if (!IsEnabled(logLevel)) return;
                var message = formatter(state, exception);
                // TestServer has no body-size feature, so [RequestSizeLimit] warns on every request here (Kestrel does not): not a finding.
                if (message.Contains("request body size limit could not be applied", StringComparison.Ordinal)) return;
                sink.Enqueue(new LogEntry(logLevel, category, message, exception));
            }
        }
    }
}
