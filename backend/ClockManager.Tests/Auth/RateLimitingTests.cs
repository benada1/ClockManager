using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClockManager.Tests.Auth;

/// <summary>429 on login (per IP) and on punches (per employee), with a tiny limit and a 1-second window (the other tests use huge limits).</summary>
[Trait("Category", "Integration")]
public sealed class RateLimitingTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string Password = "correct horse 42";
    private const string IpHeader = "X-Test-Ip";

    [SqlServerFact]
    public async Task Login_over_the_limit_is_429_with_code_and_Retry_After_per_IP_and_works_again_after_the_window()
    {
        var name = await AddEmployeeAsync();
        await using var factory = LimitedFactory(authLimit: 3, punchLimit: 1000);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, name, "10.0.0.1")).StatusCode);

        using var limited = await LoginAsync(client, name, "10.0.0.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.True(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture) >= 1);
        using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("request.rate_limited", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(limited.Headers.Contains("Set-Cookie"));

        using var otherIp = await LoginAsync(client, name, "10.0.0.2");
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);

        await Task.Delay(TimeSpan.FromMilliseconds(1500)); // the window is 1 second
        using var afterWindow = await LoginAsync(client, name, "10.0.0.1");
        Assert.Equal(HttpStatusCode.OK, afterWindow.StatusCode);
    }

    [SqlServerFact]
    public async Task Punches_over_the_limit_are_429_per_employee_and_an_anonymous_call_stays_401()
    {
        var busy = await AddEmployeeAsync();
        var other = await AddEmployeeAsync();
        await using var factory = LimitedFactory(authLimit: 1000, punchLimit: 2);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await PunchAsync(client, busy, "clock-in")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PunchAsync(client, busy, "clock-in")).StatusCode); // counts too

        using var limited = await PunchAsync(client, busy, "clock-in");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.Single(limited.Headers.GetValues("Retry-After"));
        using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("request.rate_limited", body.RootElement.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Created, (await PunchAsync(client, other, "clock-in")).StatusCode);

        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await PunchAsync(client, null, "clock-in")).StatusCode);
    }

    private WebApplicationFactory<Program> LimitedFactory(int authLimit, int punchLimit) =>
        new PunchApiFactory(database, new FakeTrustedClock("2026-01-12T07:00:00Z")).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:AuthPermitLimit"] = authLimit.ToString(CultureInfo.InvariantCulture),
                ["RateLimits:AuthWindowSeconds"] = "1",
                ["RateLimits:PunchPermitLimit"] = punchLimit.ToString(CultureInfo.InvariantCulture),
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new TestIpFilter()));
        });

    /// <summary>The test server has no client address: this sets it from <c>X-Test-Ip</c>.</summary>
    private sealed class TestIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, inner) =>
            {
                if (context.Request.Headers.TryGetValue(IpHeader, out var ip) && IPAddress.TryParse(ip.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }
                return inner(context);
            });
            next(app);
        };
    }

    private async Task<string> AddEmployeeAsync()
    {
        await using var db = database.CreateContext();
        var employee = PersistenceTestData.NewEmployee();
        employee.SetPasswordHash(new PasswordService(new PasswordHasher<Employee>()).Hash(employee, Password));
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Username;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string ip)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { username, password = Password }), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(IpHeader, ip);
        return client.SendAsync(message);
    }

    private static Task<HttpResponseMessage> PunchAsync(HttpClient client, string? user, string intent)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { intent }), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        if (user is not null) message.Headers.TryAddWithoutValidation(PunchApiFactory.TestUserHeader, user);
        return client.SendAsync(message);
    }
}
