using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Infrastructure;

namespace ClockManager.Tests.Common;

public sealed class ProblemDetailsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ProblemJson = "application/problem+json";
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_returns_404_problem_with_code_and_traceId()
    {
        var response = await _client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJson(response);
        Assert.Equal(404, body.GetProperty("status").GetInt32());
        Assert.Equal(ErrorCodes.RequestNotFound, body.GetProperty("code").GetString());
        AssertHasTraceId(body);
    }

    [Fact]
    public async Task Wrong_method_returns_405_problem()
    {
        var response = await _client.DeleteAsync("/api/health");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJson(response);
        Assert.Equal(ErrorCodes.MethodNotAllowed, body.GetProperty("code").GetString());
        AssertHasTraceId(body);
    }

    [Fact]
    public async Task AppException_maps_to_its_status_code_title_detail_and_extensions()
    {
        var response = await _client.GetAsync("/test-only/app-error");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJson(response);
        Assert.Equal(409, body.GetProperty("status").GetInt32());
        Assert.Equal(ErrorCodes.ShiftAlreadyOpen, body.GetProperty("code").GetString());
        Assert.Equal("A shift is already open.", body.GetProperty("title").GetString());
        Assert.Equal("Clock out before clocking in again.", body.GetProperty("detail").GetString());
        Assert.Equal(42, body.GetProperty("openShiftId").GetInt32());
        AssertHasTraceId(body);
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_server_error_without_leaking_the_message()
    {
        var response = await _client.GetAsync("/test-only/unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TestOnlyController.SecretMessage, raw);
        Assert.DoesNotContain(nameof(InvalidOperationException), raw);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(500, body.GetProperty("status").GetInt32());
        Assert.Equal(ErrorCodes.ServerError, body.GetProperty("code").GetString());
        AssertHasTraceId(body);
    }

    [Fact]
    public async Task Model_validation_failure_returns_400_with_request_invalid_code()
    {
        var response = await _client.PostAsJsonAsync("/test-only/validate", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJson(response);
        Assert.Equal(ErrorCodes.RequestInvalid, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("name", out _));
        AssertHasTraceId(body);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static void AssertHasTraceId(JsonElement body) =>
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
}
