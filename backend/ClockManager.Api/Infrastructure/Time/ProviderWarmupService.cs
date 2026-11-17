using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// QA-11: after the host has started, sends ONE request to each time provider, only so the DNS + TCP + TLS setup is
/// already done when the first punch arrives (D-037).
///
/// CLAUDE.md rule 1 / D1: this class must never produce or keep a time. It does not use <c>ITrustedClock</c>,
/// <c>ITimeSource</c>, the server-now cache or the health cache; the response is drained into <see cref="Stream.Null"/>
/// without being read as text, and only an outcome code is logged (never the exception text, like the rest of the
/// time code). Every failure is swallowed: a punch asks the providers itself and fails closed on its own.
/// The local timer below only bounds the wait; it is not a record time.
/// </summary>
internal sealed class ProviderWarmupService(
    IHttpClientFactory clients,
    IOptions<TrustedClockOptions> options,
    ILogger<ProviderWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var warmup = options.Value.Warmup;
        if (!warmup.Enabled) return;

        // Yield first: even if everything below were synchronous, host startup is never held up by this service.
        await Task.Yield();

        var timeout = TimeSpan.FromMilliseconds(warmup.TimeoutMs);
        await Task.WhenAll(
            WarmAsync(TrustedClockServiceCollectionExtensions.TimeNowClientName, TimeNowSource.Path, timeout, stoppingToken),
            WarmAsync(TrustedClockServiceCollectionExtensions.TimeApiIoClientName, TimeApiIoSource.Path, timeout, stoppingToken));
    }

    private async Task WarmAsync(string provider, string path, TimeSpan timeout, CancellationToken stoppingToken)
    {
        string outcome;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(timeout); // bounds the wait only (not a punch time)
            var http = clients.CreateClient(provider);
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // Drain so the connection goes back to the pool for the first punch; the content is never looked at.
            await response.Content.CopyToAsync(Stream.Null, cts.Token);
            outcome = response.IsSuccessStatusCode ? "ok" : $"http_{(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // shutting down
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
        }
        catch (Exception)
        {
            outcome = "failed"; // deliberately no exception text
        }

        if (outcome == "ok") logger.LogInformation("Provider warm-up {Provider}: {Outcome}", provider, outcome);
        else logger.LogWarning("Provider warm-up {Provider}: {Outcome}", provider, outcome);
    }
}
