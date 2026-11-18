using System.Reflection;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace ClockManager.Tests.Auth;

public sealed class UserSecretsTests
{
    /// <summary>The README's <c>dotnet user-secrets set ...</c> fails without a UserSecretsId (F1).</summary>
    [Fact]
    public void The_api_assembly_has_a_user_secrets_id()
    {
        var id = typeof(Program).Assembly.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;

        Assert.True(Guid.TryParse(id, out _), $"UserSecretsId: '{id}'");
    }
}
