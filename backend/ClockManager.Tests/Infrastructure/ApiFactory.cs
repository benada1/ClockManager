using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// Test host for the API. Adds <see cref="TestOnlyController"/> (from this test assembly) so tests can
/// trigger errors without any throwing endpoint in production code.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(TestOnlyController).Assembly));
    }
}
