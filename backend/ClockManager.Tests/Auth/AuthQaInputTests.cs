using System.Diagnostics;
using System.Net;
using System.Text;
using ClockManager.Api.Domain;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, groups 1 and 2: what signup and login accept (names, passwords, display names, content types, odd JSON)
/// and whether login answers leak anything. One host per test; the cases of a test share it and name themselves in the failure message.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaInputTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private async Task<bool> ExistsWithDisplayNameAsync(string marker)
    {
        await using var db = database.CreateContext();
        return await db.Employees.AnyAsync(e => e.DisplayName == marker);
    }

    // ---- 1. Usernames ----

    [SqlServerFact]
    public async Task Username_length_is_3_to_32_boundaries_included()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var cases = new (int Length, HttpStatusCode Expected)[]
        {
            (2, HttpStatusCode.BadRequest), (3, HttpStatusCode.Created), (32, HttpStatusCode.Created), (33, HttpStatusCode.BadRequest),
        };

        foreach (var (length, expected) in cases)
        {
            var name = (Guid.NewGuid().ToString("N") + "zz")[..length];
            var reply = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Len " + length, password = Password });
            Assert.True(expected == reply.Status, $"length {length}: {reply.Status} {reply.Text}");
            if (expected == HttpStatusCode.BadRequest) Assert.True(reply.HasErrorFor("username"), reply.Text);
        }
    }

    [SqlServerFact]
    public async Task Look_alike_unicode_whitespace_control_and_emoji_usernames_are_400_and_create_nothing()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var cases = new Dictionary<string, string>
        {
            ["cyrillic a"] = "аlice" + Guid.NewGuid().ToString("N")[..6],
            ["fullwidth a"] = "ａlice" + Guid.NewGuid().ToString("N")[..6],
            ["kelvin sign"] = "Kelvin" + Guid.NewGuid().ToString("N")[..6],
            ["dotless i"] = "ıstan" + Guid.NewGuid().ToString("N")[..6],
            ["emoji"] = "party\U0001F389" + Guid.NewGuid().ToString("N")[..6],
            ["soft hyphen"] = "so­ft" + Guid.NewGuid().ToString("N")[..6],
            ["zero width space"] = "zero​width" + Guid.NewGuid().ToString("N")[..6],
            ["right-to-left override"] = "rtl‮evil" + Guid.NewGuid().ToString("N")[..6],
            ["nul byte"] = "nul\u0000byte" + Guid.NewGuid().ToString("N")[..6],
            ["tab"] = "tab\tname" + Guid.NewGuid().ToString("N")[..6],
            ["leading space"] = " lead" + Guid.NewGuid().ToString("N")[..6],
            ["trailing space"] = "trail" + Guid.NewGuid().ToString("N")[..6] + " ",
            ["trailing CRLF"] = "crlf" + Guid.NewGuid().ToString("N")[..6] + "\r\n",
            ["inner newline"] = "new\nline" + Guid.NewGuid().ToString("N")[..6],
            ["sql quote"] = "o'brien" + Guid.NewGuid().ToString("N")[..6],
            ["sql comment"] = "x'--" + Guid.NewGuid().ToString("N")[..6],
            ["wildcard"] = "100%" + Guid.NewGuid().ToString("N")[..6],
            ["path"] = "../etc" + Guid.NewGuid().ToString("N")[..6],
        };

        foreach (var (label, name) in cases)
        {
            var marker = "Marker " + label + " " + Guid.NewGuid().ToString("N");
            var reply = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = marker, password = Password });
            Assert.True(HttpStatusCode.BadRequest == reply.Status, $"{label}: {reply.Status} {reply.Text}");
            Assert.True(reply.HasErrorFor("username"), $"{label}: {reply.Text}");
            Assert.Empty(reply.SetCookies);
            Assert.False(await ExistsWithDisplayNameAsync(marker), label);
        }
    }

    [SqlServerFact]
    public async Task Signup_names_that_differ_only_by_case_are_one_name_for_every_ASCII_variant()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var core = Guid.NewGuid().ToString("N")[..8];
        var first = await PostJsonAsync(client, "/api/auth/signup", new { username = "Ab-" + core + "_X.y", displayName = "First", password = Password });
        Assert.Equal(HttpStatusCode.Created, first.Status);

        foreach (var variant in new[] { "ab-" + core + "_x.y", "AB-" + core.ToUpperInvariant() + "_X.Y", "aB-" + core + "_x.Y" })
        {
            var reply = await PostJsonAsync(client, "/api/auth/signup", new { username = variant, displayName = "Again", password = Password });
            Assert.True(HttpStatusCode.Conflict == reply.Status, $"{variant}: {reply.Status} {reply.Text}");
            Assert.Equal(ErrorCodes.AuthUsernameTaken, reply.Code);
        }
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Employees.CountAsync(e => e.Username == "ab-" + core + "_x.y"));
    }

    /// <summary>
    /// Login looks the name up with the database's comparison, which is case-insensitive and ignores trailing spaces.
    /// A name that signup could never have stored (a trailing space, full-width letters) therefore still logs in. Harmless (the
    /// password is still needed, and signup can never create a colliding name) and the answer shows the stored name, so this only
    /// pins the behaviour; look-alikes the collation does not fold (Cyrillic, the Kelvin sign) must NOT log in.
    /// </summary>
    [SqlServerFact]
    public async Task Login_name_matching_follows_the_collation_trailing_space_folds_look_alikes_do_not()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var name = "kk" + Guid.NewGuid().ToString("N")[..10];
        Assert.Equal(HttpStatusCode.Created, (await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "K", password = Password })).Status);

        var trailing = await PostJsonAsync(client, "/api/auth/login", new { username = name + " ", password = Password });
        var cyrillic = await PostJsonAsync(client, "/api/auth/login", new { username = name.Replace("k", "к", StringComparison.Ordinal), password = Password });
        var fullWidth = await PostJsonAsync(client, "/api/auth/login", new { username = "ｋｋ" + name[2..], password = Password });
        var kelvin = await PostJsonAsync(client, "/api/auth/login", new { username = "K" + name[1..], password = Password });

        Assert.Equal(HttpStatusCode.OK, trailing.Status); // SQL Server ignores trailing spaces in "="
        Assert.Equal(name, trailing.Json.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.OK, fullWidth.Status); // the default collation is width-insensitive
        Assert.Equal(name, fullWidth.Json.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, cyrillic.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, kelvin.Status);
    }

    // ---- 1. Passwords ----

    [SqlServerFact]
    public async Task Password_length_is_8_to_128_UTF16_units_and_every_accepted_one_logs_in()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var cases = new (string Label, string Password, HttpStatusCode Expected)[]
        {
            ("7 chars", new string('p', 7), HttpStatusCode.BadRequest),
            ("8 chars", new string('p', 8), HttpStatusCode.Created),
            ("128 chars", new string('p', 128), HttpStatusCode.Created),
            ("129 chars", new string('p', 129), HttpStatusCode.BadRequest),
            ("unicode", "pässwörd-日本語-\U0001F600", HttpStatusCode.Created),
            ("spaces inside and around", "  spaced  pass  ", HttpStatusCode.Created),
            ("64 emoji = 128 units", string.Concat(Enumerable.Repeat("\U0001F600", 64)), HttpStatusCode.Created),
            ("65 emoji = 130 units", string.Concat(Enumerable.Repeat("\U0001F600", 65)), HttpStatusCode.BadRequest),
        };

        foreach (var (label, password, expected) in cases)
        {
            var name = NewName();
            var signup = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = label, password });
            Assert.True(expected == signup.Status, $"{label}: {signup.Status} {signup.Text}");
            if (expected != HttpStatusCode.Created) continue;

            using var fresh = factory.CreateClient();
            var exact = await PostJsonAsync(fresh, "/api/auth/login", new { username = name, password });
            Assert.True(HttpStatusCode.OK == exact.Status, $"{label} exact: {exact.Status}");
            var altered = await PostJsonAsync(fresh, "/api/auth/login", new { username = name, password = "z" + password[(char.IsSurrogate(password[0]) ? 2 : 1)..] });
            Assert.True(HttpStatusCode.Unauthorized == altered.Status, $"{label} altered: {altered.Status}");
        }
    }

    [SqlServerFact]
    public async Task A_password_is_never_trimmed_or_normalised_so_only_the_exact_text_logs_in()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var name = NewName();
        await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Spaces", password = "  keep my spaces  " });

        var trimmed = await PostJsonAsync(client, "/api/auth/login", new { username = name, password = "keep my spaces" });
        var exact = await PostJsonAsync(client, "/api/auth/login", new { username = name, password = "  keep my spaces  " });

        Assert.Equal(HttpStatusCode.Unauthorized, trimmed.Status);
        Assert.Equal(HttpStatusCode.OK, exact.Status);
    }

    /// <summary>The 8-character minimum counts UTF-16 code units (<c>[StringLength]</c>): 4 emoji pass. Pinned, see the QA report.</summary>
    [SqlServerFact]
    public async Task Four_emoji_pass_the_8_character_minimum_because_length_counts_UTF16_units()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();

        var reply = await PostJsonAsync(client, "/api/auth/signup",
            new { username = NewName(), displayName = "Emoji", password = string.Concat(Enumerable.Repeat("\U0001F600", 4)) });

        Assert.Equal(HttpStatusCode.Created, reply.Status);
    }

    [SqlServerFact]
    public async Task A_whitespace_only_password_is_400_not_a_weak_account()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var name = NewName();

        var reply = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Blank", password = "         " });

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.True(reply.HasErrorFor("password"), reply.Text);
    }

    [SqlServerFact]
    public async Task A_huge_password_or_name_is_refused_by_validation_before_any_hashing_and_stays_fast()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var megabyte = new string('a', 1024 * 1024);
        var tenMegabytes = new string('b', 10 * 1024 * 1024);
        var requests = new (string Label, string Url, object Body, string Field)[]
        {
            ("signup 1 MB password", "/api/auth/signup", new { username = NewName(), displayName = "Big", password = megabyte }, "password"),
            ("signup 10 MB display name", "/api/auth/signup", new { username = NewName(), displayName = tenMegabytes, password = Password }, "displayName"),
            ("login 1 MB password", "/api/auth/login", new { username = NewName(), password = megabyte }, "password"),
            ("login 10 MB username", "/api/auth/login", new { username = tenMegabytes, password = Password }, "username"),
        };

        foreach (var (label, url, body, field) in requests)
        {
            var watch = Stopwatch.StartNew();
            var reply = await PostJsonAsync(client, url, body);
            watch.Stop();

            Assert.True(HttpStatusCode.BadRequest == reply.Status, $"{label}: {reply.Status}");
            Assert.True(reply.HasErrorFor(field), $"{label}: {reply.Text.Length} chars");
            Assert.True(reply.Text.Length < 2000, $"{label}: the error echoes the input ({reply.Text.Length} chars)");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{label}: {watch.Elapsed}");
        }
    }

    // ---- 1. Display names ----

    [SqlServerFact]
    public async Task Display_names_with_markup_are_stored_as_is_and_only_ever_come_back_JSON_escaped()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var manager = await AddEmployeeAsync(database, EmployeeRole.Manager);
        var name = NewName();
        const string display = "<script>alert('x')</script><img src=x onerror=alert(1)> & \"q\"";

        var signup = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = display, password = Password });
        var me = await GetAsync(client, "/api/auth/me");
        using var managerClient = factory.CreateClient();
        await PostJsonAsync(managerClient, "/api/auth/login", new { username = manager.Username, password = Password });
        var list = await GetAsync(managerClient, "/api/manager/employees");

        Assert.Equal(display, (await LoadAsync(database, name)).DisplayName); // stored as typed
        // The MVC JSON writer uses relaxed escaping: '<' and '&' stay raw inside the JSON string. That is safe only because every
        // response is typed application/json (never sniffed as HTML) and the React app renders text, never HTML (no
        // dangerouslySetInnerHTML anywhere). The missing nosniff header is QA-44 below.
        foreach (var (label, reply) in new[] { ("signup", signup), ("me", me), ("manager list", list) })
        {
            Assert.True(reply.MediaType == "application/json", $"{label}: {reply.MediaType}");
        }
        Assert.Equal(display, me.Json.GetProperty("displayName").GetString()); // it decodes back to what was typed
        Assert.Equal(display, signup.Json.GetProperty("displayName").GetString());
    }

    [SqlServerFact(Skip = "QA-44 open: no 'X-Content-Type-Options: nosniff' on any API response, while user-controlled display names come back with raw '<' and '&' inside JSON (defence in depth).")]
    [Trait("Status", "KnownBug")]
    public async Task QA44_JSON_responses_carry_nosniff_because_they_echo_user_text()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var name = NewName();

        var signup = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "<img src=x onerror=alert(1)>", password = Password });
        var me = await GetAsync(client, "/api/auth/me");
        var failed = await PostJsonAsync(client, "/api/auth/login", new { username = name, password = "wrong wrong" });

        foreach (var (label, reply) in new[] { ("signup", signup), ("me", me), ("401", failed) })
        {
            Assert.True(reply.Headers.GetValueOrDefault("X-Content-Type-Options") == "nosniff", $"{label}: {string.Join(",", reply.Headers.Keys)}");
        }
    }

    [SqlServerFact]
    public async Task Display_names_with_control_characters_never_cause_a_500()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var cases = new Dictionary<string, string>
        {
            ["nul"] = "Ann\u0000Bob",
            ["newlines"] = "Ann\r\nBob\n",
            ["tab and bell"] = "Ann\t\u0007Bob",
            ["rtl override"] = "Ann‮bob",
            ["emoji"] = "Ann \U0001F600",
            ["quotes"] = "Ann \"\\ ' ;-- DROP TABLE Employees",
        };

        foreach (var (label, display) in cases)
        {
            var name = NewName();
            var reply = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = display, password = Password });
            Assert.True((int)reply.Status < 500, $"{label}: {reply.Status} {reply.Text}");
            if (reply.Status == HttpStatusCode.Created)
            {
                Assert.Equal(display.Trim(), (await LoadAsync(database, name)).DisplayName);
                Assert.Equal(display.Trim(), (await GetAsync(client, "/api/auth/me")).Json.GetProperty("displayName").GetString());
            }
        }
    }

    [SqlServerFact]
    public async Task Display_name_is_trimmed_and_the_limit_is_128_units_before_trimming()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var padded = NewName();
        var atLimit = NewName();

        var spaces = await PostJsonAsync(client, "/api/auth/signup", new { username = padded, displayName = "   Ann   ", password = Password });
        var exact = await PostJsonAsync(client, "/api/auth/signup", new { username = atLimit, displayName = new string('d', 128), password = Password });

        Assert.Equal(HttpStatusCode.Created, spaces.Status);
        Assert.Equal("Ann", (await LoadAsync(database, padded)).DisplayName);
        Assert.Equal(HttpStatusCode.Created, exact.Status);
    }

    // ---- 1. Content types ----

    [SqlServerFact]
    public async Task Signup_and_login_accept_only_JSON_other_content_types_are_415_and_nothing_happens()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var existing = await AddEmployeeAsync(database);
        var marker = "Marker " + Guid.NewGuid().ToString("N");
        var jsonBody = $$"""{"username":"{{NewName()}}","displayName":"{{marker}}","password":"{{Password}}"}""";
        var formBody = $"username={NewName()}&displayName={marker}&password=correct+horse+42";
        var loginJson = $$"""{"username":"{{existing.Username}}","password":"{{Password}}"}""";
        var cases = new (string Label, string Body, string? ContentType)[]
        {
            ("text/plain (the form-enctype CSRF trick)", jsonBody, "text/plain"),
            ("text/plain;charset=UTF-8", jsonBody, "text/plain;charset=UTF-8"),
            ("form-urlencoded", formBody, "application/x-www-form-urlencoded"),
            ("xml", "<a/>", "application/xml"),
            ("text/json", jsonBody, "text/json"),
            ("no content type", jsonBody, null),
        };

        foreach (var (label, body, contentType) in cases)
        {
            var signup = await PostRawAsync(client, "/api/auth/signup", body, contentType);
            Assert.True(HttpStatusCode.UnsupportedMediaType == signup.Status, $"signup {label}: {signup.Status} {signup.Text}");
            Assert.Equal(ErrorCodes.UnsupportedMediaType, signup.Code);
            Assert.Equal("application/problem+json", signup.MediaType);
            Assert.Empty(signup.SetCookies);

            var login = await PostRawAsync(client, "/api/auth/login", loginJson, contentType);
            Assert.True(HttpStatusCode.UnsupportedMediaType == login.Status, $"login {label}: {login.Status}");
            Assert.Empty(login.SetCookies);
        }
        Assert.False(await ExistsWithDisplayNameAsync(marker));
    }

    [SqlServerFact]
    public async Task JSON_with_a_charset_parameter_or_other_case_still_works_and_a_wrong_charset_is_never_a_500()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        string Body() => $$"""{"username":"{{NewName()}}","displayName":"Ct","password":"{{Password}}"}""";

        var utf8 = await PostRawAsync(client, "/api/auth/signup", Body(), "application/json; charset=utf-8");
        var quoted = await PostRawAsync(client, "/api/auth/signup", Body(), "application/json; charset=\"utf-8\"");
        var upper = await PostRawAsync(client, "/api/auth/signup", Body(), "APPLICATION/JSON");

        Assert.Equal(HttpStatusCode.Created, utf8.Status);
        Assert.Equal(HttpStatusCode.Created, quoted.Status);
        Assert.Equal(HttpStatusCode.Created, upper.Status);
        // Charsets the JSON reader does not support are a clean 415 (utf-16 and an empty charset are QA-42 below).
        foreach (var charset in new[] { "iso-8859-1", "bogus", "utf-7", "utf-32", "us-ascii" })
        {
            var reply = await PostRawAsync(client, "/api/auth/signup", Body(), "application/json; charset=" + charset);
            Assert.True(HttpStatusCode.UnsupportedMediaType == reply.Status, $"{charset}: {reply.Status}");
            AssertNoLeak(reply.Text, charset);
        }
    }

    /// <summary>
    /// The client chooses the charset of the body. <c>utf-16</c> (with a UTF-8 body) and an empty <c>charset=</c> make the JSON
    /// reader throw DecoderFallbackException / ArgumentOutOfRangeException: an unhandled 500 and an Error log line per request,
    /// on every JSON endpoint (signup and login need no sign-in). No data is touched and the body leaks nothing.
    /// </summary>
    [SqlServerFact(Skip = "QA-42 open: Content-Type 'application/json; charset=utf-16' (or an empty charset=) makes every JSON endpoint answer 500 server.error and log an Error; expected 400/415.")]
    [Trait("Status", "KnownBug")]
    public async Task QA42_a_utf16_or_empty_charset_is_a_4xx_not_a_500_on_every_JSON_endpoint()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);
        string Signup() => $$"""{"username":"{{NewName()}}","displayName":"Ct","password":"{{Password}}"}""";
        var login = $$"""{"username":"{{person.Username}}","password":"{{Password}}"}""";
        var failures = new List<string>();

        foreach (var charset in new[] { "utf-16", "utf-16le", "unicode", "" })
        {
            var type = "application/json; charset=" + charset;
            var signup = await PostRawAsync(client, "/api/auth/signup", Signup(), type);
            var loginReply = await PostRawAsync(client, "/api/auth/login", login, type);
            var punch = await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", type,
                (PunchApiFactoryHeaders.User, person.Username), ("Idempotency-Key", Guid.NewGuid().ToString()));
            foreach (var (label, reply) in new[] { ("signup", signup), ("login", loginReply), ("punch", punch) })
            {
                if ((int)reply.Status >= 500) failures.Add($"{label} charset='{charset}': {(int)reply.Status}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
        Assert.DoesNotContain(factory.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    // ---- 1. Odd JSON ----

    [SqlServerFact]
    public async Task Malformed_wrong_typed_and_hostile_JSON_gives_400_request_invalid_with_no_internals_for_both_endpoints()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var good = $$"""{"username":"{{NewName()}}","displayName":"Ok","password":"{{Password}}"}""";
        var cases = new Dictionary<string, byte[]>
        {
            ["empty body"] = [],
            ["whitespace body"] = Encoding.UTF8.GetBytes("   "),
            ["null literal"] = Encoding.UTF8.GetBytes("null"),
            ["array"] = Encoding.UTF8.GetBytes("[]"),
            ["string"] = Encoding.UTF8.GetBytes("\"username\""),
            ["number"] = Encoding.UTF8.GetBytes("123"),
            ["empty object"] = Encoding.UTF8.GetBytes("{}"),
            ["truncated"] = Encoding.UTF8.GetBytes("{\"username\":\"abc"),
            ["trailing comma"] = Encoding.UTF8.GetBytes("""{"username":"abc","password":"12345678",}"""),
            ["comment"] = Encoding.UTF8.GetBytes("""{"username":"abc" /* x */,"password":"12345678"}"""),
            ["username is a number"] = Encoding.UTF8.GetBytes("""{"username":12345,"displayName":"A","password":"12345678"}"""),
            ["password is an object"] = Encoding.UTF8.GetBytes("""{"username":"abcd","displayName":"A","password":{"a":1}}"""),
            ["password is an array"] = Encoding.UTF8.GetBytes("""{"username":"abcd","displayName":"A","password":["x"]}"""),
            ["username is true"] = Encoding.UTF8.GetBytes("""{"username":true,"displayName":"A","password":"12345678"}"""),
            ["huge number"] = Encoding.UTF8.GetBytes("""{"username":1e999999,"displayName":"A","password":"12345678"}"""),
            ["invalid utf-8"] = [0x7B, 0x22, 0xFF, 0xFE, 0x22, 0x3A, 0x31, 0x7D],
            ["lone surrogate escape"] = Encoding.UTF8.GetBytes("""{"username":"ab\ud800cd","displayName":"\ud800","password":"\ud800\ud800\ud800\ud800\ud800\ud800\ud800\ud800"}"""),
            ["bad escape"] = Encoding.UTF8.GetBytes("""{"username":"ab\qcd","displayName":"A","password":"12345678"}"""),
            ["nested 5000 deep"] = Encoding.UTF8.GetBytes(new string('[', 5000) + new string(']', 5000)),
            ["nested object in a field, 200 deep"] = Encoding.UTF8.GetBytes("""{"username":""" + string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200) + "}"),
            ["BOM before the object"] = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(good)],
        };

        foreach (var url in new[] { "/api/auth/signup", "/api/auth/login" })
        {
            foreach (var (label, body) in cases)
            {
                var reply = await PostRawAsync(client, url, body, "application/json");
                var where = $"{url} {label}";
                if (label == "BOM before the object")
                {
                    Assert.True((int)reply.Status < 500, $"{where}: {reply.Status} {reply.Text}"); // accepted (it signs up, then logs in), never a 500
                    continue;
                }
                Assert.True(HttpStatusCode.BadRequest == reply.Status, $"{where}: {reply.Status} {reply.Text}");
                Assert.Equal("application/problem+json", reply.MediaType);
                Assert.Equal(ErrorCodes.RequestInvalid, reply.Code);
                Assert.Empty(reply.SetCookies);
                AssertNoLeak(reply.Text, where);
            }
        }
    }

    /// <summary>
    /// Duplicate JSON members: the punch body rejects them (QA-17, D-041) because a gateway and the app could read different
    /// values. Signup and login read the LAST one silently. Not exploitable today (nothing in these bodies is a privilege),
    /// but the rule "the first and the last reader must agree" is the same.
    /// </summary>
    [SqlServerFact(Skip = "QA-41 open: signup and login accept duplicate JSON members (the last one wins), while the punch body rejects them with 400 (D-041).")]
    [Trait("Status", "KnownBug")]
    public async Task QA41_duplicate_members_in_signup_and_login_are_400_like_in_the_punch_body()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var existing = await AddEmployeeAsync(database);

        var signup = await PostRawAsync(client, "/api/auth/signup",
            $$"""{"username":"{{NewName()}}","username":"{{NewName()}}","displayName":"Dup","password":"{{Password}}"}""", "application/json");
        var login = await PostRawAsync(client, "/api/auth/login",
            $$"""{"username":"nobody","username":"{{existing.Username}}","password":"{{Password}}"}""", "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, signup.Status);
        Assert.Equal(HttpStatusCode.BadRequest, login.Status);
    }

    /// <summary>Documents today's behaviour of the same case, so the skipped test above has a passing twin.</summary>
    [SqlServerFact]
    public async Task Today_the_last_duplicate_member_wins_in_login()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var existing = await AddEmployeeAsync(database);

        var login = await PostRawAsync(client, "/api/auth/login",
            $$"""{"username":"nobody","username":"{{existing.Username}}","password":"{{Password}}"}""", "application/json");

        Assert.Equal(HttpStatusCode.OK, login.Status); // "nobody" was ignored, the last username was used
    }

    [SqlServerFact]
    public async Task Extra_fields_like_role_isActive_id_and_passwordHash_are_ignored_and_never_applied()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var name = NewName();
        const string planted = "AQAAAAEAACcQAAAAEPLANTEDHASHPLANTEDHASHPLANTEDHASH";

        var signup = await PostRawAsync(client, "/api/auth/signup",
            $$"""
            {"username":"{{name}}","displayName":"Mallory","password":"{{Password}}",
             "role":"Manager","Role":1,"isActive":false,"IsActive":false,"id":1,"employeeId":1,"passwordHash":"{{planted}}",
             "rowVersion":"AAAA","isAdmin":true}
            """, "application/json");

        Assert.Equal(HttpStatusCode.Created, signup.Status);
        Assert.Equal("Employee", signup.Json.GetProperty("role").GetString());
        var stored = await LoadAsync(database, name);
        Assert.Equal(EmployeeRole.Employee, stored.Role);
        Assert.True(stored.IsActive);
        Assert.NotEqual(planted, stored.PasswordHash);
        Assert.Equal(HttpStatusCode.OK, (await PostJsonAsync(client, "/api/auth/login", new { username = name, password = Password })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostJsonAsync(client, "/api/auth/login", new { username = name, password = planted })).Status);

        // The same fields on login change nothing either.
        var manager = await PostRawAsync(client, "/api/auth/login",
            $$"""{"username":"{{name}}","password":"{{Password}}","role":"Manager"}""", "application/json");
        Assert.Equal("Employee", manager.Json.GetProperty("role").GetString());
    }

    // ---- 2. Enumeration ----

    [SqlServerFact]
    public async Task Failed_logins_are_identical_down_to_the_headers_and_the_byte_length()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var known = await AddEmployeeAsync(database);
        var withoutPassword = await AddEmployeeAsync(database, withPassword: false);

        var wrong = await PostJsonAsync(client, "/api/auth/login", new { username = known.Username, password = "not the password" });
        var unknown = await PostJsonAsync(client, "/api/auth/login", new { username = NewName(), password = "not the password" });
        var none = await PostJsonAsync(client, "/api/auth/login", new { username = withoutPassword.Username, password = Password });
        var wrongCase = await PostJsonAsync(client, "/api/auth/login", new { username = known.Username.ToUpperInvariant(), password = "not the password" });

        foreach (var reply in new[] { unknown, none, wrongCase })
        {
            Assert.Equal(wrong.Status, reply.Status);
            Assert.Equal(wrong.ContentLength, reply.ContentLength);
            Assert.Equal(wrong.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase), reply.Headers.Keys.Order(StringComparer.OrdinalIgnoreCase));
            foreach (var header in new[] { "Cache-Control", "Pragma", "Content-Type", "WWW-Authenticate" })
            {
                Assert.Equal(wrong.Headers.GetValueOrDefault(header), reply.Headers.GetValueOrDefault(header));
            }
            Assert.Empty(reply.SetCookies);
            Assert.False(reply.Text.Contains(known.Username, StringComparison.OrdinalIgnoreCase), "the body echoes a username");
        }
    }

    [SqlServerFact]
    public async Task Inactive_account_with_the_right_password_is_403_with_no_cookie_and_the_same_shape_of_headers()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var person = await AddEmployeeAsync(database);
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Employees] SET [IsActive] = 0 WHERE [Id] = {person.Id}");
        }

        var right = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password });
        var wrong = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = "nope nope nope" });

        Assert.Equal(HttpStatusCode.Forbidden, right.Status);
        Assert.Equal("employee.inactive", right.Code);
        Assert.Empty(right.SetCookies);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        AssertNoLeak(right.Text, "403");
    }

    /// <summary>
    /// Rough timing check: an unknown name must cost about as much as a wrong password (one PBKDF2 either way, D-052).
    /// The tolerance is deliberately wide: a real leak (no hashing at all) is more than 10x faster, noise is not.
    /// </summary>
    [SqlServerFact]
    public async Task An_unknown_user_costs_about_as_much_time_as_a_wrong_password()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();
        var known = await AddEmployeeAsync(database);
        var withoutPassword = await AddEmployeeAsync(database, withPassword: false);
        await PostJsonAsync(client, "/api/auth/login", new { username = known.Username, password = "warm up the host" }); // JIT, pool, hasher

        async Task<double> MedianMsAsync(Func<string> name)
        {
            var times = new List<double>();
            for (var i = 0; i < 7; i++)
            {
                var start = Stopwatch.GetTimestamp();
                var reply = await PostJsonAsync(client, "/api/auth/login", new { username = name(), password = "not the password" });
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
            }
            times.Sort();
            return times[times.Count / 2];
        }

        var wrong = await MedianMsAsync(() => known.Username);
        var unknown = await MedianMsAsync(NewName);
        var noHash = await MedianMsAsync(() => withoutPassword.Username);

        Assert.True(unknown > wrong * 0.4, $"unknown {unknown:F1} ms vs wrong password {wrong:F1} ms: no hashing for unknown names?");
        Assert.True(noHash > wrong * 0.4, $"no hash {noHash:F1} ms vs wrong password {wrong:F1} ms: no hashing without a hash?");
    }

    /// <summary>
    /// A hash column that is valid base64 but not a PasswordHasher string (an admin writes 'disabled' into it to lock the
    /// account) is a plain failed login.
    /// </summary>
    [SqlServerFact]
    public async Task A_stored_hash_that_is_base64_but_not_a_real_hash_is_a_normal_401()
    {
        await RunCorruptHashesAsync(["disabled", "AQ==", "", " ", new string('A', 200)]);
    }

    /// <summary>
    /// The same with text that is not base64 (a natural way to lock an account: '!' or 'LOCKED-by-admin' in the hash column).
    /// Identity's hasher throws FormatException, nothing catches it: login answers 500 and logs an Error.
    /// </summary>
    [SqlServerFact(Skip = "QA-43 open: a PasswordHash that is not valid base64 (e.g. '!') makes POST /api/auth/login answer 500 server.error; expected the normal 401.")]
    [Trait("Status", "KnownBug")]
    public async Task QA43_a_stored_hash_that_is_not_base64_is_a_normal_401_not_a_500()
    {
        await RunCorruptHashesAsync(["!", "LOCKED-by-admin", "not base64 at all"]);
    }

    private async Task RunCorruptHashesAsync(string[] cases)
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient();

        foreach (var hash in cases)
        {
            var person = await AddEmployeeAsync(database);
            await using (var db = database.CreateContext())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Employees] SET [PasswordHash] = {hash} WHERE [Id] = {person.Id}");
            }

            var reply = await PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password });

            Assert.True(HttpStatusCode.Unauthorized == reply.Status, $"hash '{hash}': {reply.Status} {reply.Text}");
            Assert.Equal("auth.invalid_credentials", reply.Code);
        }
    }
}
