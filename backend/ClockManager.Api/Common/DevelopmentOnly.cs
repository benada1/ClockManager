using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ClockManager.Api.Common;

/// <summary>
/// Marks a controller that exists only in the Development environment. Outside Development the controller is
/// removed from discovery entirely, so its routes are not mapped (404) and do not appear in Swagger (D-013).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class DevelopmentOnlyAttribute : Attribute;

public static class DevelopmentOnlyMvcBuilderExtensions
{
    /// <summary>Removes every <see cref="DevelopmentOnlyAttribute"/> controller unless <paramref name="environment"/> is Development.</summary>
    public static IMvcBuilder ExcludeDevelopmentOnlyControllers(this IMvcBuilder builder, IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            // Added after MVC's own ControllerFeatureProvider, so it sees (and prunes) the discovered list.
            builder.ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new RemoveDevelopmentOnlyControllers()));
        }
        return builder;
    }

    private sealed class RemoveDevelopmentOnlyControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(c => c.IsDefined(typeof(DevelopmentOnlyAttribute), inherit: true)).ToList())
            {
                feature.Controllers.Remove(controller);
            }
        }
    }
}
