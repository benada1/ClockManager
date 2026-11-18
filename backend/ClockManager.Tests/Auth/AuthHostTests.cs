using System.Net;
using System.Text.Json;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Auth;

/// <summary>Auth tests that need no database: startup rules for the signing key, the 403 shape, and password checks.</summary>
public sealed class AuthHostTests
{
    private static WebApplicationFactory<Program> Host(string environment, string? signingKey)
    {
        var factory = new WebApplicationFactory<Program>();
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseNoDatabase();
            // An empty value in memory beats the test-wide environment variable: "no key set".
            if (signingKey is not null) builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:SigningKey"] = signingKey }));
        });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_host_outside_Development_refuses_to_start_without_a_signing_key(string environment)
    {
        using var factory = Host(environment, signingKey: "");

        var failure = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(failure);
        Assert.Contains("Auth:SigningKey", string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_signing_key_shorter_than_32_characters_is_refused_even_in_Development()
    {
        using var factory = Host("Development", signingKey: "too-short");

        var failure = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(failure);
        Assert.Contains("at least 32", string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Development_without_a_key_makes_a_random_one_per_run()
    {
        using var first = Host("Development", signingKey: "");
        using var second = Host("Development", signingKey: "");

        var a = first.Services.GetRequiredService<IOptions<AuthOptions>>().Value.SigningKey;
        var b = second.Services.GetRequiredService<IOptions<AuthOptions>>().Value.SigningKey;

        Assert.True(a!.Length >= AuthOptions.MinSigningKeyLength);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task A_role_failure_is_403_auth_forbidden_as_problem_json()
    {
        await using var factory = new ApiFactory(); // has the test-only /test-only/managers-only endpoint
        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        async Task<HttpResponseMessage> CallAsync(string role)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/test-only/managers-only");
            request.Headers.TryAddWithoutValidation("Cookie", "clockmanager_auth=" + TestTokens.Create(options, 1, role, TestTokens.Past, TestTokens.Future));
            return await client.SendAsync(request);
        }

        using var employee = await CallAsync("Employee");
        using var manager = await CallAsync("Manager");

        Assert.Equal(HttpStatusCode.Forbidden, employee.StatusCode);
        Assert.Equal("application/problem+json", employee.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ErrorCodes.Forbidden, JsonDocument.Parse(await employee.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
    }

    [Fact]
    public void PasswordService_accepts_only_a_real_user_with_a_hash_and_the_right_password()
    {
        var passwords = new PasswordService(new PasswordHasher<Employee>());
        var withHash = new Employee("ann", "Ann", EmployeeRole.Employee);
        withHash.SetPasswordHash(passwords.Hash(withHash, "right password"));
        var withoutHash = new Employee("bob", "Bob", EmployeeRole.Employee);

        Assert.True(passwords.Verify(withHash, "right password"));
        Assert.False(passwords.Verify(withHash, "wrong password"));
        Assert.False(passwords.Verify(withoutHash, "right password"));
        Assert.False(passwords.Verify(null, "right password"));
        Assert.NotEqual("right password", withHash.PasswordHash);
    }
}
