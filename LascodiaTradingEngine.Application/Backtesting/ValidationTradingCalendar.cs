using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

public readonly record struct ValidationCandleSeriesAssessment(
    bool IsValid,
    string Issue);

public interface IValidationTradingCalendar
{
    Task<(string? TradingHoursJson, HashSet<DateTime> HolidayDates)> LoadAsync(
        DbContext writeDb,
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct);
}

public interface IValidationCandleSeriesGuard
{
    Task<ValidationCandleSeriesAssessment> ValidateAsync(
        DbContext writeDb,
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        DateTime fromUtc,
        DateTime toUtc,
        int maxGapMultiplier,
        CancellationToken ct);
}

internal sealed class ValidationTradingCalendar : IValidationTradingCalendar
{
    public async Task<(string? TradingHoursJson, HashSet<DateTime> HolidayDates)> LoadAsync(
        DbContext writeDb,
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var pairInfo = await writeDb.Set<CurrencyPair>()
            .AsNoTracking()
            .FirstOrDefaultAsync(pair => pair.Symbol == symbol && !pair.IsDeleted, ct);
        var strategyCurrencies = OptimizationRunMetadataService.ResolveStrategyCurrencies(symbol, pairInfo);

        var holidayQuery = writeDb.Set<EconomicEvent>()
            .AsNoTracking()
            .Where(e => e.Impact == EconomicImpact.Holiday
                     && e.ScheduledAt >= fromUtc
                     && e.ScheduledAt <= toUtc
                     && !e.IsDeleted);
        if (strategyCurrencies.Count > 0)
            holidayQuery = holidayQuery.Where(e => strategyCurrencies.Contains(e.Currency));

        var holidayDates = new HashSet<DateTime>(await holidayQuery
            .Select(e => e.ScheduledAt.Date)
            .Distinct()
            .ToListAsync(ct));

        return (pairInfo?.TradingHoursJson, holidayDates);
    }
}

internal sealed class ValidationCandleSeriesGuard : IValidationCandleSeriesGuard
{
    private readonly IValidationTradingCalendar _calendar;

    public ValidationCandleSeriesGuard(IValidationTradingCalendar calendar)
    {
        _calendar = calendar;
    }

    public async Task<ValidationCandleSeriesAssessment> ValidateAsync(
        DbContext writeDb,
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        DateTime fromUtc,
        DateTime toUtc,
        int maxGapMultiplier,
        CancellationToken ct)
    {
        var (tradingHoursJson, holidayDates) = await _calendar.LoadAsync(
            writeDb,
            symbol,
            fromUtc,
            toUtc,
            ct);

        return ValidationCandleSeriesInspector.TryValidate(
            candles,
            timeframe,
            maxGapMultiplier,
            holidayDates,
            tradingHoursJson,
            out string issue)
            ? new ValidationCandleSeriesAssessment(true, string.Empty)
            : new ValidationCandleSeriesAssessment(false, issue);
    }
}
