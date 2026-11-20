using System.Net;
using System.Text;
using ClockManager.Api.Domain;
using ClockManager.Tests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 6: what a web page on another site can make the API do. There is no CORS and no CSRF token (D-049),
/// so the defence is: JSON-only POSTs (a cross-site page cannot send JSON without a preflight, and there is no CORS to
/// approve it), SameSite=Lax cookies, and GET requests that change nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaBrowserTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string Evil = "https://evil.example";

    private static readonly (string Name, string Value)[] CrossSite =
    [
        ("Origin", Evil), ("Referer", Evil + "/attack.html"), ("Sec-Fetch-Site", "cross-site"), ("Sec-Fetch-Mode", "navigate"),
    ];

    private async Task<(int Employees, int Shifts, int Records, int Corrections)> CountsAsync()
    {
        await using var db = database.CreateContext();
        return (await db.Employees.CountAsync(), await db.Shifts.CountAsync(), await db.IdempotencyRecords.CountAsync(), await db.ShiftCorrections.CountAsync());
    }

    [SqlServerFact]
    public async Task No_response_ever_carries_a_CORS_header_not_even_for_a_preflight_or_an_error()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);
        var manager = await AddEmployeeAsync(database, EmployeeRole.Manager);
        var calls = new (HttpMethod Method, string Url, string? User)[]
        {
            (HttpMethod.Options, "/api/punches", null),
            (HttpMethod.Options, "/api/auth/login", null),
            (HttpMethod.Options, "/api/auth/signup", null),
            (HttpMethod.Options, "/api/manager/employees", null),
            (HttpMethod.Options, "/api/manager/shifts/1/corrections", null),
            (HttpMethod.Options, "/api/me/status", person.Username),
            (HttpMethod.Options, "/nowhere", null),
            (HttpMethod.Get, "/api/auth/me", null),
            (HttpMethod.Get, "/api/auth/me", person.Username),
            (HttpMethod.Get, "/api/manager/employees", manager.Username),
            (HttpMethod.Get, "/api/manager/employees", person.Username),
            (HttpMethod.Get, "/nowhere", null),
            (HttpMethod.Post, "/api/auth/login", null),
            (HttpMethod.Post, "/api/auth/signup", null),
            (HttpMethod.Put, "/api/punches", person.Username),
            (HttpMethod.Delete, "/api/manager/shifts/1", manager.Username),
        };

        foreach (var (method, url, user) in calls)
        {
            var headers = CrossSite.Concat(
            [
                ("Access-Control-Request-Method", "POST"), ("Access-Control-Request-Headers", "content-type,idempotency-key"),
            ]).ToList();
            if (user is not null) headers.Add((Punches.PunchApiFactory.TestUserHeader, user));
            var content = method == HttpMethod.Post ? new StringContent("{}", Encoding.UTF8, "application/json") : null;

            var reply = await SendAsync(client, method, url, content, headers.ToArray());

            var cors = reply.Headers.Keys.Where(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(cors.Count == 0, $"{method} {url}: {string.Join(", ", cors)}");
            Assert.True((int)reply.Status < 500, $"{method} {url}: {reply.Status}");
            if (method == HttpMethod.Options) Assert.False(reply.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices, $"{url}: the preflight is answered {reply.Status}");
        }
    }

    [SqlServerFact]
    public async Task A_cross_site_form_or_text_post_with_a_valid_cookie_changes_nothing()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);
        var manager = await AddEmployeeAsync(database, EmployeeRole.Manager);
        var shiftId = await ManagerSeedShiftAsync(person.Id);
        var before = await CountsAsync();
        var json = """{"intent":"clock-in"}""";
        var correction = """{"field":"clockOut","newTime":"2026-01-12T16:00:00+01:00","reason":"Left at four","rowVersion":"AAAAAAAAB9E="}""";
        var types = new[] { "text/plain", "text/plain;charset=UTF-8", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x", "application/xml", "text/html" };

        foreach (var type in types)
        {
            var cases = new (string Label, string Url, string Body, string User)[]
            {
                ("punch", "/api/punches", json, person.Username),
                ("signup", "/api/auth/signup", $$"""{"username":"{{NewName()}}","displayName":"X","password":"{{Password}}"}""", person.Username),
                ("login", "/api/auth/login", $$"""{"username":"{{person.Username}}","password":"{{Password}}"}""", person.Username),
                ("correction", $"/api/manager/shifts/{shiftId}/corrections", correction, manager.Username),
            };
            foreach (var (label, url, body, user) in cases)
            {
                var reply = await PostRawAsync(client, url, body, type,
                    [.. CrossSite, (Punches.PunchApiFactory.TestUserHeader, user), ("Idempotency-Key", Guid.NewGuid().ToString())]);
                Assert.True(HttpStatusCode.UnsupportedMediaType == reply.Status, $"{label} as {type}: {reply.Status} {reply.Text}");
                Assert.Equal("request.unsupported_media_type", reply.Code);
                Assert.Empty(reply.SetCookies);
            }
        }

        Assert.Equal(before, await CountsAsync());
    }

    /// <summary>
    /// The server does not look at Origin or Referer: a JSON POST from "another site" is handled like any other. The browser is
    /// what stops a real attack (no CORS approval = no cross-site JSON, SameSite=Lax = no cookie on a cross-site POST). Pinned so
    /// that adding CORS later (a SPA on another origin) has to revisit D-049.
    /// </summary>
    [SqlServerFact]
    public async Task The_server_itself_does_not_check_Origin_so_the_browser_defences_are_the_only_ones_D049()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);

        var reply = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password }, CrossSite);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Contains("samesite=lax", reply.SetCookies.Single(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Logout has no body and no Consumes, so a cross-site form can POST it: the user is signed out (a nuisance, not a breach;
    /// the cross-site request carries no cookie because of SameSite=Lax, but the Set-Cookie answer clears the browser's cookie).
    /// </summary>
    [SqlServerFact]
    public async Task Logout_can_be_triggered_by_any_POST_a_forced_logout_known_limit()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var before = await CountsAsync();

        var form = await PostRawAsync(client, "/api/auth/logout", "x=1", "application/x-www-form-urlencoded", CrossSite);
        var text = await PostRawAsync(client, "/api/auth/logout", "x", "text/plain", CrossSite);

        Assert.Equal(HttpStatusCode.NoContent, form.Status);
        Assert.Equal(HttpStatusCode.NoContent, text.Status);
        Assert.Contains("expires=Thu, 01 Jan 1970", form.SetCookies.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task GET_never_changes_data_HEAD_and_OPTIONS_never_do_and_logout_login_signup_are_not_reachable_by_GET()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);
        var manager = await AddEmployeeAsync(database, EmployeeRole.Manager);
        var shiftId = await ManagerSeedShiftAsync(person.Id);

        // Every GET route the app maps must be in this list: a new one has to be added here, i.e. looked at for side effects.
        var mapped = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is { } m && m.HttpMethods.Contains("GET"))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Where(p => !p.StartsWith("/swagger", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        var covered = new[]
        {
            "/api/auth/me", "/api/me/status", "/api/me/shifts", "/api/manager/employees", "/api/manager/anomalies",
            "/api/manager/employees/{id:int}/shifts", "/api/manager/shifts/{id:long}",
            "/api/Health", // static {"status":"ok"}
            "/health/time", // reads the providers, writes nothing (TimeHealthEndpointTests); not called here: it would leave the test host
            "/api/health/time", // the same alias
        };
        Assert.True(mapped.All(m => covered.Contains(m)), "a GET route is not in the side-effect test: " + string.Join(", ", mapped.Except(covered)));

        var urls = new[]
        {
            "/api/auth/me", "/api/me/status", "/api/me/shifts", "/api/me/shifts?from=2026-01-01&to=2026-01-31",
            "/api/manager/employees", "/api/manager/anomalies", $"/api/manager/employees/{person.Id}/shifts", $"/api/manager/shifts/{shiftId}",
        };
        var before = await CountsAsync();

        foreach (var user in new[] { null, person.Username, manager.Username })
        {
            foreach (var url in urls)
            {
                var headers = user is null ? [] : new[] { (Punches.PunchApiFactory.TestUserHeader, user) };
                foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Options })
                {
                    var reply = await SendAsync(client, method, url, null, headers);
                    Assert.True((int)reply.Status < 500, $"{method} {url} as {user}: {reply.Status}");
                }
            }
        }
        Assert.Equal(before, await CountsAsync());

        // Anything that changes state is POST only: the same URLs by GET or HEAD are 405, never a quiet success.
        foreach (var url in new[] { "/api/auth/login", "/api/auth/signup", "/api/auth/logout", "/api/punches", $"/api/manager/shifts/{shiftId}/corrections" })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            {
                var reply = await SendAsync(client, method, url, null, (Punches.PunchApiFactory.TestUserHeader, manager.Username));
                Assert.True(HttpStatusCode.MethodNotAllowed == reply.Status, $"{method} {url}: {reply.Status}");
                Assert.Empty(reply.SetCookies);
            }
        }
        Assert.Equal(before, await CountsAsync());
    }

    [SqlServerFact]
    public async Task The_cookie_attributes_are_the_same_on_signup_login_and_logout_so_Lax_is_consistent()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);

        var signup = await PostJsonAsync(client, "/api/auth/signup", new { username = NewName(), displayName = "Lax", password = Password });
        var login = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password });
        var logout = await PostRawAsync(client, "/api/auth/logout", "", null);

        foreach (var reply in new[] { signup, login, logout })
        {
            var attributes = reply.SetCookies.Single().Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
            Assert.Contains("samesite=lax", attributes);
            Assert.Contains("httponly", attributes);
            Assert.Contains("path=/", attributes);
        }
    }

    /// <summary>
    /// Forwarded headers are not trusted (no UseForwardedHeaders), so behind a TLS-terminating proxy the cookie loses its Secure
    /// flag until forwarded-header handling is configured (known limit, D-049). Pinned: a client claiming https changes nothing.
    /// </summary>
    [SqlServerFact]
    public async Task A_claimed_X_Forwarded_Proto_https_does_not_make_the_cookie_Secure_known_limit_D049()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);

        var reply = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password },
            ("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "app.example"), ("Forwarded", "proto=https"));

        Assert.DoesNotContain("secure", reply.SetCookies.Single(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> ManagerSeedShiftAsync(int employeeId)
    {
        var kit = new Manager.ManagerTestKit(database);
        return await kit.SaveClosedAsync(employeeId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
    }
}
