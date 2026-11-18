using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Tests.Auth;

/// <summary>One HTTP answer, with everything the security tests compare: status, body, JSON, every header, every Set-Cookie.</summary>
internal sealed record Reply(
    HttpStatusCode Status,
    string Text,
    JsonElement Json,
    string? MediaType,
    IReadOnlyList<string> SetCookies,
    IReadOnlyDictionary<string, string> Headers,
    long? ContentLength)
{
    public string Code => Json.GetProperty("code").GetString()!;

    public bool HasErrorFor(string field) => Json.TryGetProperty("errors", out var errors) && errors.TryGetProperty(field, out _);
}

internal static class PunchApiFactoryHeaders
{
    public const string User = ClockManager.Tests.Punches.PunchApiFactory.TestUserHeader;
}

/// <summary>Helpers shared by the AuthQa* tests: a throwaway host, raw requests with any content type, and employees written straight to the database.</summary>
internal static class AuthQa
{
    public const string Password = "correct horse 42";
    public const string CookieName = "clockmanager_auth";
    public const string StartInstant = "2026-01-12T07:00:00Z";

    public static string NewName() => "q" + Guid.NewGuid().ToString("N")[..12];

    public static PunchApiFactory NewHost(MigratedDatabaseFixture database, FakeTrustedClock? clock = null) =>
        new(database, clock ?? new FakeTrustedClock(StartInstant));

    /// <summary>Adds an employee with a known password straight to the database (no signup, so no rate-limit budget used).</summary>
    public static async Task<Employee> AddEmployeeAsync(
        MigratedDatabaseFixture database, EmployeeRole role = EmployeeRole.Employee, string? displayName = null, bool withPassword = true)
    {
        await using var db = database.CreateContext();
        var employee = new Employee(NewName(), displayName ?? "Qa Person", role);
        if (withPassword) employee.SetPasswordHash(new PasswordService(new PasswordHasher<Employee>()).Hash(employee, Password));
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    public static async Task<Employee> LoadAsync(MigratedDatabaseFixture database, string username)
    {
        await using var db = database.CreateContext();
        return await db.Employees.AsNoTracking().SingleAsync(e => e.Username == username);
    }

    public static Task<Reply> PostJsonAsync(HttpClient client, string url, object body, params (string Name, string Value)[] headers) =>
        SendAsync(client, HttpMethod.Post, url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), headers);

    /// <summary>A body written by hand: for duplicate members, bad UTF-8, odd content types.</summary>
    public static Task<Reply> PostRawAsync(HttpClient client, string url, byte[] body, string? contentType, params (string Name, string Value)[] headers)
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return SendAsync(client, HttpMethod.Post, url, content, headers);
    }

    public static Task<Reply> PostRawAsync(HttpClient client, string url, string body, string? contentType, params (string Name, string Value)[] headers) =>
        PostRawAsync(client, url, Encoding.UTF8.GetBytes(body), contentType, headers);

    public static Task<Reply> GetAsync(HttpClient client, string url, params (string Name, string Value)[] headers) =>
        SendAsync(client, HttpMethod.Get, url, null, headers);

    public static Task<Reply> CookieGetAsync(HttpClient client, string url, string token) =>
        GetAsync(client, url, ("Cookie", $"{CookieName}={token}"));

    public static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string url, HttpContent? content, params (string Name, string Value)[] headers)
    {
        using var message = new HttpRequestMessage(method, url) { Content = content };
        foreach (var (name, value) in headers) message.Headers.TryAddWithoutValidation(name, value);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(message, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);

        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers)) all[header.Key] = string.Join(", ", header.Value);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

        var json = default(JsonElement);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) json = JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(response.StatusCode, text, json, response.Content.Headers.ContentType?.MediaType, cookies, all, response.Content.Headers.ContentLength);
    }

    public static string CookieValue(string setCookie) =>
        setCookie[(CookieName.Length + 1)..setCookie.IndexOf(';', StringComparison.Ordinal)];

    /// <summary>Words that would show a stack trace, a CLR type name or a parser position in a client-facing body.</summary>
    public static readonly string[] LeakMarkers =
    [
        "System.", "ClockManager.", "Exception", "LineNumber", "BytePositionInLine", "   at ", ".cs:", "Microsoft.", "Newtonsoft", "Stack",
    ];

    public static void AssertNoLeak(string text, string where)
    {
        foreach (var marker in LeakMarkers)
        {
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{where}: body leaks '{marker}': {text}");
        }
    }
}
