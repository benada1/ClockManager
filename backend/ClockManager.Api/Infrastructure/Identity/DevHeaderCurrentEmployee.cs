using ClockManager.Api.Application.Employees;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Api.Infrastructure.Identity;

/// <summary>
/// DEVELOPMENT ONLY stand-in for login (D-027): the request names its employee in the header
/// <c>X-Dev-Employee: &lt;username&gt;</c> (e.g. <c>alice</c>). Anyone can claim to be anyone, so this class is
/// registered only when the environment is Development (<see cref="CurrentEmployeeServiceCollectionExtensions"/>).
/// Phase 4 deletes it and adds cookie login behind the same <see cref="ICurrentEmployee"/>.
/// </summary>
internal sealed class DevHeaderCurrentEmployee(IHttpContextAccessor httpContextAccessor, ClockManagerDbContext db)
    : ICurrentEmployee
{
    public const string HeaderName = "X-Dev-Employee";

    public async Task<CurrentEmployee?> GetAsync(CancellationToken ct)
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null || !headers.TryGetValue(HeaderName, out var values)) return null;

        // Exactly one non-blank value; two values would make "who is this?" ambiguous, so nobody is signed in.
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) return null;
        var username = values[0]!.Trim();
        if (username.Length > Employee.UsernameMaxLength) return null;

        // Unknown username → null → 401. Same comparison as UX_Employees_Username (the database collation).
        return await db.Employees
            .AsNoTracking()
            .Where(e => e.Username == username)
            .Select(e => new CurrentEmployee(e.Id, e.Username, e.DisplayName, e.Role, e.IsActive))
            .SingleOrDefaultAsync(ct);
    }
}

/// <summary>Every environment except Development, until Phase 4: there is no way to sign in, so every punch is a 401.</summary>
internal sealed class NoSignedInEmployee : ICurrentEmployee
{
    public Task<CurrentEmployee?> GetAsync(CancellationToken ct) => Task.FromResult<CurrentEmployee?>(null);
}

public static class CurrentEmployeeServiceCollectionExtensions
{
    /// <summary>
    /// Development: the <c>X-Dev-Employee</c> header. Anywhere else: nobody is ever signed in (the header is not read
    /// at all). Plan 03 risk "the dev header leaks into Production"; <c>PunchApiTests.P17_*</c> proves it doesn't.
    /// </summary>
    public static IServiceCollection AddCurrentEmployee(this IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentEmployee, DevHeaderCurrentEmployee>();
            services.Configure<Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions>(o =>
                o.OperationFilter<DevEmployeeHeaderOperationFilter>());
        }
        else
        {
            services.AddSingleton<ICurrentEmployee, NoSignedInEmployee>();
        }

        return services;
    }
}

/// <summary>
/// Development only: shows the <c>X-Dev-Employee</c> header on every API operation in Swagger, so punches can be tried
/// there. The header is not a bound action parameter (it is read by <see cref="DevHeaderCurrentEmployee"/>).
/// </summary>
internal sealed class DevEmployeeHeaderOperationFilter : Swashbuckle.AspNetCore.SwaggerGen.IOperationFilter
{
    public void Apply(Microsoft.OpenApi.Models.OpenApiOperation operation, Swashbuckle.AspNetCore.SwaggerGen.OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) != true) return;
        operation.Parameters ??= [];
        operation.Parameters.Add(new Microsoft.OpenApi.Models.OpenApiParameter
        {
            Name = DevHeaderCurrentEmployee.HeaderName,
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Required = false,
            Description = "Development-only stand-in for login (D-027): a seeded username, e.g. alice, bob, carol, manager.",
            Schema = new Microsoft.OpenApi.Models.OpenApiSchema { Type = "string" },
        });
    }
}
