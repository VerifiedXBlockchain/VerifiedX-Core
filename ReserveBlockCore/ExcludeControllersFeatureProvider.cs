using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using System.Reflection;

namespace ReserveBlockCore
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
}
