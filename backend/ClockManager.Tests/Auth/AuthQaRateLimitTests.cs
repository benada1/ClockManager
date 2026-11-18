using System.Globalization;
using System.Net;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 5: the rate limiter. Which header decides the partition, the shared login/signup budget, the 429
/// answer, a forged token that must not spend someone else's budget, and a burst of parallel requests. The window is 60 s, so
/// a test finishes long before the budget refills; the limit is the only thing that varies.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaRateLimitTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string IpHeader = "X-Test-Ip";

    private WebApplicationFactory<Program> Host(int authLimit, int punchLimit = 1000, FakeTrustedClock? clock = null) =>
        new PunchApiFactory(database, clock ?? new FakeTrustedClock(StartInstant)).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimits:AuthPermitLimit"] = authLimit.ToString(CultureInfo.InvariantCulture),
                ["RateLimits:AuthWindowSeconds"] = "60",
                ["RateLimits:PunchPermitLimit"] = punchLimit.ToString(CultureInfo.InvariantCulture),
                ["RateLimits:PunchWindowSeconds"] = "60",
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new TestIpFilter()));
        });

    /// <summary>The test server has no client address: this sets it from <c>X-Test-Ip</c>, like Kestrel would from the socket.</summary>
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

    private static Task<Reply> WrongLoginAsync(HttpClient client, string? ip, params (string, string)[] extra)
    {
        var headers = ip is null ? extra : [(IpHeader, ip), .. extra];
        return PostJsonAsync(client, "/api/auth/login", new { username = "nobody-" + NewName(), password = "not a password" }, headers);
    }

    [SqlServerFact]
    public async Task Forwarding_headers_cannot_pick_the_partition_the_connection_address_does()
    {
        await using var factory = Host(authLimit: 3);
        using var client = factory.CreateClient();

        // The socket says 10.1.1.1 every time; every request claims to come from somewhere else.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var reply = await WrongLoginAsync(client, "10.1.1.1",
                ("X-Forwarded-For", $"203.0.113.{i + 1}, 198.51.100.{i + 1}"),
                ("X-Real-IP", $"192.0.2.{i + 1}"),
                ("Forwarded", $"for=192.0.2.{i + 50};proto=https"),
                ("X-Forwarded-Host", $"spoof{i}.example"),
                ("X-Client-IP", $"192.0.2.{i + 100}"),
                ("CF-Connecting-IP", $"192.0.2.{i + 150}"));
            statuses.Add(reply.Status);
        }
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);

        // A different socket address has its own budget (so the partition really is the address) ...
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(client, "10.1.1.2")).Status);

        // ... and with no address at all (every proxy-less test host, or a unix socket) everybody shares one bucket, whatever they claim.
        var unknown = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) unknown.Add((await WrongLoginAsync(client, null, ("X-Forwarded-For", $"203.0.113.{i + 1}"))).Status);
        Assert.Equal(2, unknown.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [SqlServerFact]
    public async Task Login_and_signup_spend_one_shared_budget_and_failed_requests_count_but_logout_me_and_anonymous_punches_do_not()
    {
        await using var factory = Host(authLimit: 4);
        using var client = factory.CreateClient();
        const string ip = "10.2.2.2";

        var steps = new (string Label, Func<Task<Reply>> Call, HttpStatusCode Expected)[]
        {
            ("wrong login", () => WrongLoginAsync(client, ip), HttpStatusCode.Unauthorized),
            ("invalid signup", () => PostJsonAsync(client, "/api/auth/signup", new { username = "x", displayName = "x", password = "short" }, (IpHeader, ip)), HttpStatusCode.BadRequest),
            ("good signup", () => PostJsonAsync(client, "/api/auth/signup", new { username = NewName(), displayName = "Ok", password = Password }, (IpHeader, ip)), HttpStatusCode.Created),
            ("wrong login again", () => WrongLoginAsync(client, ip), HttpStatusCode.Unauthorized),
            ("signup over the limit", () => PostJsonAsync(client, "/api/auth/signup", new { username = NewName(), displayName = "No", password = Password }, (IpHeader, ip)), HttpStatusCode.TooManyRequests),
            ("login over the limit", () => WrongLoginAsync(client, ip), HttpStatusCode.TooManyRequests),
        };
        foreach (var (label, call, expected) in steps)
        {
            var reply = await call();
            Assert.True(expected == reply.Status, $"{label}: {reply.Status} {reply.Text}");
        }

        // Not limited: logout, me, and an anonymous punch (401, counted nowhere).
        Assert.Equal(HttpStatusCode.NoContent, (await PostRawAsync(client, "/api/auth/logout", "", null, (IpHeader, ip))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/api/auth/me", (IpHeader, ip))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", "application/json", (IpHeader, ip), ("Idempotency-Key", Guid.NewGuid().ToString()))).Status);
    }

    [SqlServerFact]
    public async Task The_429_answer_is_one_small_problem_json_with_a_whole_number_Retry_After_and_nothing_about_the_caller()
    {
        await using var factory = Host(authLimit: 1);
        using var client = factory.CreateClient();
        const string ip = "198.51.100.77";
        await WrongLoginAsync(client, ip);

        var limited = await WrongLoginAsync(client, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("application/problem+json", limited.MediaType);
        Assert.Equal("request.rate_limited", limited.Code);
        Assert.Equal(429, limited.Json.GetProperty("status").GetInt32());
        var keys = limited.Json.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var required in new[] { "title", "status", "detail", "code", "traceId" }) Assert.Contains(required, keys);
        Assert.Subset(new HashSet<string> { "type", "title", "status", "detail", "instance", "code", "traceId" }, keys.ToHashSet()); // nothing else, e.g. no ip, no limit, no window
        var retry = int.Parse(Assert.Single(limited.Headers["Retry-After"].Split(',')), NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.InRange(retry, 1, 60);
        Assert.Empty(limited.SetCookies);
        Assert.DoesNotContain(ip, limited.Text, StringComparison.Ordinal);
        AssertNoLeak(limited.Text, "429");
    }

    /// <summary>
    /// The partition key is the full address text. An IPv6 client usually owns a whole /64 (2^64 addresses), so with the
    /// per-IP limit as the only brute-force defence (no per-account lockout, D-054) the budget can be multiplied at will.
    /// </summary>
    [SqlServerFact(Skip = "QA-46 open: the login/signup limiter partitions on the full IP text, so one IPv6 /64 (or any rotating-address client) gets a fresh budget per address; expected one shared budget per /64.")]
    [Trait("Status", "KnownBug")]
    public async Task QA46_addresses_in_one_IPv6_64_share_one_login_budget()
    {
        await using var factory = Host(authLimit: 3);
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 12; i++) statuses.Add((await WrongLoginAsync(client, $"2001:db8:abcd:12::{i:x}")).Status);

        Assert.Equal(9, statuses.Count(s => s == HttpStatusCode.TooManyRequests)); // 3 pass, 9 limited
    }

    /// <summary>Today's behaviour of the case above: every address in the /64 starts a new budget.</summary>
    [SqlServerFact]
    public async Task Today_every_address_in_an_IPv6_64_has_its_own_budget()
    {
        await using var factory = Host(authLimit: 3);
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 12; i++) statuses.Add((await WrongLoginAsync(client, $"2001:db8:abcd:12::{i:x}")).Status);

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses);
    }

    [SqlServerFact]
    public async Task A_forged_token_naming_a_victim_cannot_spend_the_victims_punch_budget()
    {
        var clock = new FakeTrustedClock(StartInstant);
        await using var factory = Host(authLimit: 1000, punchLimit: 3, clock);
        using var client = factory.CreateClient();
        var victim = await AddEmployeeAsync(database);
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClockManager.Api.Infrastructure.Identity.AuthOptions>>().Value;
        var forged = Infrastructure.TestTokens.Create(options, victim.Id, "Employee", Infrastructure.TestTokens.Past, Infrastructure.TestTokens.Future,
            signingKey: "another-key-another-key-another-key-0000");

        for (var i = 0; i < 10; i++)
        {
            var attack = await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", "application/json",
                ("Cookie", $"{CookieName}={forged}"), ("Idempotency-Key", Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.Unauthorized, attack.Status); // never 429: the limiter never saw a victim
        }

        var own = await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", "application/json",
            (PunchApiFactory.TestUserHeader, victim.Username), ("Idempotency-Key", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, own.Status);
    }

    [SqlServerFact]
    public async Task The_punch_budget_belongs_to_the_employee_not_to_the_address()
    {
        await using var factory = Host(authLimit: 1000, punchLimit: 2);
        using var client = factory.CreateClient();
        var busy = await AddEmployeeAsync(database);
        var calm = await AddEmployeeAsync(database);

        async Task<HttpStatusCode> PunchAsync(Employee who, string ip) =>
            (await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", "application/json",
                (PunchApiFactory.TestUserHeader, who.Username), (IpHeader, ip), ("Idempotency-Key", Guid.NewGuid().ToString()))).Status;

        Assert.Equal(HttpStatusCode.Created, await PunchAsync(busy, "10.3.3.1"));
        Assert.Equal(HttpStatusCode.Conflict, await PunchAsync(busy, "10.3.3.2")); // another address, the same budget
        Assert.Equal(HttpStatusCode.TooManyRequests, await PunchAsync(busy, "10.3.3.3"));
        Assert.Equal(HttpStatusCode.Created, await PunchAsync(calm, "10.3.3.1")); // same address as busy's first call, own budget
    }

    [SqlServerFact]
    public async Task Limiter_state_never_leaks_from_one_host_to_another()
    {
        await using var first = Host(authLimit: 1);
        await using var second = Host(authLimit: 1);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        const string ip = "10.4.4.4";

        await WrongLoginAsync(a, ip);
        var limitedHere = await WrongLoginAsync(a, ip);
        var freshElsewhere = await WrongLoginAsync(b, ip);

        Assert.Equal(HttpStatusCode.TooManyRequests, limitedHere.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, freshElsewhere.Status);
    }

    [SqlServerFact]
    public async Task A_parallel_burst_passes_exactly_the_limit_and_the_rest_get_429_never_a_500()
    {
        await using var factory = Host(authLimit: 5);
        using var client = factory.CreateClient();

        var replies = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => WrongLoginAsync(client, "10.5.5.5"))))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(5, replies.Count(r => r.Status == HttpStatusCode.Unauthorized));
        Assert.Equal(35, replies.Count(r => r.Status == HttpStatusCode.TooManyRequests));
        Assert.All(replies, r => Assert.True((int)r.Status < 500, r.Text));
    }
}
