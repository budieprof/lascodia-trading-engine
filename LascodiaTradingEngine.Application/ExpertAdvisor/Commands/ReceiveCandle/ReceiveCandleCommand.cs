using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandle;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Receives a single OHLCV candle from a MetaTrader 5 Expert Advisor instance and upserts it
/// into the <see cref="Domain.Entities.Candle"/> table.
///
/// This is the real-time candle ingestion endpoint — the EA calls it each time a candle updates
/// (forming candle) or closes (completed candle). Unlike <see cref="ReceiveCandleBackfill"/>,
/// which bulk-inserts historical bars, this command handles one candle at a time so updates to
/// the currently forming bar can overwrite the previous snapshot in-place.
///
/// Processing pipeline:
///   1. Anomaly detection — validates OHLC consistency (e.g. High ≥ Open/Close ≥ Low) via
///      <see cref="IMarketDataAnomalyDetector"/>. Invalid candles are quarantined in the
///      <see cref="Domain.Entities.MarketDataAnomaly"/> table and rejected.
///   2. Timestamp normalisation — snaps the EA-reported timestamp to the timeframe boundary
///      (e.g. a M5 candle at 10:03:17 → 10:00:00) to prevent near-duplicate rows caused by
///      sub-second EA timing drift.
///   3. Upsert — looks up an existing candle by (Symbol, Timeframe, NormalisedTimestamp). If
///      found, updates OHLCV + IsClosed in place; otherwise inserts a new row.
///
/// The EA sends both forming (IsClosed = false) and completed (IsClosed = true) candles:
///   • Forming candles are overwritten on every tick/timer cycle so the engine always has the
///     latest partial bar for indicator calculations.
///   • Once the bar closes, the EA sends a final update with IsClosed = true, which downstream
///     workers (StrategyWorker, RegimeDetectionWorker, etc.) treat as the authoritative bar.
///
/// Returns the candle entity ID on success ("00"), or a validation error ("-11") if the
/// anomaly detector rejects the candle.
/// </summary>
public class ReceiveCandleCommand : IRequest<ResponseData<long>>
{
    /// <summary>EA instance sending this candle — used for anomaly quarantine attribution, not ownership-checked here.</summary>
    public required string InstanceId { get; set; }

    /// <summary>Instrument symbol as reported by MT5 (e.g. "EURUSD"). Normalised to upper-case during processing.</summary>
    public required string Symbol     { get; set; }

    /// <summary>Bar timeframe as a string (e.g. "M1", "M5", "H1", "D1"). Parsed to <see cref="Timeframe"/> enum in the handler.</summary>
    public required string Timeframe  { get; set; }

    /// <summary>Opening price of the candle (first tick price when the bar opened).</summary>
    public decimal  Open      { get; set; }

    /// <summary>Highest price reached during the candle's timeframe.</summary>
    public decimal  High      { get; set; }

    /// <summary>Lowest price reached during the candle's timeframe.</summary>
    public decimal  Low       { get; set; }

    /// <summary>Closing price — last tick price if the candle is still forming, or final price if IsClosed is true.</summary>
    public decimal  Close     { get; set; }

    /// <summary>Tick volume (number of ticks) during the candle's timeframe. Zero is valid for illiquid sessions.</summary>
    public decimal  Volume    { get; set; }

    /// <summary>Candle open time as reported by MT5. Normalised to the timeframe boundary during processing.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// True when the bar has completed (no further updates expected). False for the currently forming bar.
    /// Downstream workers use this flag to distinguish partial bars from authoritative completed bars.
    /// </summary>
    public bool     IsClosed  { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Structural validation for incoming candle data. Runs in the MediatR
/// <see cref="ValidationBehaviour{TRequest,TResponse}"/> pipeline before the handler.
///
/// This validator checks data-type / range constraints only — it does NOT validate OHLC
/// logical consistency (e.g. High ≥ Open). That deeper check is performed by
/// <see cref="IMarketDataAnomalyDetector"/> in the handler, which can quarantine the candle
/// with full context rather than rejecting the entire request at the pipeline level.
/// </summary>
public class ReceiveCandleCommandValidator : AbstractValidator<ReceiveCandleCommand>
{
    public ReceiveCandleCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            // MT5 symbols are typically 6 chars (e.g. "EURUSD") but some brokers add suffixes (e.g. "EURUSD.m")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.Timeframe)
            .NotEmpty().WithMessage("Timeframe cannot be empty")
            // Validate against the Timeframe enum to catch typos like "m1" vs "M1" (case-insensitive parse)
            .Must(t => Enum.TryParse<Timeframe>(t, ignoreCase: true, out _))
            .WithMessage("Timeframe must be one of: M1, M5, M15, H1, H4, D1");

        // OHLC must be positive — zero or negative prices indicate corrupt data
        RuleFor(x => x.Open).GreaterThan(0).WithMessage("Open must be greater than zero");
        RuleFor(x => x.High).GreaterThan(0).WithMessage("High must be greater than zero");
        RuleFor(x => x.Low).GreaterThan(0).WithMessage("Low must be greater than zero");
        RuleFor(x => x.Close).GreaterThan(0).WithMessage("Close must be greater than zero");
        // Volume can be zero during illiquid sessions (e.g. weekends, holidays) but never negative
        RuleFor(x => x.Volume).GreaterThanOrEqualTo(0).WithMessage("Volume cannot be negative");
        RuleFor(x => x.Timestamp).NotEmpty().WithMessage("Timestamp cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Processes a single candle received from an EA instance and upserts it into the Candle table.
///
/// Execution flow:
///   1. Anomaly detection — validates OHLC logical consistency (High ≥ max(Open, Close),
///      Low ≤ min(Open, Close), etc.) via <see cref="IMarketDataAnomalyDetector"/>. Rejected
///      candles are persisted as <see cref="Domain.Entities.MarketDataAnomaly"/> quarantine
///      records for later review and are NOT written to the Candle table.
///   2. Timestamp normalisation — snaps the raw timestamp to the nearest timeframe boundary
///      using <see cref="ReceiveCandleBackfill.ReceiveCandleBackfillCommandHandler.NormalizeTimestamp"/>.
///      This is essential because the EA may fire candle events a few hundred milliseconds
///      after the actual bar open, causing the same bar to appear as two different rows
///      without normalisation.
///   3. Upsert logic — queries for an existing candle matching (Symbol, Timeframe, NormalisedTimestamp):
///      • If found → updates OHLCV and IsClosed in place (forming bar overwrite).
///      • If not found → inserts a new row.
///      This upsert pattern means the currently forming bar is continuously refreshed until
///      the EA sends the final IsClosed = true update.
///
/// Important: this handler does NOT publish integration events. Downstream consumers
/// (StrategyWorker, etc.) react to <see cref="Common.Events.PriceUpdatedIntegrationEvent"/>
/// from tick ingestion, not from candle writes. Candle data is consumed on-demand when
/// workers need historical bars for indicator calculation.
///
/// Returns the candle entity ID on success ("00"), or "-11" if the anomaly detector rejects it.
/// </summary>
public class ReceiveCandleCommandHandler : IRequestHandler<ReceiveCandleCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IMarketDataAnomalyDetector _anomalyDetector;
    private readonly ILogger<ReceiveCandleCommandHandler> _logger;

    public ReceiveCandleCommandHandler(
        IWriteApplicationDbContext context,
        IMarketDataAnomalyDetector anomalyDetector,
        ILogger<ReceiveCandleCommandHandler> logger)
    {
        _context = context;
        _anomalyDetector = anomalyDetector;
        _logger = logger;
    }

    public async Task<ResponseData<long>> Handle(ReceiveCandleCommand request, CancellationToken cancellationToken)
    {
        var dbContext  = _context.GetDbContext();
        // Canonicalize at ingestion so every downstream lookup (CurrencyPair,
        // SymbolSpec, RiskChecker) hits the same key regardless of broker suffix
        // ("EURUSD.a" / "EURUSD.i" / "EUR/USD" / "EUR_USD" all → "EURUSD").
        var symbol    = SymbolNormalizer.Normalize(request.Symbol);
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        // ── Step 1: Candle quality validation ────────────────────────────────
        // Checks OHLC logical consistency (e.g. High must be the highest value, Low the lowest).
        // This catches corrupt data from MT5 feed glitches, broker disconnections mid-bar, or
        // EA bugs that swap fields. Rejected candles are quarantined, not silently dropped.
        var qualityResult = _anomalyDetector.ValidateCandle(
            request.Open, request.High, request.Low, request.Close,
            (long)request.Volume, request.Timestamp, null);

        if (!qualityResult.IsValid)
        {
            _logger.LogWarning(
                "Candle quality check failed for {Symbol} at {Timestamp}: {Reason}",
                request.Symbol, request.Timestamp, qualityResult.Description);

            // Persist a quarantine record so the rejected candle can be reviewed later.
            // This creates an audit trail of bad data from specific EA instances, which helps
            // diagnose systemic feed issues (e.g. a broker consistently sending malformed H4 bars).
            await dbContext.Set<Domain.Entities.MarketDataAnomaly>().AddAsync(new Domain.Entities.MarketDataAnomaly
            {
                AnomalyType = Domain.Enums.MarketDataAnomalyType.InvalidOhlc,
                Symbol = symbol,
                InstanceId = request.InstanceId,
                AnomalousValue = request.Close,
                ExpectedValue = null,
                Description = $"Candle rejected: {qualityResult.Description} (O={request.Open}, H={request.High}, L={request.Low}, C={request.Close}, V={request.Volume})",
                WasQuarantined = true,
                DetectedAt = DateTime.UtcNow,
            }, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return ResponseData<long>.Init(0, false, $"Candle quality check failed: {qualityResult.Description}", "-11");
        }

        // ── Step 2: Timestamp normalisation ──────────────────────────────────
        // Snap to the timeframe boundary (e.g. M5 candle at 10:03:17 → 10:00:00).
        // Without this, the same bar could be stored as two rows if the EA sends the
        // forming update at 10:00:00.100 and the close at 10:04:59.900 — after rounding
        // both map to 10:00:00, enabling the upsert to work correctly.
        var normalizedTs = ReceiveCandleBackfill.ReceiveCandleBackfillCommandHandler
            .NormalizeTimestamp(request.Timestamp, timeframe);

        // ── Step 3: Upsert — update existing or insert new ──────────────────
        // Look up by the natural key (Symbol + Timeframe + NormalisedTimestamp).
        // The soft-delete filter ensures we don't resurrect manually deleted candles.
        var existing = await dbContext
            .Set<Domain.Entities.Candle>()
            .FirstOrDefaultAsync(
                x => x.Symbol == symbol
                  && x.Timeframe == timeframe
                  && x.Timestamp == normalizedTs
                  && !x.IsDeleted,
                cancellationToken);

        if (existing is not null)
        {
            // Update the forming bar in place — this happens many times per bar lifetime
            // (every tick or EA timer cycle) until the bar closes.
            existing.Open     = request.Open;
            existing.High     = request.High;
            existing.Low      = request.Low;
            existing.Close    = request.Close;
            existing.Volume   = request.Volume;
            existing.IsClosed = request.IsClosed;

            await _context.SaveChangesAsync(cancellationToken);
            return ResponseData<long>.Init(existing.Id, true, "Updated", "00");
        }

        // First time seeing this bar — insert a new row.
        // For forming candles this is the initial snapshot; for closed candles (e.g. from a
        // delayed EA that missed the forming phase) this is the final record.
        var entity = new Domain.Entities.Candle
        {
            Symbol    = symbol,
            Timeframe = timeframe,
            Open      = request.Open,
            High      = request.High,
            Low       = request.Low,
            Close     = request.Close,
            Volume    = request.Volume,
            Timestamp = normalizedTs,
            IsClosed  = request.IsClosed,
        };

        await dbContext.Set<Domain.Entities.Candle>().AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
