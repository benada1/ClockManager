using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.Identity;

/// <summary>Section <c>Auth</c>. The signing key comes from user-secrets or an environment variable, never from git.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const int MinSigningKeyLength = 32;

    /// <summary>HS256 key (at least 32 characters). Development makes a random one when missing; elsewhere it is required.</summary>
    public string? SigningKey { get; set; }

    [Range(1, 24)]
    public int TokenLifetimeHours { get; set; } = 8;

    [Required, RegularExpression("^[A-Za-z0-9_-]{1,64}$")]
    public string CookieName { get; set; } = "clockmanager_auth";
}

/// <summary>Refuses a missing or short key at startup, with a message that says how to set it.</summary>
internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            return ValidateOptionsResult.Fail(
                "Auth:SigningKey is missing. Set it with 'dotnet user-secrets set Auth:SigningKey <random 32+ characters>' "
                + "or the environment variable 'Auth__SigningKey'. Only Development generates one.");
        }

        return options.SigningKey.Length < AuthOptions.MinSigningKeyLength
            ? ValidateOptionsResult.Fail($"Auth:SigningKey must be at least {AuthOptions.MinSigningKeyLength} characters.")
            : ValidateOptionsResult.Success;
    }
}
