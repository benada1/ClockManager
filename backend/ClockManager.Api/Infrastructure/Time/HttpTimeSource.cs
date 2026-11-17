using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NodaTime;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// Shared HTTP mechanics for a time provider: one GET with its own timeout, round-trip measurement on a
/// monotonic timer, status mapping, a body-size cap, and turning every provider problem into a typed
/// <see cref="TimeSourceFailure"/>. Subclasses only say where to ask and how to validate the body.
/// </summary>
internal abstract class HttpTimeSource : ITimeSource
{
    /// <summary>Real bodies are ~400 bytes; anything far larger is not a time response.</summary>
    internal const int MaxBodyBytes = 16 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _http;
    private readonly IElapsedTimer _timer;
    private readonly TimeSpan _timeout;

    protected HttpTimeSource(HttpClient http, IElapsedTimer timer, IOptions<TrustedClockOptions> options, string name)
    {
        _http = http;
        _timer = timer;
        _timeout = TimeSpan.FromMilliseconds(options.Value.TimeoutMs);
        Name = name;
    }

    public string Name { get; }

    /// <summary>Path relative to the configured base URL.</summary>
    protected abstract string RequestPath { get; }

    /// <summary>Top-level JSON fields removed before the body is kept for the audit trail.</summary>
    protected virtual IReadOnlyCollection<string> SensitiveFields => [];

    /// <summary>Validates a 200 body. Must reject anything doubtful; never repair or guess.</summary>
    protected abstract ParseOutcome Parse(string body);

    public async Task<TimeSourceResult> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Our own timeout, separate from the caller's token, so the two can be told apart below.
        // CancelAfter uses a local timer only to bound the wait (allowed: not a punch time).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        var started = _timer.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RequestPath);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Fail(TimeSourceFailureReason.RateLimited, started, "HTTP 429", 429);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                return Fail(TimeSourceFailureReason.HttpError, started, $"HTTP {status}", status);
            }

            var body = await ReadBodyAsync(response.Content, timeoutCts.Token);
            var roundTripMs = ElapsedMs(started);
            if (body is null)
            {
                return new TimeSourceFailure(Name, TimeSourceFailureReason.InvalidResponse, roundTripMs, $"body larger than {MaxBodyBytes} bytes");
            }

            var parsed = Parse(body);
            if (parsed.Error is not null)
            {
                return new TimeSourceFailure(Name, TimeSourceFailureReason.InvalidResponse, roundTripMs, parsed.Error);
            }

            return new TimeSourceReading(Name, parsed.Instant, parsed.Offset, roundTripMs,
                PayloadSanitizer.Sanitize(body, SensitiveFields), started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up (e.g. client disconnected): not a provider problem
        }
        catch (OperationCanceledException)
        {
            return Fail(TimeSourceFailureReason.Timeout, started, $"no complete response within {_timeout.TotalMilliseconds} ms");
        }
        catch (HttpRequestException ex)
        {
            return Fail(TimeSourceFailureReason.NetworkError, started, ex.HttpRequestError is HttpRequestError.Unknown
                ? ex.Message
                : $"{ex.HttpRequestError}: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail(TimeSourceFailureReason.NetworkError, started, ex.Message);
        }
        catch (DecoderFallbackException)
        {
            return Fail(TimeSourceFailureReason.InvalidResponse, started, "body is not valid UTF-8");
        }
    }

    private TimeSourceFailure Fail(TimeSourceFailureReason reason, long started, string detail, int? status = null) =>
        new(Name, reason, ElapsedMs(started), detail, status);

    private int ElapsedMs(long started) =>
        (int)Math.Min(int.MaxValue, Math.Round(_timer.GetElapsedTime(started).TotalMilliseconds, MidpointRounding.AwayFromZero));

    /// <summary>Reads at most <see cref="MaxBodyBytes"/>; returns null if the body is larger.</summary>
    private static async Task<string?> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxBodyBytes) return null;

        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }
        return total > MaxBodyBytes ? null : StrictUtf8.GetString(buffer, 0, total);
    }

    /// <summary>Result of <see cref="Parse"/>: an instant + offset, or an error message (for logs only).</summary>
    protected readonly record struct ParseOutcome(Instant Instant, Offset Offset, string? Error)
    {
        public static ParseOutcome Ok(Instant instant, Offset offset) => new(instant, offset, null);

        public static ParseOutcome Invalid(string error) => new(default, default, error);
    }
}
