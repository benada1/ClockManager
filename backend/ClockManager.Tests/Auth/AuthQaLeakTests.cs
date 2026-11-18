using System.Net;
using System.Text.RegularExpressions;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Infrastructure;
using ClockManager.Tests.Manager;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 7 (with a database): after a full session (signup, login, failed login, tampered and header
/// tokens, a rate-limited login, a manager correction) no password, hash, token or signing key may be in any response body, any
/// header other than the one Set-Cookie that is meant to carry the token, or any log line, with framework logging turned up to
/// Information so the JWT handler's own messages are captured too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaLeakTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private static readonly Regex JwtShape = new(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", RegexOptions.CultureInvariant);

    [SqlServerFact]
    public async Task No_secret_reaches_a_response_a_header_or_a_log_during_a_full_session()
    {
        var clock = new FakeTrustedClock("2026-01-13T07:00:00Z");
        var inner = new PunchApiFactory(database, clock);
        await using var factory = inner.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Information",
                ["RateLimits:AuthPermitLimit"] = "6", // the seventh login/signup is the rate-limited one
            })));
        using var client = factory.CreateClient();
        using var noCookies = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        var kit = new ManagerTestKit(database);
        var (_, managerName) = await kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (employeeId, _) = await kit.AddEmployeeAsync();
        var shiftId = await kit.SaveClosedAsync(employeeId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");

        var name = NewName();
        var secret = "Sup3r-secret-" + Guid.NewGuid().ToString("N");
        var wrong = "Wr0ng-secret-" + Guid.NewGuid().ToString("N");
        var weak = "short"; // rejected by validation: must not be echoed either
        var replies = new List<(string Label, Reply Reply)>();
        async Task Add(string label, Task<Reply> call) => replies.Add((label, await call));

        await Add("signup", PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Ann Example", password = secret }));
        var issued = CookieValue(replies[0].Reply.SetCookies.Single());
        await Add("signup again", PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Ann Again", password = secret }));
        await Add("signup too short", PostJsonAsync(client, "/api/auth/signup", new { username = NewName(), displayName = "X", password = weak }));
        await Add("failed login", PostJsonAsync(client, "/api/auth/login", new { username = name, password = wrong }));
        await Add("good login", PostJsonAsync(client, "/api/auth/login", new { username = name, password = secret }));
        issued += "\n" + CookieValue(replies[^1].Reply.SetCookies.Single());
        await Add("me", GetAsync(client, "/api/auth/me"));
        await Add("rate limited login", PostJsonAsync(client, "/api/auth/login", new { username = name, password = wrong }));
        await Add("rate limited login 2", PostJsonAsync(client, "/api/auth/login", new { username = name, password = secret }));
        Assert.Contains(replies, r => r.Reply.Status == HttpStatusCode.TooManyRequests);

        // Tokens that fail: a tampered one and one sent in the Authorization header.
        var parts = issued.Split('\n')[0].Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{(parts[2][0] == 'A' ? 'B' : 'A')}{parts[2][1..]}";
        await Add("tampered cookie", GetAsync(noCookies, "/api/auth/me", ("Cookie", $"{CookieName}={tampered}")));
        await Add("bearer header", GetAsync(noCookies, "/api/auth/me", ("Authorization", "Bearer " + issued.Split('\n')[0])));

        // A manager correction (the manager gets a cookie from the test header).
        var rowVersion = await kit.RowVersionAsync(shiftId);
        await Add("correction", SendAsync(noCookies, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections",
            new StringContent($$"""{"field":"clockOut","newTime":"2026-01-12T16:00:00+01:00","reason":"Left at four","rowVersion":"{{rowVersion}}"}""",
                System.Text.Encoding.UTF8, "application/json"), (PunchApiFactory.TestUserHeader, managerName)));
        Assert.Equal(HttpStatusCode.Created, replies[^1].Reply.Status);
        await Add("failed correction", SendAsync(noCookies, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections",
            new StringContent("""{"field":"clockOut","newTime":"bad","reason":"x","rowVersion":"AAAA"}""", System.Text.Encoding.UTF8, "application/json"),
            (PunchApiFactory.TestUserHeader, managerName)));

        var hash = (await LoadAsync(database, name)).PasswordHash!;
        var secrets = new Dictionary<string, string>
        {
            ["the password"] = secret,
            ["the wrong password"] = wrong,
            ["the rejected short password"] = weak,
            ["the stored hash"] = hash,
            ["the signing key"] = TestAuthKey.Value,
            ["the tampered token"] = tampered,
            ["an issued token (signature part)"] = issued.Split('\n')[0].Split('.')[2],
            ["a second issued token (signature part)"] = issued.Split('\n')[1].Split('.')[2],
        };

        var logs = string.Join("\n", inner.Logs.Select(l => $"{l.Category}: {l.Message} {l.Exception}"));
        Assert.Contains("signed up", logs, StringComparison.Ordinal); // the capture really works
        Assert.Contains("logged in", logs, StringComparison.Ordinal);
        Assert.Contains(inner.Logs, l => l.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)); // and framework categories were on

        foreach (var (label, reply) in replies)
        {
            var headers = string.Join("\n", reply.Headers.Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Select(h => h.Key + ": " + h.Value));
            foreach (var (what, value) in secrets)
            {
                Assert.False(reply.Text.Contains(value, StringComparison.Ordinal), $"{label}: the body contains {what}");
                Assert.False(headers.Contains(value, StringComparison.Ordinal), $"{label}: a header contains {what}");
            }
            Assert.False(JwtShape.IsMatch(reply.Text), $"{label}: the body contains something shaped like a JWT: {reply.Text}");
            Assert.False(JwtShape.IsMatch(headers), $"{label}: a header contains something shaped like a JWT");
            Assert.False(reply.Text.Contains("passwordHash", StringComparison.OrdinalIgnoreCase), $"{label}: names a password hash");
            if (label is not ("signup" or "good login")) Assert.Empty(reply.SetCookies); // a cookie only where one is issued
        }
        foreach (var (what, value) in secrets)
        {
            Assert.False(logs.Contains(value, StringComparison.Ordinal), $"a log line contains {what}");
        }
        Assert.False(JwtShape.IsMatch(logs), "a log line contains something shaped like a JWT");
        Assert.DoesNotContain(inner.Logs, l => l.Message.Contains(name, StringComparison.Ordinal)); // no user names in logs either
    }
}
