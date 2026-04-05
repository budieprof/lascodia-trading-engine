using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Bridge.Options;

/// <summary>
/// Validates <see cref="BridgeOptions"/> at startup. When <c>Enabled</c> is true, the TCP
/// bridge must have valid port and connection limits.
/// </summary>
public class BridgeOptionsValidator : IValidateOptions<BridgeOptions>
{
    public ValidateOptionsResult Validate(string? name, BridgeOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;

        var errors = new List<string>();

        if (o.Port is < 1 or > 65535)
            errors.Add("BridgeOptions.Port must be between 1 and 65535");
        if (o.MaxTotalConnections < 1)
            errors.Add("BridgeOptions.MaxTotalConnections must be >= 1");
        if (o.MaxConnectionsPerAccount < 1)
            errors.Add("BridgeOptions.MaxConnectionsPerAccount must be >= 1");
        if (o.TcpBacklog < 1)
            errors.Add("BridgeOptions.TcpBacklog must be >= 1");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
