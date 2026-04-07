using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Utilities;

internal static class RiskContextValidator
{
    internal static bool Validate(RiskCheckContext context, ILogger logger)
    {
        bool valid = true;

        if (context.Account is null)
        {
            logger.LogError("RiskContextValidator: Account is null");
            valid = false;
        }

        if (context.Profile is null)
        {
            logger.LogError("RiskContextValidator: RiskProfile is null");
            valid = false;
        }

        if (context.DailyStartBalance <= 0)
        {
            logger.LogWarning("RiskContextValidator: DailyStartBalance is {Balance} — defaulting checks may degrade",
                context.DailyStartBalance);
        }

        if (context.SymbolSpec is null)
        {
            logger.LogWarning("RiskContextValidator: SymbolSpec is null — margin/contract-size checks will be skipped");
        }

        return valid;
    }
}
