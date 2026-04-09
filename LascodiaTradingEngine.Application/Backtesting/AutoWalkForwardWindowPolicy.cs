using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

public readonly record struct AutoWalkForwardWindowSelection(
    int InSampleDays,
    int OutOfSampleDays,
    string? SkipReason = null);

public interface IAutoWalkForwardWindowPolicy
{
    bool TryCreateWindow(
        BacktestRun run,
        BacktestWorkerSettings settings,
        out AutoWalkForwardWindowSelection selection);
}

internal sealed class AutoWalkForwardWindowPolicy : IAutoWalkForwardWindowPolicy
{
    public bool TryCreateWindow(
        BacktestRun run,
        BacktestWorkerSettings settings,
        out AutoWalkForwardWindowSelection selection)
    {
        var totalDuration = run.ToDate - run.FromDate;
        int totalWholeDays = (int)Math.Floor(totalDuration.TotalDays);
        if (totalWholeDays < 2)
        {
            selection = new AutoWalkForwardWindowSelection(0, 0, "backtest window is shorter than 2 whole days");
            return false;
        }

        decimal inSampleRatio = settings.AutoWalkForwardInSampleRatio;
        decimal outOfSampleRatio = settings.AutoWalkForwardOutOfSampleRatio;
        decimal ratioSum = inSampleRatio + outOfSampleRatio;
        if (ratioSum <= 0m)
        {
            selection = new AutoWalkForwardWindowSelection(0, 0, "auto walk-forward ratios are not positive");
            return false;
        }

        inSampleRatio /= ratioSum;
        outOfSampleRatio /= ratioSum;

        int inSampleDays = Math.Max(settings.AutoWalkForwardMinInSampleDays, (int)Math.Round(totalWholeDays * inSampleRatio));
        int outOfSampleDays = Math.Max(settings.AutoWalkForwardMinOutOfSampleDays, (int)Math.Round(totalWholeDays * outOfSampleRatio));
        int timeframeFloor = GetTimeframeMinimumDays(run.Timeframe);
        inSampleDays = Math.Max(inSampleDays, timeframeFloor * 2);
        outOfSampleDays = Math.Max(outOfSampleDays, timeframeFloor);

        if (inSampleDays + outOfSampleDays > totalWholeDays)
        {
            int reducibleInSample = Math.Max(0, inSampleDays - Math.Max(settings.AutoWalkForwardMinInSampleDays, timeframeFloor * 2));
            int overflow = inSampleDays + outOfSampleDays - totalWholeDays;
            int trimInSample = Math.Min(reducibleInSample, overflow);
            inSampleDays -= trimInSample;
            overflow -= trimInSample;

            if (overflow > 0)
            {
                int reducibleOutOfSample = Math.Max(0, outOfSampleDays - Math.Max(settings.AutoWalkForwardMinOutOfSampleDays, timeframeFloor));
                int trimOutOfSample = Math.Min(reducibleOutOfSample, overflow);
                outOfSampleDays -= trimOutOfSample;
                overflow -= trimOutOfSample;
            }
        }

        if (inSampleDays <= 0 || outOfSampleDays <= 0)
        {
            selection = new AutoWalkForwardWindowSelection(0, 0, "derived walk-forward window has non-positive length");
            return false;
        }

        if (inSampleDays + outOfSampleDays > totalWholeDays)
        {
            selection = new AutoWalkForwardWindowSelection(0, 0, "backtest window is too short for the configured walk-forward minimums");
            return false;
        }

        selection = new AutoWalkForwardWindowSelection(inSampleDays, outOfSampleDays);
        return true;
    }

    private static int GetTimeframeMinimumDays(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1 => 1,
        Timeframe.M5 => 1,
        Timeframe.M15 => 2,
        Timeframe.H1 => 3,
        Timeframe.H4 => 7,
        Timeframe.D1 => 30,
        _ => 3
    };
}
