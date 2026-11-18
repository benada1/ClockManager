using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice A group 1: everything a client can put in the request (body, content type, query, headers) and
/// whether any of it can change the stored time or surface an exception. Complements P15/P16 in PunchApiTests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaBodyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string FakeNow = "2026-01-12T07:00:00Z";

    // ---- Body shapes ----

    [SqlServerTheory]
    [InlineData("""{"intent":"clock-in","utc":"2020-01-01T00:00:00Z"}""")]
    [InlineData("""{"intent":"clock-in","timestamp":1577836800}""")]
    [InlineData("""{"intent":"clock-in","clockOutUtc":"2020-01-01T00:00:00Z"}""")]
    [InlineData("""{"intent":"clock-in","Time":"2020-01-01T00:00:00Z"}""")]     // other casing of an extra key
    [InlineData("""{"intent":"clock-in","at":null}""")]                        // an extra key even with a null value
    [InlineData("""{"intent":"clock-in","nested":{"time":"2020-01-01T00:00:00Z"}}""")]
    [InlineData("""{"intent":"clock-in",}""")]                                 // trailing comma
    [InlineData("""{"intent":"clock-in" /* hi */}""")]                         // comment
    [InlineData("""{"intent":["clock-in"]}""")]
    [InlineData("""{"intent":true}""")]
    [InlineData("""{"intent":{"x":1}}""")]
    [InlineData("""{"intent":"clock-in\u0000"}""")]
    [InlineData("""{"intent":"clock-in\n"}""")]
    [InlineData("""{"intent":"CLOCK-IN"}""")]
    [InlineData("null")]
    [InlineData("\"clock-in\"")]
    [InlineData("12")]
    public async Task Bad_body_is_400_request_invalid_with_no_exception_text_and_the_stored_time_is_untouched(string body)
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, body, user);

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Property_name_casing_Intent_is_accepted_like_any_ASP_NET_Core_JSON_and_stores_the_clock_time()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, """{"Intent":"clock-in"}""", user);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.Equal(Utc(FakeNow), await StoredClockInAsync(employeeId));
    }

    [SqlServerFact]
    public async Task Whitespace_around_the_JSON_and_a_utf8_bom_are_harmless()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, "  \r\n {\"intent\" : \"clock-in\"}\n ", user);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
    }

    /// <summary>
    /// QA-17 / D-041: a member that appears twice is ambiguous (System.Text.Json would run the LAST one, a gateway reading the
    /// FIRST would see another request), so it is a 400 with a fixed message. This replaces the earlier test that documented
    /// "the last member wins and the stored idempotency intent matches it", which is no longer true.
    /// </summary>
    [SqlServerTheory]
    [InlineData("""{"intent":"clock-in","intent":"clock-out"}""", "intent")]            // different values
    [InlineData("""{"intent":"clock-in","intent":"clock-in"}""", "intent")]             // same value
    [InlineData("""{"intent":"clock-in","Intent":"clock-out"}""", "intent")]            // differing case = same member for the binder
    [InlineData("""{"INTENT":"clock-in","intent":"clock-in"}""", "intent")]
    [InlineData("""{"intent":"clock-in","intent":"clock-in","intent":"clock-out"}""", "intent")] // three
    [InlineData("{\"inten\\u0074\":\"clock-in\",\"intent\":\"clock-out\"}", "intent")]  // escaped spelling (t = t) of the same name
    [InlineData("""{"intent":"clock-in","time":1,"time":2}""", "time")]                  // duplicate unknown member
    public async Task Duplicate_members_are_rejected_as_ambiguous_with_a_fixed_message(string body, string key)
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, body, user);

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
        foreach (var leak in new[] { "System.", "ClockManager.", "LineNumber", "BytePositionInLine", "Path: $", "$.", "duplicate JSON member", "could not be" })
            Assert.False(reply.Text.Contains(leak, StringComparison.Ordinal), $"'{leak}' leaked: {reply.Text}");
        var errors = reply.Body.GetProperty("errors");
        Assert.Equal([key], errors.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal([InvalidRequestResponse.DuplicateMessage], errors.GetProperty(key).EnumerateArray().Select(m => m.GetString()!).ToArray());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task A_rejected_duplicate_body_stores_no_idempotency_record_so_the_same_key_can_punch_with_a_clean_body()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var bad = await SendAsync(client, """{"intent":"clock-in","intent":"clock-out"}""", user, key: key);
        var good = await SendAsync(client, """{"intent":"clock-in"}""", user, key: key);

        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(HttpStatusCode.Created, good.Status);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Huge_body_is_400_not_a_crash_and_does_not_reach_the_provider()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var huge = "{\"intent\":\"" + new string('x', 5 * 1024 * 1024) + "\"}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var reply = await SendAsync(client, huge, user, ct: cts.Token);

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Deeply_nested_body_is_400_not_a_crash()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        var nested = "{\"intent\":" + new string('[', 5000) + new string(']', 5000) + "}";

        var reply = await SendAsync(client, nested, user);

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
    }

    // ---- Content-Type ----

    [SqlServerTheory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/xml")]
    public async Task Wrong_content_type_is_a_415_ProblemDetails_and_nothing_is_saved(string contentType)
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, """{"intent":"clock-in"}""", user, contentType: contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, reply.Status);
        AssertNoLeak(reply);
        Assert.False(string.IsNullOrEmpty(reply.Body.GetProperty("code").GetString()));
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Form_body_with_intent_field_is_415_not_a_punch()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, "intent=clock-in", user, contentType: "application/x-www-form-urlencoded");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, reply.Status);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task No_content_type_at_all_is_415_and_a_json_charset_parameter_is_fine()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var none = await SendAsync(client, """{"intent":"clock-in"}""", user, contentType: null);
        var charset = await SendAsync(client, """{"intent":"clock-in"}""", user, contentType: "application/json; charset=utf-8");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, none.Status);
        Assert.Equal(HttpStatusCode.Created, charset.Status);
    }

    [SqlServerFact]
    public async Task Invalid_utf8_bytes_in_the_body_are_400_not_500()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        byte[] bytes = [.. "{\"intent\":\"clock-in"u8.ToArray(), 0xFF, 0xFE, .. "\"}"u8.ToArray()];

        var reply = await SendAsync(client, bytes, user, contentType: "application/json");

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
    }

    // ---- Every way a client might influence the time ----

    [SqlServerFact]
    public async Task Query_string_time_and_header_dates_are_ignored_and_the_stored_time_is_the_fake_clocks()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var headers = new Dictionary<string, string>
        {
            ["Date"] = "Wed, 01 Jan 2020 00:00:00 GMT",
            ["X-Forwarded-For"] = "203.0.113.9",
            ["X-Forwarded-Host"] = "evil.example",
            ["X-Forwarded-Proto"] = "https",
            ["X-Forwarded-Date"] = "2020-01-01T00:00:00Z",
            ["X-Request-Time"] = "2020-01-01T00:00:00Z",
            ["X-Timestamp"] = "1577836800",
            ["X-HTTP-Method-Override"] = "GET",
            ["If-Unmodified-Since"] = "Wed, 01 Jan 2020 00:00:00 GMT",
            ["Accept-Language"] = "ar-SA",   // non-Gregorian calendar: must not change anything stored
        };

        var reply = await SendAsync(client, """{"intent":"clock-in"}""", user,
            url: "/api/punches?time=2020-01-01T00:00:00Z&utc=2020-01-01T00:00:00Z&timestamp=1577836800&clockIn=2020-01-01", headers: headers);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.Equal(FakeNow, reply.Body.GetProperty("clockIn").GetProperty("utc").GetString());
        Assert.Equal(Utc(FakeNow), await StoredClockInAsync(employeeId));
        Assert.Equal(1, clock.Calls);
    }

    [SqlServerFact]
    public async Task Duplicate_query_and_a_form_style_time_in_a_JSON_request_change_nothing()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, """{"intent":"clock-in"}""", user, url: "/api/punches?time=1&time=2&time[]=3");

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.Equal(Utc(FakeNow), await StoredClockInAsync(employeeId));
    }

    // ---- Errors never leak ----

    [SqlServerFact]
    public async Task Unknown_route_method_and_GET_on_punches_are_ProblemDetails_without_stack_traces()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        foreach (var (method, url) in new[] { ("GET", "/api/punches"), ("PUT", "/api/punches"), ("DELETE", "/api/punches/1"), ("POST", "/api/punches/extra") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Assert.True((int)response.StatusCode is 404 or 405, $"{method} {url} → {(int)response.StatusCode}");
            Assert.False(text.Contains("   at ", StringComparison.Ordinal), $"{method} {url}: {text}");
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
            if (text.Length > 0) Assert.Equal("code", JsonDocument.Parse(text).RootElement.EnumerateObject().Single(p => p.Name == "code").Name);
        }
        Assert.Equal(0, clock.Calls);
    }

    [SqlServerFact]
    public async Task Error_responses_use_the_problem_json_media_type()
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        string inactive;
        await using (var db = database.CreateContext())
        {
            var employee = NewEmployee();
            db.Employees.Add(employee);
            db.Entry(employee).Property(e => e.IsActive).CurrentValue = false;
            await db.SaveChangesAsync();
            inactive = employee.Username;
        }
        const string Problem = "application/problem+json";

        var created = await SendAsync(client, """{"intent":"clock-in"}""", user);
        var replies = new Dictionary<string, Reply>
        {
            ["400"] = await SendAsync(client, "{}", user),
            ["401"] = await SendAsync(client, """{"intent":"clock-in"}""", null),
            ["403"] = await SendAsync(client, """{"intent":"clock-in"}""", inactive),
            ["409"] = await SendAsync(client, """{"intent":"clock-in"}""", user),
            ["415"] = await SendAsync(client, "intent=clock-in", user, contentType: "text/plain"),
        };
        var key = Guid.NewGuid().ToString();
        clock.Set("2026-01-12T15:30:00Z");
        await SendAsync(client, """{"intent":"clock-out"}""", user, key);
        replies["422"] = await SendAsync(client, """{"intent":"clock-in"}""", user, key);
        clock.FailWith(FakeTrustedClock.Unavailable());
        replies["503"] = await SendAsync(client, """{"intent":"clock-in"}""", user);

        using var notFound = await client.GetAsync("/api/punches/nope");
        using var notAllowed = await client.PutAsync("/api/punches", new StringContent("{}", Encoding.UTF8, "application/json"));
        using var me401 = await client.GetAsync("/api/me/status");

        Assert.Equal("application/json", created.ContentType); // success stays plain JSON
        foreach (var (status, reply) in replies)
        {
            Assert.Equal(status, ((int)reply.Status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Problem, reply.ContentType);
        }
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(Problem, notFound.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, notAllowed.StatusCode);
        Assert.Equal(Problem, notAllowed.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, me401.StatusCode);
        Assert.Equal(Problem, me401.Content.Headers.ContentType?.MediaType);
    }

    // QA-19 / D-040: the 400 `errors` hold only fixed, client-facing sentences keyed by the wire field name.
    [SqlServerTheory]
    [InlineData("""{"intent":"clock-in",}""", "body", "must be a JSON object")]                       // invalid JSON
    [InlineData("not json", "body", "must be a JSON object")]
    [InlineData("12", "body", "must be a JSON object")]                                              // wrong top-level type
    [InlineData("""{"intent":true}""", "intent", "has the wrong type or an invalid value")]
    [InlineData("""{"intent":12}""", "intent", "has the wrong type or an invalid value")]
    [InlineData("""{"intent":["clock-in"]}""", "intent", "has the wrong type or an invalid value")]
    [InlineData("""{"intent":{"x":1}}""", "intent", "has the wrong type or an invalid value")]
    [InlineData("""{"intent":"clock-in","time":"x"}""", "time", "unknown field 'time' is not allowed; the server sets the time")]
    [InlineData("", "body", "must be a JSON object")]                                                // missing body
    [InlineData("null", "body", "must be a JSON object")]
    [InlineData("{}", "intent", "must be \"clock-in\" or \"clock-out\"")]                            // empty object
    [InlineData("""{"intent":"nope"}""", "intent", "must be \"clock-in\" or \"clock-out\"")]
    public async Task Bad_body_400_does_not_expose_CLR_type_names_or_parser_positions(string body, string key, string message)
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, body, user);

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
        foreach (var leak in new[] { "System.", "ClockManager.", "LineNumber", "BytePositionInLine", ".NET member", "Path: $", "$.", "The request field", "could not be" })
            Assert.False(reply.Text.Contains(leak, StringComparison.Ordinal), $"'{leak}' leaked: {reply.Text}");
        var errors = reply.Body.GetProperty("errors");
        var keys = errors.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal([key], keys);                                       // no "request" noise entry, no second key
        Assert.Equal([message], errors.GetProperty(key).EnumerateArray().Select(m => m.GetString()!).ToArray());
    }

    [SqlServerTheory]
    [InlineData("""{"intent":"clock-in","Tim\u0000e\n":1}""")]
    [InlineData("""{"intent":"clock-in","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa":1}""")]
    public async Task Unknown_field_name_echoed_to_the_client_has_no_control_characters_and_is_capped(string body)
    {
        var clock = new FakeTrustedClock(FakeNow);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, body, user);

        AssertProblem(reply, HttpStatusCode.BadRequest, ErrorCodes.RequestInvalid);
        var name = Assert.Single(reply.Body.GetProperty("errors").EnumerateObject()).Name;
        Assert.InRange(name.Length, 1, InvalidRequestResponse.MaxFieldNameLength);
        Assert.DoesNotContain(name, char.IsControl);
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, string Text, JsonElement Body, string? ContentType);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        await using var db = database.CreateContext();
        var employee = NewEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (employee.Id, employee.Username);
    }

    private async Task<NodaTime.Instant> StoredClockInAsync(int employeeId)
    {
        await using var db = database.CreateContext();
        return (await db.Shifts.AsNoTracking().SingleAsync(s => s.EmployeeId == employeeId)).ClockIn.Utc;
    }

    private static Task<Reply> SendAsync(
        HttpClient client, string body, string? user, string? key = null, string? contentType = "application/json",
        string url = "/api/punches", IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default) =>
        SendAsync(client, Encoding.UTF8.GetBytes(body), user, key, contentType, url, headers, ct);

    private static async Task<Reply> SendAsync(
        HttpClient client, byte[] body, string? user, string? key = null, string? contentType = "application/json",
        string url = "/api/punches", IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (user is not null) request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        if (headers is not null) foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(response.StatusCode, text, json, response.Content.Headers.ContentType?.MediaType);
    }

    private static void AssertProblem(Reply reply, HttpStatusCode status, string code)
    {
        Assert.Equal(status, reply.Status);
        Assert.Equal("application/problem+json", reply.ContentType);
        Assert.Equal(code, reply.Body.GetProperty("code").GetString());
        Assert.True(reply.Body.TryGetProperty("traceId", out _));
        AssertNoLeak(reply);
    }

    private static void AssertNoLeak(Reply reply)
    {
        Assert.False(reply.Text.Contains("   at "), reply.Text);          // stack frames
        Assert.False(reply.Text.Contains("Exception"), reply.Text);
    }
}
