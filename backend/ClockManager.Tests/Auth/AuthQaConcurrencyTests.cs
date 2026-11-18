using System.Net;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 9: many requests at once against the real host and database. Same-name signups (the unique index
/// has to decide), signups with different names, one user logging in many times, and signup followed at once by a punch.
/// Every wait has a 30 second limit.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaConcurrencyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static async Task<T[]> AllAsync<T>(IEnumerable<Func<Task<T>>> work) =>
        await Task.WhenAll(work.Select(w => Task.Run(w))).WaitAsync(Limit);

    [SqlServerFact]
    public async Task Twenty_signups_with_one_name_in_mixed_case_give_exactly_one_201_and_one_row()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var core = Guid.NewGuid().ToString("N")[..10];
        var variants = new[] { "Race-" + core, "race-" + core, "RACE-" + core.ToUpperInvariant(), "rAcE-" + core };

        var replies = await AllAsync(Enumerable.Range(0, 20).Select<int, Func<Task<Reply>>>(i => () =>
            PostJsonAsync(client, "/api/auth/signup", new { username = variants[i % variants.Length], displayName = "Racer " + i, password = Password })));

        Assert.Equal(1, replies.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(19, replies.Count(r => r.Status == HttpStatusCode.Conflict));
        Assert.All(replies.Where(r => r.Status == HttpStatusCode.Conflict), r =>
        {
            Assert.Equal("auth.username_taken", r.Code);
            Assert.Empty(r.SetCookies);
        });
        Assert.Single(replies, r => r.SetCookies.Count == 1);
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Employees.CountAsync(e => e.Username == "race-" + core));
        // The row that exists is the one the winner was told about.
        var winner = replies.Single(r => r.Status == HttpStatusCode.Created);
        Assert.Equal((await LoadAsync(database, "race-" + core)).DisplayName, "Racer " + Array.IndexOf(replies, winner));
    }

    /// <summary>
    /// The losers hit the unique index: the answer is the clean 409, handled refusals stay at Information, nothing is unhandled.
    /// </summary>
    [SqlServerFact]
    public async Task A_lost_signup_race_is_a_clean_409_and_never_an_unhandled_exception()
    {
        var (factory, replies) = await RaceAsync();
        await using var _ = factory;

        Assert.Equal(1, replies.Count(r => r.Status == HttpStatusCode.Created));
        Assert.DoesNotContain(replies, r => (int)r.Status >= 500);
        Assert.DoesNotContain(factory.Logs, l => l.Message.Contains("Unhandled exception", StringComparison.Ordinal));
        Assert.All(factory.Logs.Where(l => l.Message.Contains("Request failed with", StringComparison.Ordinal)),
            l => Assert.True(l.Level == Microsoft.Extensions.Logging.LogLevel.Information, l.Message));
    }

    /// <summary>
    /// Every loser of the race also makes EF Core log "An exception occurred in the database while saving changes" at Error, with a
    /// stack trace, although the code handles it as a 409. Same kind of noise as QA-25 (punch abort); the answer and the data are right.
    /// </summary>
    [SqlServerFact(Skip = "QA-47 open: each lost signup race (unique index) logs an EF Core Error with a stack trace, although the request is handled as a normal 409.")]
    [Trait("Status", "KnownBug")]
    public async Task QA47_a_lost_signup_race_logs_nothing_at_Error()
    {
        var (factory, _) = await RaceAsync();
        await using var _f = factory;

        var errors = factory.Logs.Where(l => l.Level >= Microsoft.Extensions.Logging.LogLevel.Error).Select(l => l.Category + ": " + l.Message).ToList();
        Assert.True(errors.Count == 0, $"{errors.Count} Error lines, e.g. {errors.FirstOrDefault()}");
    }

    private async Task<(PunchApiFactory Factory, Reply[] Replies)> RaceAsync()
    {
        var factory = NewHost(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var name = NewName();
        var replies = await AllAsync(Enumerable.Range(0, 20).Select<int, Func<Task<Reply>>>(_ => () =>
            PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Racer", password = Password })));
        return (factory, replies);
    }

    [SqlServerFact]
    public async Task Twenty_signups_with_different_names_all_succeed_and_each_cookie_belongs_to_its_own_user()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var names = Enumerable.Range(0, 20).Select(_ => NewName()).ToList();

        var replies = await AllAsync(names.Select<string, Func<Task<Reply>>>(n => () =>
            PostJsonAsync(client, "/api/auth/signup", new { username = n, displayName = "Name " + n, password = Password })));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        var mes = await AllAsync(replies.Select<Reply, Func<Task<Reply>>>(r => () => CookieGetAsync(client, "/api/auth/me", CookieValue(r.SetCookies.Single()))));
        Assert.Equal(names, mes.Select(m => m.Json.GetProperty("username").GetString()));
    }

    [SqlServerFact]
    public async Task Twenty_logins_at_once_for_one_user_all_succeed_and_every_cookie_works()
    {
        await using var factory = NewHost(database);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var person = await AddEmployeeAsync(database);

        var replies = await AllAsync(Enumerable.Range(0, 20).Select<int, Func<Task<Reply>>>(_ => () =>
            PostJsonAsync(client, "/api/auth/login", new { username = person.Username, password = Password })));

        Assert.All(replies, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Single(r.SetCookies);
        });
        var mes = await AllAsync(replies.Select<Reply, Func<Task<Reply>>>(r => () => CookieGetAsync(client, "/api/auth/me", CookieValue(r.SetCookies.Single()))));
        Assert.All(mes, m => Assert.Equal(person.Username, m.Json.GetProperty("username").GetString()));
    }

    [SqlServerFact]
    public async Task Signing_up_and_punching_straight_away_in_the_same_session_works_for_ten_users_in_parallel()
    {
        var clock = new FakeTrustedClock(StartInstant);
        await using var factory = new PunchApiFactory(database, clock);

        var results = await AllAsync(Enumerable.Range(0, 10).Select<int, Func<Task<(Reply Signup, Reply Punch, Reply Status)>>>(i => async () =>
        {
            using var client = factory.CreateClient(); // own cookie jar: the cookie from signup is the only login
            var signup = await PostJsonAsync(client, "/api/auth/signup", new { username = NewName(), displayName = "Fast " + i, password = Password });
            var punch = await PostRawAsync(client, "/api/punches", """{"intent":"clock-in"}""", "application/json", ("Idempotency-Key", Guid.NewGuid().ToString()));
            var status = await GetAsync(client, "/api/me/status");
            return (signup, punch, status);
        }));

        Assert.All(results, r =>
        {
            Assert.Equal(HttpStatusCode.Created, r.Signup.Status);
            Assert.True(HttpStatusCode.Created == r.Punch.Status, $"{r.Punch.Status} {r.Punch.Text}");
            Assert.Equal(r.Punch.Json.GetProperty("shiftId").GetInt64(), r.Status.Json.GetProperty("openShift").GetProperty("shiftId").GetInt64());
        });
        Assert.Equal(10, results.Select(r => r.Punch.Json.GetProperty("shiftId").GetInt64()).Distinct().Count());
        Assert.True(clock.Calls >= 10, $"{clock.Calls} clock calls"); // every punch asked the trusted clock (signup and login never do)
    }
}
