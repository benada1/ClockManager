using System.Security.Cryptography;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Application.Employees;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ClockManager.Api.Infrastructure.Identity;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Login: validated options, password hashing, the JWT read from an HttpOnly cookie, <see cref="ICurrentEmployee"/> from the
    /// token, and 401 / 403 as ProblemDetails. A bearer header is NOT accepted: the cookie is the only way in.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        if (environment.IsDevelopment())
        {
            // Development only: no key set means a random one for this run (logins end when the server restarts).
            services.AddOptions<AuthOptions>().PostConfigure<ILoggerFactory>((options, logs) =>
            {
                if (!string.IsNullOrWhiteSpace(options.SigningKey)) return;
                options.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                logs.CreateLogger("ClockManager.Auth").LogWarning("Auth:SigningKey is not set: using a random key for this run.");
            });
        }

        services.AddSingleton<IPasswordHasher<Employee>, PasswordHasher<Employee>>();
        services.AddSingleton<PasswordService>();
        services.AddSingleton<IAuthClock, AuthClock>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<AuthCookie>();
        services.AddScoped<AuthService>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentEmployee, CookieCurrentEmployee>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
        services.AddScoped<IAuthorizationHandler, ManagerPolicy.Handler>();
        services.AddAuthorization(o => o.AddPolicy(ManagerPolicy.Name, p => p.RequireAuthenticatedUser().AddRequirements(new ManagerPolicy.Requirement())));

        services.Configure<SwaggerGenOptions>(o =>
        {
            o.AddSecurityDefinition("cookieAuth", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = new AuthOptions().CookieName,
                Description = "Log in with POST /api/auth/login: the browser then keeps the HttpOnly cookie and sends it by itself.",
            });
            o.OperationFilter<CookieAuthOperationFilter>();
        });

        return services;
    }

    private sealed class ConfigureJwtBearer(IOptions<AuthOptions> options) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions o) => Configure(JwtBearerDefaults.AuthenticationScheme, o);

        public void Configure(string? name, JwtBearerOptions o)
        {
            if (name != JwtBearerDefaults.AuthenticationScheme) return;
            var auth = options.Value;

            o.MapInboundClaims = false; // keep "sub" and "role" as written
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = JwtSettings.Key(auth),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // not "none", not another algorithm
                ValidateIssuer = true,
                ValidIssuer = JwtSettings.Issuer,
                ValidateAudience = true,
                ValidAudience = JwtSettings.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero, // one server issues and checks the token
                NameClaimType = JwtSettings.SubjectClaim,
                RoleClaimType = JwtSettings.RoleClaim,
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.Request.Cookies.TryGetValue(auth.CookieName, out var token) && !string.IsNullOrEmpty(token))
                    {
                        context.Token = token;
                    }
                    else
                    {
                        context.NoResult(); // no cookie = anonymous; never fall back to an Authorization header
                    }
                    return Task.CompletedTask;
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse(); // no empty 401: write the ProblemDetails
                    await WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated,
                        "You are not signed in.", "Sign in to continue. Your session may have expired.");
                },
                OnForbidden = context => WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
                    "You are not allowed to do this.", "Your role does not allow this action."),
            };
        }
    }

    // Same writer as every other error, so media type (application/problem+json), traceId and code match.
    private static async Task WriteProblemAsync(HttpContext http, int status, string code, string title, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions[ProblemDetailsSetup.CodeKey] = code;
        http.Response.StatusCode = status;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }

    /// <summary>Marks the endpoints that need the cookie in Swagger.</summary>
    private sealed class CookieAuthOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (!metadata.OfType<IAuthorizeData>().Any() || metadata.OfType<IAllowAnonymous>().Any()) return;

            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "cookieAuth" } }] = [],
            });
        }
    }
}
