using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="CalibrationSnapshotOptions"/> at startup.</summary>
public class CalibrationSnapshotOptionsValidator : IValidateOptions<CalibrationSnapshotOptions>
{
    public ValidateOptionsResult Validate(string? name, CalibrationSnapshotOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelayMinutes < 0)
            errors.Add("CalibrationSnapshotOptions.InitialDelayMinutes must be >= 0.");
        if (o.PollIntervalHours is < 1 or > 168)
            errors.Add("CalibrationSnapshotOptions.PollIntervalHours must be in [1, 168].");
        if (o.BackfillMonths is < 1 or > 120)
            errors.Add("CalibrationSnapshotOptions.BackfillMonths must be in [1, 120].");
        if (o.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("CalibrationSnapshotOptions.PollJitterSeconds must be in [0, 86400].");
        if (o.FailureBackoffCapShift is < 0 or > 16)
            errors.Add("CalibrationSnapshotOptions.FailureBackoffCapShift must be in [0, 16].");
        if (o.CycleLockTimeoutSeconds is < 0 or > 300)
            errors.Add("CalibrationSnapshotOptions.CycleLockTimeoutSeconds must be in [0, 300].");
        if (o.FleetSystemicConsecutiveFailureCycles < 1)
            errors.Add("CalibrationSnapshotOptions.FleetSystemicConsecutiveFailureCycles must be >= 1.");
        if (o.StalenessAlertHours < 1)
            errors.Add("CalibrationSnapshotOptions.StalenessAlertHours must be >= 1.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
