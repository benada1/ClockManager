using System.Text.Json;
using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ClockManager.Tests.Punches;

/// <summary>The punch endpoint is documented in Swagger (Development) with both headers and only <c>intent</c> in the body.</summary>
public sealed class PunchSwaggerTests
{
    [Fact]
    public async Task Swagger_documents_the_punch_endpoint_with_its_headers_and_a_body_of_intent_only()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseNoDatabase();
        });
        using var client = factory.CreateClient();

        var doc = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json")).RootElement;

        var post = doc.GetProperty("paths").GetProperty("/api/punches").GetProperty("post");
        var parameters = post.GetProperty("parameters").EnumerateArray()
            .Select(p => (p.GetProperty("name").GetString(), p.GetProperty("in").GetString()))
            .ToList();
        Assert.Contains(("Idempotency-Key", "header"), parameters);
        Assert.Contains(("X-Dev-Employee", "header"), parameters);
        Assert.Equal(["201", "400", "401", "403", "409", "422", "503"], post.GetProperty("responses").EnumerateObject().Select(r => r.Name));

        // The status endpoint gets the same dev header, and documents 200 / 401 / 403 only (a time problem is never an error there).
        var get = doc.GetProperty("paths").GetProperty("/api/me/status").GetProperty("get");
        var getParameters = get.GetProperty("parameters").EnumerateArray()
            .Select(p => (p.GetProperty("name").GetString(), p.GetProperty("in").GetString()))
            .ToList();
        Assert.Contains(("X-Dev-Employee", "header"), getParameters);
        Assert.DoesNotContain(("Idempotency-Key", "header"), getParameters);
        Assert.Equal(["200", "401", "403"], get.GetProperty("responses").EnumerateObject().Select(r => r.Name));

        var body = doc.GetProperty("components").GetProperty("schemas").GetProperty("PunchRequest");
        Assert.Equal(["intent"], body.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }
}
