using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Common.Attributes;

/// <summary>
/// Marks a class for automatic DI registration via assembly scanning.
/// The scanner registers the class against its <see cref="ServiceType"/> (or the first
/// implemented interface if not specified) with the given <see cref="Lifetime"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class RegisterServiceAttribute : Attribute
{
    public ServiceLifetime Lifetime { get; }
    public Type? ServiceType { get; }

    public RegisterServiceAttribute(ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        Lifetime = lifetime;
    }

    public RegisterServiceAttribute(ServiceLifetime lifetime, Type serviceType)
    {
        Lifetime = lifetime;
        ServiceType = serviceType;
    }
}

/// <summary>
/// Marks a class for automatic keyed DI registration via assembly scanning.
/// The scanner calls <c>AddKeyed{Lifetime}</c> with the specified <see cref="Key"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class RegisterKeyedServiceAttribute : Attribute
{
    public ServiceLifetime Lifetime { get; }
    public Type ServiceType { get; }
    public object Key { get; }

    public RegisterKeyedServiceAttribute(Type serviceType, object key, ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        ServiceType = serviceType;
        Key = key;
        Lifetime = lifetime;
    }
}
