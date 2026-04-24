using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

public sealed class TradingDayOptionsValidator : IValidateOptions<TradingDayOptions>
{
    public ValidateOptionsResult Validate(string? name, TradingDayOptions options)
    {
        var errors = new List<string>();

        if (options.RolloverMinuteOfDayUtc is < 0 or >= 1440)
            errors.Add("TradingDayOptions.RolloverMinuteOfDayUtc must be in [0, 1439].");

        if (options.BrokerSnapshotBoundaryToleranceMinutes is < 0 or > 1440)
            errors.Add("TradingDayOptions.BrokerSnapshotBoundaryToleranceMinutes must be in [0, 1440].");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
