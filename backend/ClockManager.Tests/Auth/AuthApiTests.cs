using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Infrastructure;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ClockManager.Tests.Auth;

/// <summary>Signup, login, logout and me through the real host and a real SQL Server (plans/04-auth.md §6).</summary>
[Trait("Category", "Integration")]
public sealed class AuthApiTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string Password = "correct horse 42";
    private const string CookieName = "clockmanager_auth";

    private sealed record Reply(HttpStatusCode Status, string Text, JsonElement Json, string? MediaType, IReadOnlyList<string> SetCookies);

    // ---- Signup ----

    [SqlServerFact]
    public async Task Signup_creates_an_Employee_answers_201_with_three_fields_and_signs_in()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var name = NewName();

        var signup = await SignupAsync(client, name, "Ann Example", Password);

        Assert.Equal(HttpStatusCode.Created, signup.Status);
        Assert.Equal(["username", "displayName", "role"], signup.Json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(name, signup.Json.GetProperty("username").GetString());
        Assert.Equal("Employee", signup.Json.GetProperty("role").GetString());
        Assert.Single(signup.SetCookies, c => c.StartsWith(CookieName + "=", StringComparison.Ordinal));

        var stored = await LoadAsync(name);
        Assert.Equal(EmployeeRole.Employee, stored.Role);
        Assert.NotNull(stored.PasswordHash);
        Assert.DoesNotContain(Password, stored.PasswordHash, StringComparison.Ordinal);

        var me = await GetAsync(client, "/api/auth/me"); // the cookie from signup is enough
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal(name, me.Json.GetProperty("username").GetString());
    }

    [SqlServerFact]
    public async Task Signup_cannot_ask_for_a_role()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var name = NewName();

        var reply = await PostAsync(client, "/api/auth/signup", new { username = name, displayName = "Mallory", password = Password, role = "Manager" });

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.Equal("Employee", reply.Json.GetProperty("role").GetString());
        Assert.Equal(EmployeeRole.Employee, (await LoadAsync(name)).Role);
    }

    [SqlServerFact]
    public async Task Signup_with_a_taken_name_in_another_case_is_409_username_taken_and_creates_nothing()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var name = "Mixed" + Guid.NewGuid().ToString("N")[..10];
        await SignupAsync(client, name, "First", Password);
        using var other = factory.CreateClient();

        var second = await SignupAsync(other, name.ToUpperInvariant(), "Second", Password);

        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal(ErrorCodes.AuthUsernameTaken, second.Json.GetProperty("code").GetString());
        Assert.Empty(second.SetCookies);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Employees.CountAsync(e => e.Username == name));
    }

    [SqlServerTheory]
    [InlineData("ab", "Ann", "longenough1", "username")]
    [InlineData("has space", "Ann", "longenough1", "username")]
    [InlineData("bad!name", "Ann", "longenough1", "username")]
    [InlineData("valid1\n", "Ann", "longenough1", "username")] // a trailing newline must not slip through
    [InlineData("<33>", "Ann", "longenough1", "username")]
    [InlineData(null, "Ann", "longenough1", "username")]
    [InlineData("ok", "", "longenough1", "displayName")]
    [InlineData("ok", "   ", "longenough1", "displayName")]
    [InlineData("ok", "<129>", "longenough1", "displayName")]
    [InlineData("ok", "Ann", "short1", "password")]
    [InlineData("ok", "Ann", "<129>", "password")]
    [InlineData("ok", "Ann", null, "password")]
    public async Task Signup_with_invalid_input_is_400_request_invalid_and_creates_nothing(string? username, string? displayName, string? password, string field)
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var unique = NewName();
        username = username == "ok" ? unique : Expand(username);

        var reply = await PostAsync(client, "/api/auth/signup", new { username, displayName = Expand(displayName), password = Expand(password) });

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal(ErrorCodes.RequestInvalid, reply.Json.GetProperty("code").GetString());
        Assert.True(reply.Json.GetProperty("errors").TryGetProperty(field, out _), reply.Text);
        Assert.Empty(reply.SetCookies);
        if (password is not null) Assert.DoesNotContain(Expand(password)!, reply.Text, StringComparison.Ordinal); // never echoed
        await using var db = database.CreateContext();
        Assert.False(await db.Employees.AnyAsync(e => e.Username == unique));
    }

    // ---- Login ----

    [SqlServerFact]
    public async Task Login_with_the_right_password_is_200_with_the_cookie_and_the_username_is_case_insensitive()
    {
        await using var factory = NewFactory();
        var name = NewName();
        using (var signupClient = factory.CreateClient()) await SignupAsync(signupClient, name, "Ann Example", Password);
        using var client = factory.CreateClient();

        var login = await PostAsync(client, "/api/auth/login", new { username = name.ToUpperInvariant(), password = Password });

        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Equal(["username", "displayName", "role"], login.Json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(name, login.Json.GetProperty("username").GetString()); // as stored, not as typed
        Assert.Single(login.SetCookies);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/auth/me")).Status);
    }

    [SqlServerFact]
    public async Task A_wrong_password_an_unknown_user_and_a_user_without_a_password_get_the_same_401()
    {
        await using var factory = NewFactory();
        var name = NewName();
        using (var signupClient = factory.CreateClient()) await SignupAsync(signupClient, name, "Ann Example", Password);
        var seeded = await AddEmployeeWithoutPasswordAsync();
        using var client = factory.CreateClient();

        var wrongPassword = await PostAsync(client, "/api/auth/login", new { username = name, password = "not the password" });
        var unknownUser = await PostAsync(client, "/api/auth/login", new { username = "nobody" + NewName(), password = Password });
        var noPassword = await PostAsync(client, "/api/auth/login", new { username = seeded, password = Password });

        foreach (var reply in new[] { wrongPassword, unknownUser, noPassword })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
            Assert.Equal("application/problem+json", reply.MediaType);
            Assert.Equal(ErrorCodes.AuthInvalidCredentials, reply.Json.GetProperty("code").GetString());
            Assert.Empty(reply.SetCookies);
        }
        Assert.Equal(Stable(wrongPassword), Stable(unknownUser));
        Assert.Equal(Stable(wrongPassword), Stable(noPassword));
    }

    [SqlServerFact]
    public async Task An_inactive_employee_with_the_right_password_is_403_employee_inactive_and_with_a_wrong_one_401()
    {
        await using var factory = NewFactory();
        var name = NewName();
        using (var signupClient = factory.CreateClient()) await SignupAsync(signupClient, name, "Ann Example", Password);
        await using (var db = database.CreateContext())
        {
            var employee = await db.Employees.SingleAsync(e => e.Username == name);
            db.Entry(employee).Property(e => e.IsActive).CurrentValue = false;
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();

        var right = await PostAsync(client, "/api/auth/login", new { username = name, password = Password });
        var wrong = await PostAsync(client, "/api/auth/login", new { username = name, password = "not the password" });

        Assert.Equal(HttpStatusCode.Forbidden, right.Status);
        Assert.Equal(ErrorCodes.EmployeeInactive, right.Json.GetProperty("code").GetString());
        Assert.Empty(right.SetCookies);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status); // an inactive account is not revealed without the password
    }

    // ---- Cookie and token ----

    [SqlServerFact]
    public async Task The_cookie_is_HttpOnly_SameSite_Lax_path_root_not_Secure_over_http_and_Secure_over_https()
    {
        await using var factory = NewFactory();
        var name = NewName();

        using var http = factory.CreateClient();
        var overHttp = (await SignupAsync(http, name, "Ann Example", Password)).SetCookies.Single();
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var overHttps = (await PostAsync(https, "/api/auth/login", new { username = name, password = Password })).SetCookies.Single();

        foreach (var cookie in new[] { overHttp, overHttps })
        {
            Assert.StartsWith(CookieName + "=", cookie, StringComparison.Ordinal);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("secure", overHttp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", overHttps, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task The_token_has_sub_role_and_an_8_hour_life_signed_with_HS256()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var name = NewName();

        var cookie = (await SignupAsync(client, name, "Ann Example", Password)).SetCookies.Single();

        var token = new JsonWebTokenHandler().ReadJsonWebToken(CookieValue(cookie));
        Assert.Equal("HS256", token.Alg);
        Assert.Equal((await LoadAsync(name)).Id.ToString(CultureInfo.InvariantCulture), token.GetClaim("sub").Value);
        Assert.Equal("Employee", token.GetClaim("role").Value);
        Assert.Equal(TimeSpan.FromHours(8), token.ValidTo - token.ValidFrom);
    }

    [SqlServerFact]
    public async Task Logout_is_204_clears_the_cookie_and_also_works_when_nobody_is_signed_in()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        await SignupAsync(client, NewName(), "Ann Example", Password);

        var logout = await PostAsync(client, "/api/auth/logout", null);
        var me = await GetAsync(client, "/api/auth/me");
        using var stranger = factory.CreateClient();
        var again = await PostAsync(stranger, "/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        Assert.Contains("expires=Thu, 01 Jan 1970", logout.SetCookies.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, me.Status);
        Assert.Equal(HttpStatusCode.NoContent, again.Status);
    }

    // ---- me, and who may call what ----

    [SqlServerFact]
    public async Task Me_without_a_cookie_is_401_auth_unauthenticated_as_problem_json()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var me = await GetAsync(client, "/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, me.Status);
        Assert.Equal("application/problem+json", me.MediaType);
        Assert.Equal(ErrorCodes.Unauthenticated, me.Json.GetProperty("code").GetString());
        Assert.Equal(401, me.Json.GetProperty("status").GetInt32());
    }

    [SqlServerFact]
    public async Task An_expired_a_tampered_a_wrong_key_and_a_bearer_header_token_are_all_401_and_a_valid_one_is_200()
    {
        await using var factory = NewFactory();
        using var signupClient = factory.CreateClient();
        var name = NewName();
        var cookie = (await SignupAsync(signupClient, name, "Ann Example", Password)).SetCookies.Single();
        var id = (await LoadAsync(name)).Id;
        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var good = TestTokens.Create(options, id, "Employee", TestTokens.Past, TestTokens.Future);
        var expired = TestTokens.Create(options, id, "Employee", TestTokens.Past, TestTokens.Past.AddHours(8));
        var wrongKey = TestTokens.Create(options, id, "Employee", TestTokens.Past, TestTokens.Future, signingKey: "another-key-another-key-another-key-0000");
        var tampered = Tamper(CookieValue(cookie));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/auth/me", good)).Status); // control: only the flaw differs
        foreach (var bad in new[] { expired, wrongKey, tampered, "not-a-token" })
        {
            var reply = await GetAsync(client, "/api/auth/me", bad);
            Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
            Assert.Equal(ErrorCodes.Unauthenticated, reply.Json.GetProperty("code").GetString());
        }

        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        bearer.Headers.TryAddWithoutValidation("Authorization", "Bearer " + good);
        using var bearerResponse = await client.SendAsync(bearer);
        Assert.Equal(HttpStatusCode.Unauthorized, bearerResponse.StatusCode); // the cookie is the only way in
    }

    [SqlServerFact]
    public async Task A_valid_token_for_a_deleted_employee_is_401()
    {
        await using var factory = NewFactory();
        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var ghost = TestTokens.Create(options, 2_000_000_000, "Employee", TestTokens.Past, TestTokens.Future);

        var reply = await GetAsync(client, "/api/auth/me", ghost);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        Assert.Equal(ErrorCodes.Unauthenticated, reply.Json.GetProperty("code").GetString());
    }

    [SqlServerFact]
    public async Task Punch_and_status_without_login_are_401_and_the_time_provider_is_not_asked()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        using var punch = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent("""{"intent":"clock-in"}""", Encoding.UTF8, "application/json"),
        };
        punch.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());

        using var punchResponse = await client.SendAsync(punch);
        var status = await GetAsync(client, "/api/me/status");

        Assert.Equal(HttpStatusCode.Unauthorized, punchResponse.StatusCode);
        Assert.Equal("application/problem+json", punchResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, status.Status);
        Assert.Equal(ErrorCodes.Unauthenticated, status.Json.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
    }

    [SqlServerFact]
    public async Task Two_users_never_see_each_others_shifts()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var ann = factory.CreateClient();
        using var ben = factory.CreateClient();
        await SignupAsync(ann, NewName(), "Ann", Password);
        await SignupAsync(ben, NewName(), "Ben", Password);

        var annIn = await PunchAsync(ann, "clock-in");
        var benStatus = await GetAsync(ben, "/api/me/status");
        var benIn = await PunchAsync(ben, "clock-in"); // would be 409 shift.already_open if Ben shared Ann's shift
        var annStatus = await GetAsync(ann, "/api/me/status");

        Assert.Equal(HttpStatusCode.Created, annIn.Status);
        Assert.Equal(JsonValueKind.Null, benStatus.Json.GetProperty("openShift").ValueKind);
        Assert.Equal(HttpStatusCode.Created, benIn.Status);
        Assert.NotEqual(annIn.Json.GetProperty("shiftId").GetInt32(), benIn.Json.GetProperty("shiftId").GetInt32());
        Assert.Equal(annIn.Json.GetProperty("shiftId").GetInt32(), annStatus.Json.GetProperty("openShift").GetProperty("shiftId").GetInt32());
    }

    [SqlServerFact]
    public async Task Neither_the_password_nor_the_hash_appears_in_any_response_or_log()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var name = NewName();
        var secret = "Sup3r-secret-" + Guid.NewGuid().ToString("N");
        var wrong = "Wr0ng-secret-" + Guid.NewGuid().ToString("N");

        var replies = new List<Reply>
        {
            await SignupAsync(client, name, "Ann Example", secret),
            await SignupAsync(client, name, "Ann Again", secret), // 409
            await PostAsync(client, "/api/auth/signup", new { username = "x", displayName = "Ann", password = wrong }), // 400
            await PostAsync(client, "/api/auth/login", new { username = name, password = wrong }), // 401
            await PostAsync(client, "/api/auth/login", new { username = name, password = secret }),
            await GetAsync(client, "/api/auth/me"),
        };
        var hash = (await LoadAsync(name)).PasswordHash!;

        var everything = string.Join("\n", replies.Select(r => r.Text))
            + "\n" + string.Join("\n", factory.Logs.Select(l => l.Message + " " + l.Exception));
        foreach (var forbidden in new[] { secret, wrong, hash })
        {
            Assert.DoesNotContain(forbidden, everything, StringComparison.Ordinal);
        }
        Assert.NotEmpty(factory.Logs); // the logs were really captured
    }

    // ---- Helpers ----

    private PunchApiFactory NewFactory() => new(database, new FakeTrustedClock("2026-01-12T07:00:00Z"));

    private static string NewName() => "u" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Placeholders for strings too long to write in an attribute.</summary>
    private static string? Expand(string? value) => value switch
    {
        "<33>" => new string('a', 33),
        "<129>" => new string('a', 129),
        _ => value,
    };

    private async Task<Employee> LoadAsync(string username)
    {
        await using var db = database.CreateContext();
        return await db.Employees.AsNoTracking().SingleAsync(e => e.Username == username);
    }

    private async Task<string> AddEmployeeWithoutPasswordAsync()
    {
        await using var db = database.CreateContext();
        var employee = PersistenceTestData.NewEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Username;
    }

    private static string CookieValue(string setCookie) => setCookie[(CookieName.Length + 1)..setCookie.IndexOf(';', StringComparison.Ordinal)];

    /// <summary>Changes one character in the middle of the signature (the last character may only carry padding bits).</summary>
    private static string Tamper(string token)
    {
        var chars = token.ToCharArray();
        var index = token.LastIndexOf('.') + 5;
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    /// <summary>Everything that must be identical between two failed logins (the trace id and the like may differ).</summary>
    private static string Stable(Reply reply) =>
        string.Join("|", new[] { "status", "title", "detail", "code", "instance" }.Select(p => reply.Json.GetProperty(p).ToString()));

    private static Task<Reply> SignupAsync(HttpClient client, string username, string displayName, string password) =>
        PostAsync(client, "/api/auth/signup", new { username, displayName, password });

    private static Task<Reply> PunchAsync(HttpClient client, string intent)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { intent }), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        return SendAsync(client, message);
    }

    private static Task<Reply> PostAsync(HttpClient client, string url, object? body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url);
        if (body is not null) message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return SendAsync(client, message);
    }

    private static Task<Reply> GetAsync(HttpClient client, string url, string? token = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) message.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={token}");
        return SendAsync(client, message);
    }

    private static async Task<Reply> SendAsync(HttpClient client, HttpRequestMessage message)
    {
        using (message)
        {
            using var response = await client.SendAsync(message);
            var text = await response.Content.ReadAsStringAsync();
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
            return new Reply(response.StatusCode, text, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(),
                response.Content.Headers.ContentType?.MediaType, cookies);
        }
    }
}
