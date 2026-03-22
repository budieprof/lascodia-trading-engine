using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Common.Attributes;

public static class ServiceRegistrationExtensions
{
    /// <summary>
    /// Scans <paramref name="assembly"/> for classes decorated with
    /// <see cref="RegisterServiceAttribute"/> or <see cref="RegisterKeyedServiceAttribute"/>
    /// and registers them in the DI container.
    /// </summary>
    public static IServiceCollection AutoRegisterAttributedServices(
        this IServiceCollection services,
        Assembly assembly)
    {
        var types = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false });

        foreach (var type in types)
        {
            foreach (var attr in type.GetCustomAttributes<RegisterServiceAttribute>())
            {
                var serviceType = attr.ServiceType
                    ?? type.GetInterfaces().FirstOrDefault()
                    ?? type;

                services.Add(new ServiceDescriptor(serviceType, type, attr.Lifetime));
            }

            foreach (var attr in type.GetCustomAttributes<RegisterKeyedServiceAttribute>())
            {
                services.Add(new ServiceDescriptor(attr.ServiceType, attr.Key, type, attr.Lifetime));
            }
        }

        return services;
    }
}
