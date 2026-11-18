using System.Net;
using System.Text.Json;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 7 (no database): which environments expose Swagger, what Swagger says, and which Auth settings the
/// host refuses at startup. Complements <c>AuthHostTests</c> (missing key outside Development, key of 9 characters).
/// </summary>
public sealed class AuthQaHostTests
{
    private static WebApplicationFactory<Program> Host(string environment, Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseNoDatabase();
            if (settings is not null) builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
        });

    private static async Task<(HttpStatusCode Status, string Text)> GetAsync(HttpClient client, string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(url, cts.Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Swagger_and_the_Time_lab_are_not_reachable_outside_Development(string environment)
    {
        await using var factory = Host(environment);
        using var client = factory.CreateClient();

        foreach (var url in new[]
        {
            "/swagger", "/swagger/", "/swagger/index.html", "/swagger/v1/swagger.json", "/swagger/v1/swagger.yaml",
            "/swagger/swagger-ui.css", "/openapi/v1.json", "/api/dev/time-lab",
        })
        {
            var (status, text) = await GetAsync(client, url);
            Assert.True(HttpStatusCode.NotFound == status, $"{environment} {url}: {status}");
            Assert.DoesNotContain("\"paths\"", text, StringComparison.Ordinal); // the 404 body echoes the path, never a document
            Assert.DoesNotContain("<html", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Development_Swagger_lists_the_API_and_exposes_no_secret_no_hash_field_and_no_connection_string()
    {
        await using var factory = Host("Development", new() { ["Auth:SigningKey"] = TestAuthKey.Value, ["Seed:ManagerPassword"] = "seed-password-for-qa-1" });
        using var client = factory.CreateClient();

        var (status, text) = await GetAsync(client, "/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, status);
        foreach (var secret in new[] { TestAuthKey.Value, "seed-password-for-qa-1", "Integrated Security", "Server=", "SQLEXPRESS", "no-database-in-this-test" })
        {
            Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"Swagger contains '{secret}'");
        }
        using var doc = JsonDocument.Parse(text);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var properties = schemas.EnumerateObject()
            .SelectMany(s => s.Value.TryGetProperty("properties", out var p) ? p.EnumerateObject().Select(x => (Schema: s.Name, x.Name)) : [])
            .ToList();
        Assert.DoesNotContain(properties, p => p.Name.Contains("hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("signingKey", StringComparison.OrdinalIgnoreCase));
        // The password only appears where a client SENDS it.
        var withPassword = properties.Where(p => string.Equals(p.Name, "password", StringComparison.OrdinalIgnoreCase)).Select(p => p.Schema).Order().ToList();
        Assert.Equal(["LoginRequest", "SignupRequest"], withPassword);
        // The security scheme names the cookie, nothing else.
        var scheme = doc.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("cookieAuth");
        Assert.Equal("clockmanager_auth", scheme.GetProperty("name").GetString());
        Assert.Equal("cookie", scheme.GetProperty("in").GetString());
    }

    private static List<long?> BodyLimits(WebApplicationFactory<Program> factory, params string[] routes)
    {
        _ = factory.CreateClient();
        var kestrelDefault = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>().Value.Limits.MaxRequestBodySize;
        return factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => routes.Contains(e.RoutePattern.RawText))
            .Select(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>() is { } m ? m.MaxRequestBodySize : kestrelDefault)
            .ToList();
    }

    /// <summary>
    /// Signup, login, punch and correction read a few short strings, so each caps the body at 16 KB (QA-48) instead of the
    /// framework default (30 MB), which the JSON reader would buffer and parse before validation says no. TestServer cannot enforce
    /// Kestrel's limit, so this checks the endpoint metadata that Kestrel reads.
    /// </summary>
    [Fact]
    public void QA48_body_endpoints_cap_the_request_at_16_KB()
    {
        using var factory = Host("Development");

        var limits = BodyLimits(factory, "api/auth/signup", "api/auth/login", "api/punches", "api/manager/shifts/{id:long}/corrections");

        Assert.Equal(4, limits.Count);
        Assert.All(limits, l => Assert.Equal(16384, l));
    }

    [Fact]
    public void A_signing_key_of_31_characters_is_refused_and_32_is_accepted_in_every_environment()
    {
        foreach (var environment in new[] { "Production", "Development" })
        {
            using var shortKey = Host(environment, new() { ["Auth:SigningKey"] = new string('k', 31) });
            using var okKey = Host(environment, new() { ["Auth:SigningKey"] = new string('k', 32) });

            var refused = HostStartup.FindStartupValidationFailure(shortKey);
            var accepted = HostStartup.FindStartupValidationFailure(okKey);

            Assert.NotNull(refused);
            Assert.Contains("at least 32", string.Join(" ", refused.Failures), StringComparison.Ordinal);
            Assert.Null(accepted);
        }
    }

    [Theory]
    [InlineData(40)]
    public void A_signing_key_of_only_spaces_counts_as_missing_outside_Development(int spaces)
    {
        using var factory = Host("Production", new() { ["Auth:SigningKey"] = new string(' ', spaces) });

        var failure = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(failure);
        Assert.Contains("Auth:SigningKey is missing", string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_startup_error_names_the_setting_but_never_prints_the_key()
    {
        const string weak = "weak-key-QA-0123456789"; // 22 characters
        using var factory = Host("Production", new() { ["Auth:SigningKey"] = weak });

        var failure = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(failure);
        Assert.DoesNotContain(weak, string.Join(" ", failure.Failures) + failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_lifetime_must_be_1_to_24_hours_and_the_cookie_name_a_plain_token()
    {
        foreach (var (key, value, valid) in new (string, string, bool)[]
        {
            ("Auth:TokenLifetimeHours", "0", false), ("Auth:TokenLifetimeHours", "25", false), ("Auth:TokenLifetimeHours", "-1", false),
            ("Auth:TokenLifetimeHours", "1", true), ("Auth:TokenLifetimeHours", "24", true),
            ("Auth:CookieName", "bad name", false), ("Auth:CookieName", "a;b", false), ("Auth:CookieName", "a=b", false), ("Auth:CookieName", "", false),
            ("Auth:CookieName", new string('c', 65), false), ("Auth:CookieName", "x\r\nSet-Cookie: y=1", false),
            ("Auth:CookieName", "my_cookie-1", true), ("Auth:CookieName", new string('c', 64), true),
        })
        {
            using var factory = Host("Production", new() { ["Auth:SigningKey"] = TestAuthKey.Value, [key] = value });

            var failure = HostStartup.FindStartupValidationFailure(factory);

            Assert.True(valid == (failure is null), $"{key}='{value}': {(failure is null ? "accepted" : "refused")}");
        }
    }

    [Fact]
    public void The_cookie_name_in_use_is_the_configured_one_and_the_default_is_not_a_prefixed_secure_name()
    {
        using var factory = Host("Development", new() { ["Auth:CookieName"] = "qa_session" });

        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

        Assert.Equal("qa_session", options.CookieName);
        Assert.Equal("clockmanager_auth", new AuthOptions().CookieName);
    }
}
