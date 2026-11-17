using System.Net;
using System.Text.Json;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Infrastructure;

namespace ClockManager.Tests.Time;

/// <summary>TrustedTimeException → 503 ProblemDetails with a stable code and per-provider outcomes (plan 01 §4.4).</summary>
public sealed class TrustedTimeExceptionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Maps_to_503_problem_with_code_and_providers()
    {
        var response = await factory.CreateClient().GetAsync("/test-only/time-error");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal(ErrorCodes.TimeUnavailable, body.GetProperty("code").GetString());
        Assert.Equal("Trusted time is unavailable.", body.GetProperty("title").GetString());
        Assert.Contains("Nothing was recorded", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        var providers = body.GetProperty("providers").EnumerateArray()
            .Select(p => (p.GetProperty("name").GetString(), p.GetProperty("outcome").GetString()))
            .ToList();
        Assert.Equal([("time.now", "timeout"), ("timeapi.io", "rate_limited")], providers);
    }

    [Theory]
    [InlineData(ErrorCodes.TimeUnavailable)]
    [InlineData(ErrorCodes.TimeInvalidResponse)]
    [InlineData(ErrorCodes.TimeProvidersDisagree)]
    public void Is_always_503_for_every_time_code(string code)
    {
        var ex = new TrustedTimeException(code, [new ProviderOutcome("time.now", "ok")]);

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(code, ex.Code);
        Assert.Same(ex.Providers, ex.Extensions[TrustedTimeException.ProvidersKey]);
    }

    [Theory]
    [InlineData(ErrorCodes.ShiftAlreadyOpen)]
    [InlineData(ErrorCodes.TimeWentBackwards)] // a business-rule rejection (Phase 3), not "no trusted time"
    [InlineData("time.whatever")]
    public void Rejects_codes_that_are_not_trusted_time_failures(string code)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrustedTimeException(code, []));
    }
}
