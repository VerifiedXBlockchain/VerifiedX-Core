using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Reflection;

namespace VerifiedXCore
{
    public class ExcludeControllersFeatureProvider<T> : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            // Remove all controllers except the specified one (ValidatorController)
            feature.Controllers.Clear();
            var controllerType = typeof(T).Assembly.ExportedTypes.FirstOrDefault(t => t == typeof(T));
            if (controllerType != null)
            {
                feature.Controllers.Add(controllerType.GetTypeInfo());
            }
        }
    }

    /// <summary>
    /// VX-03: removes controller <typeparamref name="T"/> from a host's controller set (the inverse of
    /// <see cref="ExcludeControllersFeatureProvider{T}"/>, which keeps ONLY <typeparamref name="T"/>).
    /// Register after the default provider so it runs on the populated feature.
    /// </summary>
    public class WithoutControllerFeatureProvider<T> : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var target = typeof(T).GetTypeInfo();
            foreach (var c in feature.Controllers.Where(c => c == target).ToList())
                feature.Controllers.Remove(c);
        }
    }
}
