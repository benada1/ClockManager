using System.Collections.Concurrent;
using System.Net;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClockManager.Tests.Common;

/// <summary>
/// QA-20 (D-038): handled refusals (AppException) must not log at Error, but a real unexpected exception must.
/// Runs without a database, on the same test-only endpoints as <see cref="ProblemDetailsTests"/>.
/// </summary>
public sealed class ErrorLoggingTests
{
    [Theory]
    [InlineData("/test-only/app-error", HttpStatusCode.Conflict)]
    [InlineData("/api/does-not-exist", HttpStatusCode.NotFound)] // UseStatusCodePages path, no exception
    public async Task Handled_responses_never_log_at_Error_and_keep_their_status(string path, HttpStatusCode expected)
    {
        var (response, logs) = await GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(logs, l => l.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_handled_refusal_is_still_logged_once_below_Error_by_the_application_handler()
    {
        var (_, logs) = await GetAsync("/test-only/app-error");

        var entry = Assert.Single(logs, l => l.Category == typeof(AppExceptionHandler).FullName);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public async Task A_handled_refusal_response_is_not_cacheable()
    {
        var (response, _) = await GetAsync("/test-only/app-error");

        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task An_unexpected_exception_still_logs_at_Error_with_the_exception()
    {
        var (response, logs) = await GetAsync("/test-only/unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Category == typeof(AppExceptionHandler).FullName && l.Exception is InvalidOperationException);
    }

    private static async Task<(HttpResponseMessage Response, List<(LogLevel Level, string Category, Exception? Exception)> Logs)> GetAsync(string path)
    {
        var sink = new ConcurrentQueue<(LogLevel, string, Exception?)>();
        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(new Capture(sink))));
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        await response.Content.ReadAsStringAsync();
        return (response, sink.ToList());
    }

    private sealed class Capture(ConcurrentQueue<(LogLevel, string, Exception?)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink, categoryName);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string, Exception?)> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((logLevel, category, exception));
        }
    }
}
