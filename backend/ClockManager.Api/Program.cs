using ClockManager.Api.Application.Punches;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Status;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Api.Common;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.RateLimiting;
using ClockManager.Api.Infrastructure.Time;

var builder = WebApplication.CreateBuilder(args);

// [DevelopmentOnly] controllers (the Time lab) are not even discovered outside Development: 404, not in Swagger.
builder.Services.AddControllers().ExcludeDevelopmentOnlyControllers(builder.Environment);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAppProblemDetails();

builder.Services.AddTrustedClock();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddTimeLab();
}

builder.Services.AddPersistence();

// Login (JWT in an HttpOnly cookie) and "who is punching" (ICurrentEmployee) from it.
builder.Services.AddAuth(builder.Environment);
builder.Services.AddAppRateLimiting();
builder.Services.AddScoped<PunchService>();

// The status endpoint's cached server time: display and flags only, never a record (D-035). PunchService doesn't use it.
builder.Services.AddServerNow();
builder.Services.AddScoped<StatusService>();
builder.Services.AddScoped<ClockManager.Api.Application.History.ShiftHistoryService>();

builder.Services.AddOptions<ShiftRulesOptions>()
    .BindConfiguration(ShiftRulesOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var app = builder.Build();

app.UseAppProblemDetails();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseRateLimiter(); // after authentication: the punch limit is per employee
app.UseAuthorization();
app.MapControllers();

// Development only (and only with Database:MigrateAndSeedOnStartup): apply migrations, then seed demo employees.
await app.InitializeDevelopmentDatabaseAsync();

await app.RunAsync();

/// <summary>Exposed so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
