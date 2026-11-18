using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Tests.Infrastructure;
using ClockManager.Tests.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 3: the login token and its cookie. Forged, odd and stale tokens, where a token may travel,
/// the cookie's flags and lifetime, logout, and a deactivated employee with a live token. Dates are literals, never the clock.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaTokenTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const long PastUnix = 1577836800; // 2020-01-01
    private const long FutureUnix = 4070908800; // 2099-01-01

    private static string Mint(AuthOptions options, string? sub, Action<SecurityTokenDescriptor>? tweak = null, string role = "Employee")
    {
        var claims = new List<Claim>();
        if (sub is not null) claims.Add(new Claim("sub", sub));
        claims.Add(new Claim("role", role));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = JwtSettings.Issuer,
            Audience = JwtSettings.Audience,
            NotBefore = TestTokens.Past,
            Expires = TestTokens.Future,
            SigningCredentials = new SigningCredentials(JwtSettings.Key(options), SecurityAlgorithms.HmacSha256),
        };
        tweak?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static string Id(Employee e) => e.Id.ToString(CultureInfo.InvariantCulture);

    private static string Unsigned(string alg, long id, string signature = "")
    {
        var header = Base64UrlEncoder.Encode($$"""{"alg":"{{alg}}","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""{"sub":"{{id}}","role":"Employee","iss":"ClockManager","aud":"ClockManager.Web","nbf":{{PastUnix}},"exp":{{FutureUnix}}}""");
        return $"{header}.{payload}.{signature}";
    }

    private static AuthOptions Options(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

    // ---- Forged and odd tokens ----

    [SqlServerFact]
    public async Task Forged_stale_and_odd_tokens_are_401_on_every_kind_of_endpoint_while_the_control_token_is_200()
    {
        await using var factory = AuthQa.NewHost(database);
        var options = Options(factory);
        var person = await AddEmployeeAsync(database);
        var manager = await AddEmployeeAsync(database, EmployeeRole.Manager);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var good = Mint(options, Id(person));
        var parts = good.Split('.');
        var swappedPayload = Base64UrlEncoder.Encode(
            $$"""{"sub":"{{manager.Id}}","role":"Manager","iss":"ClockManager","aud":"ClockManager.Web","nbf":{{PastUnix}},"exp":{{FutureUnix}}}""");

        var forged = new Dictionary<string, string>
        {
            ["alg none"] = Unsigned("none", person.Id),
            ["alg None"] = Unsigned("None", person.Id),
            ["alg NONE with junk signature"] = Unsigned("NONE", person.Id, "AAAA"),
            ["alg none, manager id"] = Unsigned("none", manager.Id),
            ["payload swapped to the manager, old signature"] = $"{parts[0]}.{swappedPayload}.{parts[2]}",
            ["signature removed"] = $"{parts[0]}.{parts[1]}.",
            ["signature cut in half"] = $"{parts[0]}.{parts[1]}.{parts[2][..(parts[2].Length / 2)]}",
            // HMAC pads a short key with zeros up to the block size, so the zero-padded key signs exactly like the real one.
            ["HS512 with the right key"] = Mint(options, Id(person), d => d.SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey!).Concat(new byte[64]).Take(64).ToArray()), SecurityAlgorithms.HmacSha512)),
            ["wrong key"] = Mint(options, Id(person), d => d.SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-key-another-key-another-key-0000")), SecurityAlgorithms.HmacSha256)),
            ["wrong issuer"] = Mint(options, Id(person), d => d.Issuer = "SomeoneElse"),
            ["missing issuer"] = Mint(options, Id(person), d => d.Issuer = null),
            ["wrong audience"] = Mint(options, Id(person), d => d.Audience = "Other.Web"),
            ["missing audience"] = Mint(options, Id(person), d => d.Audience = null),
            ["expired"] = Mint(options, Id(person), d => { d.NotBefore = TestTokens.Past; d.Expires = TestTokens.Past.AddHours(8); }),
            ["not valid yet (nbf in 2099)"] = Mint(options, Id(person), d => { d.NotBefore = TestTokens.Future; d.Expires = TestTokens.Future.AddHours(8); }),
            // A payload written by hand: the handler's descriptor would add a default exp (now + 1 h) when none is given.
            ["no exp claim"] = new JsonWebTokenHandler().CreateToken(
                $$"""{"sub":"{{person.Id}}","role":"Employee","iss":"ClockManager","aud":"ClockManager.Web","nbf":{{PastUnix}}}""",
                new SigningCredentials(JwtSettings.Key(options), SecurityAlgorithms.HmacSha256)),
            ["a JWE-shaped five part token"] = "aaa.bbb.ccc.ddd.eee",
        };
        var urls = new[] { "/api/auth/me", "/api/me/status", "/api/me/shifts", "/api/manager/employees" };

        foreach (var url in urls)
        {
            var control = await CookieGetAsync(client, url, Mint(options, Id(url.StartsWith("/api/manager", StringComparison.Ordinal) ? manager : person)));
            Assert.True(HttpStatusCode.OK == control.Status, $"control {url}: {control.Status}");
            foreach (var (label, token) in forged)
            {
                var reply = await CookieGetAsync(client, url, token);
                Assert.True(HttpStatusCode.Unauthorized == reply.Status, $"{label} on {url}: {reply.Status} {reply.Text}");
                Assert.Equal("auth.unauthenticated", reply.Code);
                Assert.Equal("application/problem+json", reply.MediaType);
                Assert.False(reply.Headers.ContainsKey("WWW-Authenticate"), $"{label}: a challenge header names the scheme");
                AssertNoLeak(reply.Text, label);
            }
        }
    }

    [SqlServerFact]
    public async Task Garbage_cookie_values_are_401_and_never_a_500_not_even_a_300_KB_one()
    {
        await using var factory = AuthQa.NewHost(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var cases = new Dictionary<string, string>
        {
            ["empty"] = "",
            ["spaces"] = "   ",
            ["Bearer prefix"] = "Bearer abc.def.ghi",
            ["three dots"] = "a.b.c",
            ["only dots"] = "....",
            ["quoted"] = "\"a.b.c\"",
            ["null text"] = "null",
            ["base64 junk"] = "e30.e30.e30",
            ["300 KB"] = new string('A', 300 * 1024),
            ["300 KB with dots"] = string.Join('.', new string('A', 100 * 1024), new string('B', 100 * 1024), new string('C', 100 * 1024)),
        };

        foreach (var (label, value) in cases)
        {
            var reply = await CookieGetAsync(client, "/api/auth/me", value);
            Assert.True(HttpStatusCode.Unauthorized == reply.Status, $"{label}: {reply.Status} {reply.Text.Length}");
            Assert.Equal("auth.unauthenticated", reply.Code);
        }
        Assert.DoesNotContain(factory.Logs, l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [SqlServerFact]
    public async Task A_valid_token_with_an_odd_subject_is_401_on_me_and_status_for_every_malformed_id()
    {
        await using var factory = AuthQa.NewHost(database);
        var options = Options(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var cases = new Dictionary<string, string?>
        {
            ["no sub claim"] = null,
            ["empty"] = "",
            ["letters"] = "abc",
            ["negative"] = "-5",
            ["plus sign"] = "+7",
            ["decimal"] = "1.5",
            ["leading space"] = " 7",
            ["zero"] = "0",
            ["int overflow"] = "99999999999",
            ["exponent"] = "1e3",
            ["sql"] = "1 OR 1=1",
            ["unknown id"] = "2000000000",
        };

        foreach (var (label, sub) in cases)
        {
            var token = Mint(options, sub);
            foreach (var url in new[] { "/api/auth/me", "/api/me/status" })
            {
                var reply = await CookieGetAsync(client, url, token);
                Assert.True(HttpStatusCode.Unauthorized == reply.Status, $"{label} on {url}: {reply.Status} {reply.Text}");
                Assert.Equal("auth.unauthenticated", reply.Code);
            }
        }
    }

    /// <summary>
    /// A token that is genuine but names nobody (sub of an employee that no longer exists, or no sub at all) is "not signed in"
    /// (401) on /me and /status, and on a manager endpoint too (QA-45; the policy used to answer 403
    /// <c>auth.forbidden</c>): the UI would show "not allowed" instead of sending the user to the login page.
    /// </summary>
    [SqlServerFact]
    public async Task QA45_a_token_for_nobody_is_401_on_manager_endpoints_too()
    {
        await using var factory = AuthQa.NewHost(database);
        var options = Options(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var failures = new List<string>();

        foreach (var (label, sub) in new (string, string?)[] { ("deleted employee", "2000000000"), ("no sub", null), ("garbage sub", "abc") })
        {
            foreach (var url in new[] { "/api/manager/employees", "/api/manager/anomalies", "/api/manager/shifts/1", "/api/manager/employees/1/shifts" })
            {
                var reply = await CookieGetAsync(client, url, Mint(options, sub));
                if (reply.Status != HttpStatusCode.Unauthorized) failures.Add($"{label} {url}: {(int)reply.Status}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>A forged-role claim for nobody is the same 401 and opens nothing (inactive or non-manager employees stay 403).</summary>
    [SqlServerFact]
    public async Task A_Manager_role_token_for_nobody_is_401_and_never_opens_anything()
    {
        await using var factory = AuthQa.NewHost(database);
        var options = Options(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var reply = await CookieGetAsync(client, "/api/manager/employees", Mint(options, "2000000000", role: "Manager"));

        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        Assert.Equal("auth.unauthenticated", reply.Code);
    }

    // ---- Where a token may travel ----

    [SqlServerFact]
    public async Task A_good_token_outside_the_cookie_is_ignored_header_query_string_or_other_cookie_name()
    {
        await using var factory = AuthQa.NewHost(database);
        var options = Options(factory);
        var person = await AddEmployeeAsync(database);
        var token = Mint(options, Id(person));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var cases = new (string Label, string Url, (string, string)[] Headers)[]
        {
            ("Bearer header", "/api/auth/me", [("Authorization", "Bearer " + token)]),
            ("lowercase bearer", "/api/auth/me", [("Authorization", "bearer " + token)]),
            ("Basic header", "/api/auth/me", [("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(person.Username + ":" + Password)))]),
            ("access_token query", "/api/auth/me?access_token=" + token, []),
            ("token query", "/api/auth/me?token=" + token, []),
            ("cookie name as query", "/api/auth/me?" + CookieName + "=" + token, []),
            ("custom header", "/api/auth/me", [("X-Access-Token", token), ("X-Auth-Token", token)]),
            ("other cookie name", "/api/auth/me", [("Cookie", "auth=" + token + "; jwt=" + token + "; token=" + token)]),
            ("cookie with a prefix", "/api/auth/me", [("Cookie", "x" + CookieName + "=" + token)]),
            ("on status", "/api/me/status?access_token=" + token, [("Authorization", "Bearer " + token)]),
        };

        foreach (var (label, url, headers) in cases)
        {
            var reply = await GetAsync(client, url, headers);
            Assert.True(HttpStatusCode.Unauthorized == reply.Status, $"{label}: {reply.Status} {reply.Text}");
        }
        Assert.Equal(HttpStatusCode.OK, (await CookieGetAsync(client, "/api/auth/me", token)).Status); // control
    }

    [SqlServerFact]
    public async Task Two_cookies_with_the_login_name_never_cause_a_500_and_a_valid_one_cannot_vouch_for_a_forged_one()
    {
        await using var factory = AuthQa.NewHost(database);
        var options = Options(factory);
        var person = await AddEmployeeAsync(database);
        var other = await AddEmployeeAsync(database);
        var mine = Mint(options, Id(person));
        var theirs = Mint(options, Id(other));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var goodThenBad = await GetAsync(client, "/api/auth/me", ("Cookie", $"{CookieName}={mine}; {CookieName}=forged"));
        var badThenGood = await GetAsync(client, "/api/auth/me", ("Cookie", $"{CookieName}=forged; {CookieName}={mine}"));
        var two = await GetAsync(client, "/api/auth/me", ("Cookie", $"{CookieName}={mine}; {CookieName}={theirs}"));

        foreach (var reply in new[] { goodThenBad, badThenGood, two }) Assert.True((int)reply.Status < 500, reply.Text);
        // One of the two wins, consistently, and it is never anything but one of the presented tokens.
        // Exactly one cookie is used (the framework keeps the last one), so a forged cookie next to a good one is either
        // the one that counts (401) or ignored (200), never "both must pass" and never a mix of the two identities.
        Assert.NotEqual(goodThenBad.Status, badThenGood.Status);
        Assert.Contains(goodThenBad.Status, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        Assert.Contains(badThenGood.Status, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        Assert.Contains(two.Json.GetProperty("username").GetString(), new[] { person.Username, other.Username });
        Assert.Equal(HttpStatusCode.OK, two.Status);
    }

    // ---- The cookie itself ----

    [SqlServerFact]
    public async Task The_login_cookie_has_the_exact_flags_the_lifetime_of_the_token_and_no_personal_data_inside()
    {
        await using var factory = AuthQa.NewHost(database);
        var person = await AddEmployeeAsync(database, displayName: "Carol Secretname");
        using var http = factory.CreateClient();
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var overHttp = await PostJsonAsync(http, "/api/auth/login", new { username = person.Username, password = Password });
        var overHttps = await PostJsonAsync(https, "/api/auth/login", new { username = person.Username, password = Password });

        foreach (var (reply, secure) in new[] { (overHttp, false), (overHttps, true) })
        {
            var cookie = Assert.Single(reply.SetCookies);
            var attributes = cookie.Split(';').Select(a => a.Trim()).ToList();
            Assert.StartsWith(CookieName + "=", attributes[0], StringComparison.Ordinal);
            Assert.Contains("httponly", attributes, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", attributes, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("path=/", attributes, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(secure, attributes.Contains("secure", StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain(attributes, a => a.StartsWith("domain=", StringComparison.OrdinalIgnoreCase)); // host-only
            Assert.DoesNotContain(attributes, a => a.StartsWith("samesite=none", StringComparison.OrdinalIgnoreCase));

            var token = new JsonWebTokenHandler().ReadJsonWebToken(CookieValue(cookie));
            var expires = DateTimeOffset.Parse(attributes.Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))[8..], CultureInfo.InvariantCulture);
            Assert.True(Math.Abs((expires.UtcDateTime - token.ValidTo).TotalSeconds) <= 2, $"cookie expires {expires:u}, token exp {token.ValidTo:u}");
            Assert.Equal(TimeSpan.FromHours(8), token.ValidTo - token.ValidFrom);
            Assert.Equal(["aud", "exp", "iat", "iss", "nbf", "role", "sub"], token.Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal));
            Assert.False(token.EncodedPayload.Length == 0);
            var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
            foreach (var secret in new[] { person.Username, "Carol", Password, "hash" })
            {
                Assert.False(payload.Contains(secret, StringComparison.OrdinalIgnoreCase), $"the token payload contains '{secret}'");
            }
        }
    }

    [SqlServerFact]
    public async Task Login_signup_me_and_logout_are_never_cacheable()
    {
        await using var factory = AuthQa.NewHost(database);
        var person = await AddEmployeeAsync(database);
        using var client = factory.CreateClient();

        var replies = new[]
        {
            await PostJsonAsync(client, "/api/auth/signup", new { username = NewName(), displayName = "Cache", password = Password }),
            await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password }),
            await GetAsync(client, "/api/auth/me"),
            await PostRawAsync(client, "/api/auth/logout", "", null),
            await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = "wrong wrong" }),
        };

        foreach (var reply in replies)
        {
            var cacheControl = reply.Headers.GetValueOrDefault("Cache-Control") ?? "";
            Assert.True(cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase), $"{reply.Status}: Cache-Control '{cacheControl}'");
        }
    }

    [SqlServerFact]
    public async Task Logout_clears_the_same_cookie_with_the_same_name_path_and_flags_over_http_and_https()
    {
        await using var factory = AuthQa.NewHost(database);
        var person = await AddEmployeeAsync(database);
        using var http = factory.CreateClient();
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var loginHttp = await PostJsonAsync(http, "/api/auth/login", new { username = person.Username, password = Password });
        var loginHttps = await PostJsonAsync(https, "/api/auth/login", new { username = person.Username, password = Password });

        var outHttp = await PostRawAsync(http, "/api/auth/logout", "", null);
        var outHttps = await PostRawAsync(https, "/api/auth/logout", "", null);

        foreach (var (login, logout) in new[] { (loginHttp, outHttp), (loginHttps, outHttps) })
        {
            var set = login.SetCookies.Single().Split(';').Select(a => a.Trim()).ToList();
            var clear = logout.SetCookies.Single().Split(';').Select(a => a.Trim()).ToList();
            Assert.Equal(HttpStatusCode.NoContent, logout.Status);
            Assert.Equal(CookieName + "=", clear[0]); // empty value, same name
            foreach (var flag in new[] { "httponly", "samesite=lax", "path=/", "secure" })
            {
                Assert.Equal(set.Contains(flag, StringComparer.OrdinalIgnoreCase), clear.Contains(flag, StringComparer.OrdinalIgnoreCase));
            }
            Assert.Contains(clear, a => a.StartsWith("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The token is stateless (D-049): logout only asks the browser to forget it. A copy of the cookie made before logout keeps
    /// working until it expires. Known limitation, pinned here so a future change (a revocation list) must update this test.
    /// </summary>
    [SqlServerFact]
    public async Task A_copied_cookie_still_works_after_logout_known_limitation_D049()
    {
        await using var factory = AuthQa.NewHost(database);
        var person = await AddEmployeeAsync(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password });
        var stolen = CookieValue(login.SetCookies.Single());

        var logout = await PostRawAsync(client, "/api/auth/logout", "", null, ("Cookie", $"{CookieName}={stolen}"));
        var replay = await CookieGetAsync(client, "/api/auth/me", stolen);

        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
    }

    // ---- A deactivated employee with a live token ----

    [SqlServerFact]
    public async Task A_deactivated_employee_with_a_live_cookie_is_403_everywhere_can_log_out_and_works_again_when_reactivated()
    {
        await using var factory = AuthQa.NewHost(database);
        var person = await AddEmployeeAsync(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password });
        var token = CookieValue(login.SetCookies.Single());
        Assert.Equal(HttpStatusCode.OK, (await CookieGetAsync(client, "/api/auth/me", token)).Status);

        await SetActiveAsync(person.Id, false);
        var me = await CookieGetAsync(client, "/api/auth/me", token);
        var status = await CookieGetAsync(client, "/api/me/status", token);
        var shifts = await CookieGetAsync(client, "/api/me/shifts", token);
        var punch = await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", "application/json",
            ("Cookie", $"{CookieName}={token}"), ("Idempotency-Key", Guid.NewGuid().ToString()));
        var logout = await PostRawAsync(client, "/api/auth/logout", "", null, ("Cookie", $"{CookieName}={token}"));

        foreach (var (label, reply) in new[] { ("me", me), ("status", status), ("shifts", shifts), ("punch", punch) })
        {
            Assert.True(HttpStatusCode.Forbidden == reply.Status, $"{label}: {reply.Status} {reply.Text}");
            Assert.Equal("employee.inactive", reply.Code);
        }
        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        await using (var db = database.CreateContext()) Assert.False(await db.Shifts.AnyAsync(s => s.EmployeeId == person.Id));

        await SetActiveAsync(person.Id, true);
        Assert.Equal(HttpStatusCode.OK, (await CookieGetAsync(client, "/api/auth/me", token)).Status);
    }

    private async Task SetActiveAsync(int employeeId, bool active)
    {
        var value = active ? 1 : 0;
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Employees] SET [IsActive] = {value} WHERE [Id] = {employeeId}");
    }
}
