using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

// ═══════════════════════════════════════════════════════════════════════════════
// FXCM Order Executor — production implementation
// ═══════════════════════════════════════════════════════════════════════════════
//
// FXCM REST API docs:
//   https://fxcm-api.readthedocs.io/en/latest/restapi.html
//   https://fxcm-rest.readthedocs.io/en/latest/socketrestapispecs.html
//
// Authentication:
//   Managed by FxcmSessionManager which handles the Socket.IO handshake
//   and provides pre-authenticated HttpClient instances.
//
// Endpoint reference (all trading endpoints use POST):
//   POST /trading/open_trade          — market orders
//   POST /trading/create_entry_order  — limit/stop/entry orders
//   POST /trading/close_trade         — close open positions
//   POST /trading/delete_order        — cancel pending orders
//   POST /trading/change_trade_stop_limit — modify SL/TP on open trades
//   GET  /trading/get_model           — snapshot of Account, OpenPosition, Offer tables
//
// orderId vs tradeId:
//   open_trade returns only an orderId. The tradeId (needed for close/modify)
//   is resolved asynchronously via FxcmSessionManager.ResolveTradeIdAsync().

[RegisterKeyedService(typeof(IBrokerOrderExecutor), BrokerType.Fxcm)]
public sealed class FxcmOrderExecutor : IBrokerOrderExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IFxcmSessionManager _session;
    private readonly ILogger<FxcmOrderExecutor> _logger;

    public FxcmOrderExecutor(
        IFxcmSessionManager session,
        ILogger<FxcmOrderExecutor> logger)
    {
        _session = session;
        _logger  = logger;
    }

    // ── Submit Order ─────────────────────────────────────────────────────────

    public async Task<BrokerOrderResult> SubmitOrderAsync(Order order, CancellationToken cancellationToken)
    {
        var symbol = ToFxcmSymbol(order.Symbol);
        var amount = ToFxcmAmount(order.Quantity);

        // Idempotency guard: prevent duplicate submissions for the same Order.Id.
        // If a prior call already submitted this order and received a broker ID, return it.
        var existingBrokerId = _session.GetInFlightBrokerOrderId(order.Id);
        if (existingBrokerId != null)
        {
            _logger.LogWarning(
                "FXCM SubmitOrder: order {OrderId} already submitted as brokerOrderId={BrokerOrderId} — returning existing result",
                order.Id, existingBrokerId);
            return new BrokerOrderResult(true, existingBrokerId, null, null,
                "Duplicate submission prevented — returning existing broker order ID");
        }

        if (!_session.TryMarkOrderInFlight(order.Id))
        {
            _logger.LogWarning(
                "FXCM SubmitOrder: order {OrderId} is already in-flight — rejecting duplicate submission",
                order.Id);
            return Fail(null, "Order is already being submitted (in-flight)");
        }

        try
        {
            var accountId = await _session.GetAccountIdAsync(cancellationToken);
            var isBuy     = order.OrderType == OrderType.Buy;

            _logger.LogInformation(
                "FXCM SubmitOrder: symbol={Symbol} side={Side} amount={Amount} execType={ExecType}",
                symbol, isBuy ? "Buy" : "Sell", amount, order.ExecutionType);

            BrokerOrderResult result;

            if (order.ExecutionType == ExecutionType.Market)
                result = await SubmitMarketOrderAsync(accountId, order, symbol, amount, isBuy, cancellationToken);
            else
                result = await SubmitEntryOrderAsync(accountId, order, symbol, amount, isBuy, cancellationToken);

            if (result.Success && result.BrokerOrderId != null)
                _session.CompleteInFlightOrder(order.Id, result.BrokerOrderId);
            else
                _session.ClearInFlightOrder(order.Id);

            return result;
        }
        catch (OperationCanceledException)
        {
            _session.ClearInFlightOrder(order.Id);
            throw;
        }
        catch (Exception ex)
        {
            _session.ClearInFlightOrder(order.Id);
            _logger.LogError(ex, "FXCM SubmitOrder exception for {Symbol}", symbol);
            return Fail(null, $"FXCM exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback market range in pips when no EngineConfig entry exists.
    /// Configurable at runtime via EngineConfig key "Fxcm:MarketRangePips".
    /// When > 0, uses "MarketRange" order_type (rejects fills outside range).
    /// When 0, uses "AtMarket" (accept any fill).
    /// </summary>
    private const int FallbackMarketRangePips = 2;
    private const string CK_MarketRangePips = "Fxcm:MarketRangePips";

    private const int FallbackMarketRangeMaxRetries = 2;
    private const string CK_MarketRangeMaxRetries = "Fxcm:MarketRangeMaxRetries";

    private const int FallbackMarketRangeWidenPips = 3;
    private const string CK_MarketRangeWidenPips = "Fxcm:MarketRangeWidenPips";

    private const int FallbackClosePriceMaxAttempts = 8;
    private const string CK_ClosePriceMaxAttempts = "Fxcm:ClosePriceMaxAttempts";

    private const int FallbackClosePriceDelayMs = 500;
    private const string CK_ClosePriceDelayMs = "Fxcm:ClosePriceDelayMs";

    private const int FallbackCloseMaxRetries = 2;
    private const string CK_CloseMaxRetries = "Fxcm:CloseMaxRetries";

    private const int FallbackCloseRetryDelayMs = 500;
    private const string CK_CloseRetryDelayMs = "Fxcm:CloseRetryDelayMs";

    private async Task<BrokerOrderResult> SubmitMarketOrderAsync(
        string accountId, Order order,
        string symbol, int amount, bool isBuy, CancellationToken ct)
    {
        // POST /trading/open_trade
        // "MarketRange" rejects fills outside the specified pip range (slippage control).
        // "AtMarket" accepts any fill price. Read from EngineConfig for hot-reload.
        var rangePips = await _session.GetConfigIntAsync(CK_MarketRangePips, FallbackMarketRangePips, ct);
        var maxRetries = await _session.GetConfigIntAsync(CK_MarketRangeMaxRetries, FallbackMarketRangeMaxRetries, ct);
        var widenPips = await _session.GetConfigIntAsync(CK_MarketRangeWidenPips, FallbackMarketRangeWidenPips, ct);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            // Acquire a fresh client each attempt so the auth header stays valid
            // even if a prior attempt triggered session invalidation (401/403).
            using var client = await _session.GetAuthenticatedClientAsync(ct);

            var useMarketRange = rangePips > 0;

            var form = new Dictionary<string, string>
            {
                ["account_id"]    = accountId,
                ["symbol"]        = symbol,
                ["is_buy"]        = isBuy.ToString().ToLowerInvariant(),
                ["amount"]        = amount.ToString(CultureInfo.InvariantCulture),
                ["order_type"]    = useMarketRange ? "MarketRange" : "AtMarket",
                ["time_in_force"] = "GTC",
                ["at_market"]     = rangePips.ToString(CultureInfo.InvariantCulture)
            };

            if (order.StopLoss.HasValue)
            {
                form["stop"]       = order.StopLoss.Value.ToString(CultureInfo.InvariantCulture);
                form["is_in_pips"] = "false";
            }

            if (order.TakeProfit.HasValue)
            {
                form["limit"]      = order.TakeProfit.Value.ToString(CultureInfo.InvariantCulture);
                form["is_in_pips"] = "false";
            }

            if (order.TrailingStopEnabled && order.TrailingStopValue.HasValue)
            {
                form["trailing_step"] = order.TrailingStopValue.Value.ToString(CultureInfo.InvariantCulture);
            }

            var (success, body, statusCode) = await PostFormAsync(client, "/trading/open_trade", form, ct);

            if (!success)
            {
                // On MarketRange rejection (price moved outside range), widen and retry
                if (useMarketRange && attempt < maxRetries && IsMarketRangeRejection(body))
                {
                    rangePips += widenPips;
                    _logger.LogWarning(
                        "FXCM MarketRange rejected for {Symbol} — widening to {RangePips} pips (attempt {Attempt}/{MaxRetries})",
                        symbol, rangePips, attempt + 1, maxRetries);
                    continue;
                }

                return Fail(null, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");
            }

            // Response: {"response":{"executed":true},"data":{"type":0,"orderId":81712802}}
            var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);

            if (result?.Response?.Executed != true)
            {
                // Retry on MarketRange rejection returned as executed=false with an error
                if (useMarketRange && attempt < maxRetries && IsMarketRangeRejection(result?.Response?.Error))
                {
                    rangePips += widenPips;
                    _logger.LogWarning(
                        "FXCM MarketRange not executed for {Symbol} — widening to {RangePips} pips (attempt {Attempt}/{MaxRetries})",
                        symbol, rangePips, attempt + 1, maxRetries);
                    continue;
                }

                return Fail(null, result?.Response?.Error ?? "Market order not executed");
            }

            var orderId = result.Data?.OrderId?.ToString();

            // Resolve tradeId and fill price. FXCM returns only orderId synchronously;
            // the tradeId appears in OpenPosition table after the fill propagates.
            string? tradeId    = null;
            decimal? filledPrice = null;

            if (orderId != null)
            {
                var resolveWatch = Stopwatch.StartNew();
                tradeId = await _session.ResolveTradeIdAsync(orderId, ct);
                resolveWatch.Stop();

                _logger.LogInformation(
                    "FXCM ResolveTradeId for orderId={OrderId}: resolved={Resolved} elapsed={ElapsedMs}ms",
                    orderId, tradeId != null, resolveWatch.ElapsedMilliseconds);

                if (tradeId != null)
                    filledPrice = await GetOpenPriceByTradeIdAsync(client, tradeId, ct);
            }

            if (tradeId == null)
            {
                _logger.LogWarning(
                    "FXCM open_trade executed but tradeId could not be resolved for orderId={OrderId}. " +
                    "Close/modify operations will fail until the tradeId is resolved via socket update.",
                    orderId);
            }

            _logger.LogInformation(
                "FXCM open_trade success: orderId={OrderId} tradeId={TradeId} filledPrice={FilledPrice}",
                orderId, tradeId, filledPrice);

            return new BrokerOrderResult(
                Success: true,
                BrokerOrderId: tradeId ?? orderId,
                FilledPrice: filledPrice,
                FilledQuantity: order.Quantity,
                ErrorMessage: tradeId == null ? "TradeId not yet resolved; close/modify may require retry" : null);
        }

        return Fail(null, "FXCM MarketRange rejected after all retry attempts");
    }

    private static bool IsMarketRangeRejection(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;
        // FXCM returns "PRICE_RANGE" or "Price changed" style errors for MarketRange rejections
        return message.Contains("PRICE_RANGE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("price changed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("market range", StringComparison.OrdinalIgnoreCase)
            || message.Contains("requote", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BrokerOrderResult> SubmitEntryOrderAsync(
        string accountId, Order order,
        string symbol, int amount, bool isBuy, CancellationToken ct)
    {
        // POST /trading/create_entry_order
        using var client = await _session.GetAuthenticatedClientAsync(ct);

        var orderType = order.ExecutionType switch
        {
            ExecutionType.StopLimit => "RangeEntry",
            _                       => "Entry"
        };

        var form = new Dictionary<string, string>
        {
            ["account_id"]    = accountId,
            ["symbol"]        = symbol,
            ["is_buy"]        = isBuy.ToString().ToLowerInvariant(),
            ["rate"]          = order.Price.ToString(CultureInfo.InvariantCulture),
            ["amount"]        = amount.ToString(CultureInfo.InvariantCulture),
            ["order_type"]    = orderType,
            ["time_in_force"] = "GTC",
            ["is_in_pips"]    = "false"
        };

        if (order.StopLoss.HasValue)
            form["stop"] = order.StopLoss.Value.ToString(CultureInfo.InvariantCulture);

        if (order.TakeProfit.HasValue)
            form["limit"] = order.TakeProfit.Value.ToString(CultureInfo.InvariantCulture);

        if (order.TrailingStopEnabled && order.TrailingStopValue.HasValue)
            form["trailing_step"] = order.TrailingStopValue.Value.ToString(CultureInfo.InvariantCulture);

        var (success, body, statusCode) = await PostFormAsync(client, "/trading/create_entry_order", form, ct);

        if (!success)
            return Fail(null, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");

        // Response: {"response":{"executed":true},"data":{"type":0,"orderId":81716002}}
        var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);

        if (result?.Response?.Executed != true)
            return Fail(null, result?.Response?.Error ?? "Entry order not executed");

        var orderId = result.Data?.OrderId?.ToString();

        _logger.LogInformation(
            "FXCM create_entry_order success: orderId={OrderId} type={OrderType}", orderId, orderType);

        // Entry orders haven't filled yet — orderId is the broker reference.
        // The tradeId will be resolved when the order triggers and fills.
        return new BrokerOrderResult(
            Success: true,
            BrokerOrderId: orderId,
            FilledPrice: null,
            FilledQuantity: null,
            ErrorMessage: null);
    }

    // ── Cancel Order ─────────────────────────────────────────────────────────

    public async Task<BrokerOrderResult> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);

            _logger.LogInformation("FXCM CancelOrder: orderId={OrderId}", brokerOrderId);

            // POST /trading/delete_order
            var form = new Dictionary<string, string>
            {
                ["order_id"] = brokerOrderId
            };

            var (success, body, statusCode) = await PostFormAsync(
                client, "/trading/delete_order", form, cancellationToken);

            if (!success)
                return Fail(brokerOrderId, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");

            // Response: {"response":{"executed":true},"data":null}
            var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);

            if (result?.Response?.Executed != true)
                return Fail(brokerOrderId, result?.Response?.Error ?? "Cancel not executed");

            _session.RemoveMapping(brokerOrderId);

            _logger.LogInformation("FXCM delete_order success: orderId={OrderId}", brokerOrderId);
            return new BrokerOrderResult(true, brokerOrderId, null, null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FXCM CancelOrder exception for {OrderId}", brokerOrderId);
            return Fail(brokerOrderId, $"FXCM exception: {ex.Message}");
        }
    }

    // ── Modify Order (SL/TP) ─────────────────────────────────────────────────

    public async Task<BrokerOrderResult> ModifyOrderAsync(
        string brokerOrderId, decimal? stopLoss, decimal? takeProfit, CancellationToken cancellationToken)
    {
        if (!stopLoss.HasValue && !takeProfit.HasValue)
        {
            _logger.LogDebug(
                "FXCM ModifyOrder: no SL or TP provided for {BrokerOrderId} — nothing to modify",
                brokerOrderId);
            return new BrokerOrderResult(true, brokerOrderId, null, null, null);
        }

        try
        {
            // Determine whether this is an open trade (has a tradeId mapping) or a pending entry order.
            // - Open trades: use change_trade_stop_limit (separate calls for SL and TP)
            // - Pending orders: use change_order (single call for both SL and TP)
            // Try cached mapping first, then poll the OpenPosition table in case the
            // tradeId arrived via socket after SubmitOrder but wasn't cached yet.
            var tradeId = _session.TryGetTradeId(brokerOrderId)
                          ?? await _session.ResolveTradeIdAsync(brokerOrderId, cancellationToken, maxAttempts: 2, delayMs: 250);
            var isPendingOrder = tradeId == null;

            if (isPendingOrder)
            {
                using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);
                return await ModifyPendingOrderAsync(client, brokerOrderId, stopLoss, takeProfit, cancellationToken);
            }

            return await ModifyOpenTradeAsync(brokerOrderId, tradeId!, stopLoss, takeProfit, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FXCM ModifyOrder exception for {BrokerOrderId}", brokerOrderId);
            return Fail(brokerOrderId, $"FXCM exception: {ex.Message}");
        }
    }

    private async Task<BrokerOrderResult> ModifyOpenTradeAsync(
        string brokerOrderId, string tradeId,
        decimal? stopLoss, decimal? takeProfit, CancellationToken ct)
    {
        _logger.LogInformation(
            "FXCM ModifyOpenTrade: tradeId={TradeId} (from brokerOrderId={BrokerOrderId}) SL={StopLoss} TP={TakeProfit}",
            tradeId, brokerOrderId, stopLoss, takeProfit);

        // FXCM requires separate POST calls for stop vs limit.
        // Acquire a fresh client for each call so that if the SL call triggers a
        // 401 and invalidates the session, the TP call gets a re-authenticated client.
        // If SL succeeds but TP fails, the position is left with a modified SL
        // but the original TP — log a warning so operators can reconcile.
        bool slModified = false;

        if (stopLoss.HasValue)
        {
            using var slClient = await _session.GetAuthenticatedClientAsync(ct);
            var slResult = await ChangeTradeStopLimitAsync(
                slClient, tradeId, isStop: true, stopLoss.Value, ct);
            if (!slResult.Success)
                return slResult;
            slModified = true;
        }

        if (takeProfit.HasValue)
        {
            using var tpClient = await _session.GetAuthenticatedClientAsync(ct);
            var tpResult = await ChangeTradeStopLimitAsync(
                tpClient, tradeId, isStop: false, takeProfit.Value, ct);
            if (!tpResult.Success)
            {
                if (slModified)
                {
                    _logger.LogWarning(
                        "FXCM ModifyOrder PARTIAL: SL was updated but TP failed for tradeId={TradeId}. " +
                        "Position may be in inconsistent state. TP error: {Error}",
                        tradeId, tpResult.ErrorMessage);
                }
                return tpResult;
            }
        }

        _logger.LogInformation("FXCM ModifyOpenTrade success: tradeId={TradeId}", tradeId);
        return new BrokerOrderResult(true, brokerOrderId, null, null, null);
    }

    private async Task<BrokerOrderResult> ModifyPendingOrderAsync(
        HttpClient client, string orderId,
        decimal? stopLoss, decimal? takeProfit, CancellationToken ct)
    {
        _logger.LogInformation(
            "FXCM ModifyPendingOrder: orderId={OrderId} SL={StopLoss} TP={TakeProfit}",
            orderId, stopLoss, takeProfit);

        // POST /trading/change_order — modifies a pending entry order.
        // Only send stop/limit fields that are being changed to avoid clobbering
        // existing values (sending "0" would remove the existing SL or TP).
        var form = new Dictionary<string, string>
        {
            ["order_id"] = orderId
        };

        if (stopLoss.HasValue)
        {
            form["stop"] = stopLoss.Value.ToString(CultureInfo.InvariantCulture);
            form["is_stop_in_pips"] = "false";
        }

        if (takeProfit.HasValue)
        {
            form["limit"] = takeProfit.Value.ToString(CultureInfo.InvariantCulture);
            form["is_limit_in_pips"] = "false";
        }

        var (success, body, statusCode) = await PostFormAsync(
            client, "/trading/change_order", form, ct);

        if (!success)
        {
            _logger.LogError("FXCM change_order failed: HTTP {StatusCode} — {Body}",
                statusCode, body);
            return Fail(orderId, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);
        if (result?.Response?.Executed != true)
            return Fail(orderId, result?.Response?.Error ?? "Pending order SL/TP change not executed");

        _logger.LogInformation("FXCM ModifyPendingOrder success: orderId={OrderId}", orderId);
        return new BrokerOrderResult(true, orderId, null, null, null);
    }

    // ── Close Position ───────────────────────────────────────────────────────

    public async Task<BrokerOrderResult> ClosePositionAsync(
        string brokerPositionId, decimal lots, CancellationToken cancellationToken)
    {
        var amount = ToFxcmAmount(lots);
        var maxRetries = await _session.GetConfigIntAsync(CK_CloseMaxRetries, FallbackCloseMaxRetries, cancellationToken);
        var retryDelayMs = await _session.GetConfigIntAsync(CK_CloseRetryDelayMs, FallbackCloseRetryDelayMs, cancellationToken);

        // close_trade requires a tradeId. Resolve from the mapping first,
        // then try polling the OpenPosition table, and only fall back to
        // using brokerPositionId as-is if both lookups fail.
        var tradeId = _session.TryGetTradeId(brokerPositionId);
        if (tradeId == null)
        {
            tradeId = await _session.ResolveTradeIdAsync(brokerPositionId, cancellationToken);
            if (tradeId == null)
            {
                _logger.LogWarning(
                    "FXCM ClosePosition: could not resolve tradeId for brokerPositionId={BrokerPositionId}, " +
                    "using brokerPositionId as tradeId (may fail if it is an orderId)",
                    brokerPositionId);
                tradeId = brokerPositionId;
            }
        }

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Acquire a fresh client each attempt in case a prior attempt invalidated the session.
                using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);

                _logger.LogInformation(
                    "FXCM ClosePosition: tradeId={TradeId} (from brokerPositionId={BrokerPositionId}) amount={Amount} attempt={Attempt}/{MaxRetries}",
                    tradeId, brokerPositionId, amount, attempt, maxRetries);

                // POST /trading/close_trade
                // IOC (Immediate or Cancel) fills as much as possible and cancels the rest,
                // making it more resilient than FOK during volatile markets.
                var form = new Dictionary<string, string>
                {
                    ["trade_id"]      = tradeId,
                    ["amount"]        = amount.ToString(CultureInfo.InvariantCulture),
                    ["order_type"]    = "AtMarket",
                    ["time_in_force"] = "IOC",
                    ["at_market"]     = "0"
                };

                var (success, body, statusCode) = await PostFormAsync(
                    client, "/trading/close_trade", form, cancellationToken);

                if (!success)
                {
                    // Retry on transient/requote failures
                    if (attempt < maxRetries && IsTransientCloseFailure(body, statusCode))
                    {
                        _logger.LogWarning(
                            "FXCM close_trade transient failure for tradeId={TradeId} (HTTP {StatusCode}) — retrying in {DelayMs}ms (attempt {Attempt}/{MaxRetries})",
                            tradeId, statusCode, retryDelayMs, attempt + 1, maxRetries);
                        await Task.Delay(retryDelayMs, cancellationToken);
                        continue;
                    }
                    return Fail(brokerPositionId, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");
                }

                // Response: {"response":{"executed":true},"data":{"type":0,"orderId":81713394}}
                var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);

                if (result?.Response?.Executed != true)
                {
                    // Retry on requote/price-changed errors
                    if (attempt < maxRetries && IsTransientCloseFailure(result?.Response?.Error))
                    {
                        _logger.LogWarning(
                            "FXCM close_trade not executed for tradeId={TradeId}: {Error} — retrying in {DelayMs}ms (attempt {Attempt}/{MaxRetries})",
                            tradeId, result?.Response?.Error, retryDelayMs, attempt + 1, maxRetries);
                        await Task.Delay(retryDelayMs, cancellationToken);
                        continue;
                    }
                    return Fail(brokerPositionId, result?.Response?.Error ?? "Close not executed");
                }

                // Retrieve close price and actual filled quantity from the ClosedPosition table.
                // close_trade response only returns orderId — the actual close details
                // are in the ClosedPosition record created asynchronously.
                // With IOC time-in-force, fills may be partial.
                var (closePrice, filledQuantity) = await GetCloseDetailsByTradeIdAsync(client, tradeId, cancellationToken);

                // Clean up the mapping
                _session.RemoveMapping(brokerPositionId);

                _logger.LogInformation(
                    "FXCM close_trade success: tradeId={TradeId} closeOrderId={CloseOrderId} closePrice={ClosePrice} filledQty={FilledQty}",
                    tradeId, result.Data?.OrderId, closePrice, filledQuantity ?? lots);

                return new BrokerOrderResult(true, brokerPositionId, closePrice, filledQuantity ?? lots, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (attempt < maxRetries)
                {
                    _logger.LogWarning(ex,
                        "FXCM ClosePosition exception for {BrokerPositionId} — retrying in {DelayMs}ms (attempt {Attempt}/{MaxRetries})",
                        brokerPositionId, retryDelayMs, attempt + 1, maxRetries);
                    await Task.Delay(retryDelayMs, cancellationToken);
                    continue;
                }

                _logger.LogError(ex, "FXCM ClosePosition exception for {BrokerPositionId} after all retries", brokerPositionId);
                return Fail(brokerPositionId, $"FXCM exception: {ex.Message}");
            }
        }

        return Fail(brokerPositionId, "FXCM close_trade failed after all retry attempts");
    }

    private static bool IsTransientCloseFailure(string? message, int statusCode = 0)
    {
        if (statusCode >= 500)
            return true;
        if (string.IsNullOrEmpty(message))
            return false;
        return message.Contains("requote", StringComparison.OrdinalIgnoreCase)
            || message.Contains("price changed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("PRICE_RANGE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("try again", StringComparison.OrdinalIgnoreCase)
            || message.Contains("temporarily", StringComparison.OrdinalIgnoreCase);
    }

    // ── Close All For Symbol ─────────────────────────────────────────────────

    /// <summary>
    /// Closes all open positions for a given symbol in a single FXCM API call.
    /// More efficient than closing positions individually when liquidating a symbol.
    /// Not part of <see cref="IBrokerOrderExecutor"/> — call directly on the concrete type.
    /// </summary>
    public async Task<BrokerOrderResult> CloseAllForSymbolAsync(
        string symbol, CancellationToken cancellationToken)
    {
        var fxcmSymbol = ToFxcmSymbol(symbol);

        try
        {
            using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);
            var accountId    = await _session.GetAccountIdAsync(cancellationToken);

            _logger.LogInformation(
                "FXCM CloseAllForSymbol: symbol={Symbol} accountId={AccountId}", fxcmSymbol, accountId);

            // POST /trading/close_all (with forSymbol=true to scope to a single symbol)
            // IOC (Immediate or Cancel) is more resilient than FOK during volatile markets —
            // it fills as much as possible rather than rejecting the entire request.
            var form = new Dictionary<string, string>
            {
                ["account_id"]    = accountId,
                ["symbol"]        = fxcmSymbol,
                ["forSymbol"]     = "true",
                ["order_type"]    = "AtMarket",
                ["time_in_force"] = "IOC"
            };

            var (success, body, statusCode) = await PostFormAsync(
                client, "/trading/close_all", form, cancellationToken);

            if (!success)
                return Fail(null, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");

            // Response: {"response":{"executed":true},"data":null}
            var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);

            if (result?.Response?.Executed != true)
                return Fail(null, result?.Response?.Error ?? "Close all for symbol not executed");

            _logger.LogInformation("FXCM CloseAllForSymbol success: symbol={Symbol}", fxcmSymbol);
            return new BrokerOrderResult(true, null, null, null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FXCM CloseAllForSymbol exception for {Symbol}", fxcmSymbol);
            return Fail(null, $"FXCM exception: {ex.Message}");
        }
    }

    // ── Account Summary ──────────────────────────────────────────────────────

    public async Task<BrokerAccountSummary?> GetAccountSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);
            var accountId    = await _session.GetAccountIdAsync(cancellationToken);

            _logger.LogDebug("FXCM GetAccountSummary: accountId={AccountId}", accountId);

            // GET /trading/get_model?models=Account
            await _session.ThrottleAsync(cancellationToken);
            using var response = await client.GetAsync(
                "/trading/get_model?models=Account", cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await HandleAuthFailureAsync(response);
                _logger.LogError("FXCM get_model(Account) failed: HTTP {StatusCode} — {Body}",
                    (int)response.StatusCode, body);
                return null;
            }

            // Account table (t=6): accountId, balance, equity, usdMr, usableMargin, mc
            var result = JsonSerializer.Deserialize<FxcmAccountModelResponse>(body, JsonOptions);
            var accounts = result?.Accounts;

            if (accounts == null || accounts.Count == 0)
            {
                _logger.LogWarning("FXCM GetAccountSummary: no account data returned");
                return null;
            }

            var account = accounts.FirstOrDefault(a =>
                string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                ?? accounts[0];

            _logger.LogDebug(
                "FXCM AccountSummary: balance={Balance} equity={Equity} usdMr={UsdMr} usableMargin={UsableMargin}",
                account.Balance, account.Equity, account.UsdMr, account.UsableMargin);

            return new BrokerAccountSummary(
                Balance:         account.Balance,
                Equity:          account.Equity,
                MarginUsed:      account.UsdMr,
                MarginAvailable: account.UsableMargin);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FXCM GetAccountSummary exception");
            return null;
        }
    }

    // ── Get Order Status ─────────────────────────────────────────────────────

    public async Task<BrokerOrderStatus?> GetOrderStatusAsync(
        string brokerOrderId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);

            var tradeId = _session.TryGetTradeId(brokerOrderId) ?? brokerOrderId;

            // Fetch all three tables in a single API call to reduce latency and rate-limit usage.
            await _session.ThrottleAsync(cancellationToken);
            using var response = await client.GetAsync(
                "/trading/get_model?models=OpenPosition,Order,ClosedPosition", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await HandleAuthFailureAsync(response);
                _logger.LogError("FXCM get_model(OpenPosition,Order,ClosedPosition) failed: HTTP {StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Check OpenPosition table (filled market orders / triggered entry orders)
            if (TryGetArrayProperty(root, "open_positions", "openPositions", out var positions))
            {
                foreach (var pos in positions.EnumerateArray())
                {
                    var posTradeId = pos.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;
                    var posOrderId = pos.TryGetProperty("orderId", out var oid) ? oid.ToString() : null;

                    if (string.Equals(posTradeId, tradeId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(posOrderId, brokerOrderId, StringComparison.OrdinalIgnoreCase))
                    {
                        var openPrice = pos.TryGetProperty("open", out var op) ? (decimal?)op.GetDecimal() : null;
                        var amountK = pos.TryGetProperty("amountK", out var ak) ? (decimal?)ak.GetDecimal() : null;
                        var updatedUtc = TryGetTimestamp(pos);

                        return new BrokerOrderStatus(
                            BrokerOrderId: brokerOrderId,
                            Status: "Filled",
                            FilledPrice: openPrice,
                            FilledQuantity: amountK.HasValue ? AmountKToLots(amountK.Value) : null,
                            LastUpdatedUtc: updatedUtc);
                    }
                }
            }

            // Check Order table (pending entry orders)
            if (TryGetArrayProperty(root, "orders", null, out var orders))
            {
                foreach (var ord in orders.EnumerateArray())
                {
                    var ordId = ord.TryGetProperty("orderId", out var oid) ? oid.ToString() : null;
                    if (string.Equals(ordId, brokerOrderId, StringComparison.OrdinalIgnoreCase))
                    {
                        var status = ord.TryGetProperty("status", out var s) ? s.GetString() : "Pending";
                        var updatedUtc = TryGetTimestamp(ord);

                        return new BrokerOrderStatus(
                            BrokerOrderId: brokerOrderId,
                            Status: status ?? "Pending",
                            FilledPrice: null,
                            FilledQuantity: null,
                            LastUpdatedUtc: updatedUtc);
                    }
                }
            }

            // Check ClosedPosition table (already closed trades)
            if (TryGetArrayProperty(root, "closed_positions", "closedPositions", out var closedPositions))
            {
                foreach (var pos in closedPositions.EnumerateArray())
                {
                    var posTradeId = pos.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;
                    if (string.Equals(posTradeId, tradeId, StringComparison.OrdinalIgnoreCase))
                    {
                        var closePrice = pos.TryGetProperty("close", out var cp) ? (decimal?)cp.GetDecimal() : null;
                        var amountK = pos.TryGetProperty("amountK", out var ak) ? (decimal?)ak.GetDecimal() : null;
                        var updatedUtc = TryGetTimestamp(pos);

                        return new BrokerOrderStatus(
                            BrokerOrderId: brokerOrderId,
                            Status: "Closed",
                            FilledPrice: closePrice,
                            FilledQuantity: amountK.HasValue ? AmountKToLots(amountK.Value) : null,
                            LastUpdatedUtc: updatedUtc);
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FXCM GetOrderStatus exception for {BrokerOrderId}", brokerOrderId);
            return null;
        }
    }

    private static bool TryGetArrayProperty(JsonElement root, string name1, string? name2, out JsonElement result)
    {
        if (root.TryGetProperty(name1, out result) && result.ValueKind == JsonValueKind.Array)
            return true;
        if (name2 != null && root.TryGetProperty(name2, out result) && result.ValueKind == JsonValueKind.Array)
            return true;
        result = default;
        return false;
    }

    /// <summary>
    /// Extracts a UTC timestamp from an FXCM table row. Tries "time" (epoch string used in
    /// OpenPosition/ClosedPosition) and "timeInForce" / "expireDate" won't help, so falls back
    /// to DateTime.UtcNow only when no parseable timestamp field is found.
    /// FXCM epoch format: "MM/dd/yyyy HH:mm:ss" or Unix seconds depending on table.
    /// </summary>
    private static DateTime? TryGetTimestamp(JsonElement element)
    {
        // OpenPosition and ClosedPosition rows use "time" as a date string
        if (element.TryGetProperty("time", out var timeProp))
        {
            if (timeProp.ValueKind == JsonValueKind.String)
            {
                var raw = timeProp.GetString();
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    return parsed;
            }
            else if (timeProp.ValueKind == JsonValueKind.Number && timeProp.TryGetInt64(out var epoch))
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
        }

        // Some table rows use "Updated" (epoch seconds) — e.g. Offer table
        if (element.TryGetProperty("Updated", out var updatedProp) &&
            updatedProp.ValueKind == JsonValueKind.Number &&
            updatedProp.TryGetInt64(out var updatedEpoch))
        {
            return DateTimeOffset.FromUnixTimeSeconds(updatedEpoch).UtcDateTime;
        }

        return null;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<BrokerOrderResult> ChangeTradeStopLimitAsync(
        HttpClient client, string tradeId, bool isStop, decimal rate, CancellationToken ct)
    {
        // POST /trading/change_trade_stop_limit
        var form = new Dictionary<string, string>
        {
            ["trade_id"]   = tradeId,
            ["is_stop"]    = isStop.ToString().ToLowerInvariant(),
            ["rate"]       = rate.ToString(CultureInfo.InvariantCulture),
            ["is_in_pips"] = "false"
        };

        var (success, body, statusCode) = await PostFormAsync(
            client, "/trading/change_trade_stop_limit", form, ct);

        if (!success)
        {
            var label = isStop ? "SL" : "TP";
            _logger.LogError("FXCM change_trade_stop_limit({Label}) failed: HTTP {StatusCode} — {Body}",
                label, statusCode, body);
            return Fail(tradeId, $"FXCM HTTP {statusCode}: {ExtractErrorMessage(body)}");
        }

        // Response: {"response":{"executed":true},"data":null}
        var result = JsonSerializer.Deserialize<FxcmOrderResponse>(body, JsonOptions);
        if (result?.Response?.Executed != true)
            return Fail(tradeId, result?.Response?.Error ?? $"{(isStop ? "Stop" : "Limit")} change not executed");

        return new BrokerOrderResult(true, tradeId, null, null, null);
    }

    private async Task<decimal?> GetOpenPriceByTradeIdAsync(
        HttpClient client, string tradeId, CancellationToken ct)
    {
        try
        {
            await _session.ThrottleAsync(ct);
            using var response = await client.GetAsync(
                "/trading/get_model?models=OpenPosition", ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement positions = default;
            if (!root.TryGetProperty("open_positions", out positions) &&
                !root.TryGetProperty("openPositions", out positions))
                return null;

            foreach (var pos in positions.EnumerateArray())
            {
                var posTradeId = pos.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;
                if (string.Equals(posTradeId, tradeId, StringComparison.OrdinalIgnoreCase) &&
                    pos.TryGetProperty("open", out var openPrice))
                {
                    return openPrice.GetDecimal();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FXCM: could not retrieve fill price for tradeId={TradeId}", tradeId);
            return null;
        }
    }

    /// <summary>
    /// Retrieves the close price and filled quantity after a close_trade.
    /// First checks the session manager's socket-populated cache (ClosedPosition table updates
    /// arrive via Socket.IO and are cached automatically). Falls back to polling the REST API
    /// with exponential backoff to reduce load on accounts with many closed positions.
    /// </summary>
    private async Task<(decimal? ClosePrice, decimal? FilledQuantity)> GetCloseDetailsByTradeIdAsync(
        HttpClient client, string tradeId, CancellationToken ct)
    {
        // Check socket-populated cache first — avoids any REST call if the close event
        // arrived via Socket.IO before we got here.
        var cached = _session.TryGetCloseDetails(tradeId);
        if (cached.HasValue)
        {
            _logger.LogDebug("FXCM: close details for tradeId={TradeId} resolved from socket cache", tradeId);
            return (cached.Value.ClosePrice, cached.Value.FilledLots);
        }

        var maxAttempts = await _session.GetConfigIntAsync(CK_ClosePriceMaxAttempts, FallbackClosePriceMaxAttempts, ct);
        var baseDelayMs = await _session.GetConfigIntAsync(CK_ClosePriceDelayMs, FallbackClosePriceDelayMs, ct);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Check cache again before each poll — the socket event may have arrived
            // while we were waiting between attempts.
            cached = _session.TryGetCloseDetails(tradeId);
            if (cached.HasValue)
            {
                _logger.LogDebug(
                    "FXCM: close details for tradeId={TradeId} resolved from socket cache on attempt {Attempt}",
                    tradeId, attempt);
                return (cached.Value.ClosePrice, cached.Value.FilledLots);
            }

            try
            {
                await _session.ThrottleAsync(ct);
                using var response = await client.GetAsync(
                    "/trading/get_model?models=ClosedPosition", ct);

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(ExponentialDelay(baseDelayMs, attempt), ct);
                        continue;
                    }
                    return (null, null);
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (TryGetArrayProperty(root, "closed_positions", "closedPositions", out var positions))
                {
                    foreach (var pos in positions.EnumerateArray())
                    {
                        var posTradeId = pos.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;
                        if (string.Equals(posTradeId, tradeId, StringComparison.OrdinalIgnoreCase) &&
                            pos.TryGetProperty("close", out var closePrice))
                        {
                            decimal? filledLots = null;
                            if (pos.TryGetProperty("amountK", out var amountK))
                                filledLots = AmountKToLots(amountK.GetDecimal());

                            // Cache for future lookups and socket miss scenarios
                            _session.RegisterCloseDetails(tradeId, closePrice.GetDecimal(), filledLots);
                            return (closePrice.GetDecimal(), filledLots);
                        }
                    }
                }

                if (attempt < maxAttempts)
                    await Task.Delay(ExponentialDelay(baseDelayMs, attempt), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FXCM: could not retrieve close details for tradeId={TradeId} (attempt {Attempt})",
                    tradeId, attempt);
                if (attempt < maxAttempts)
                    await Task.Delay(ExponentialDelay(baseDelayMs, attempt), ct);
            }
        }

        _logger.LogDebug("FXCM: close details unavailable for tradeId={TradeId} after {Attempts} attempts",
            tradeId, maxAttempts);
        return (null, null);
    }

    private static int ExponentialDelay(int baseDelayMs, int attempt)
    {
        // Cap at ~8x the base delay to avoid excessively long waits
        var multiplier = Math.Min(1 << (attempt - 1), 8);
        return baseDelayMs * multiplier;
    }

    /// <summary>
    /// Sends a POST with form-encoded body, retries once on transient 5xx errors,
    /// and handles 401 session invalidation.
    /// Returns (success, responseBody, httpStatusCode).
    /// </summary>
    private async Task<(bool Success, string Body, int StatusCode)> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> form, CancellationToken ct)
    {
        const int maxAttempts = 2;
        const int retryDelayMs = 500;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await _session.ThrottleAsync(ct);
            using var response = await client.PostAsync(path, new FormUrlEncodedContent(form), ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return (true, body, (int)response.StatusCode);

            var statusCode = (int)response.StatusCode;

            // Retry on 5xx (server-side transient errors) if we have attempts left
            if (statusCode >= 500 && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "FXCM {Path} returned HTTP {StatusCode} — retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                    path, statusCode, retryDelayMs, attempt, maxAttempts);
                await Task.Delay(retryDelayMs, ct);
                continue;
            }

            await HandleAuthFailureAsync(response);
            _logger.LogError("FXCM {Path} failed: HTTP {StatusCode} — {Body}",
                path, statusCode, body);
            return (false, body, statusCode);
        }

        // Unreachable, but satisfies the compiler
        return (false, string.Empty, 0);
    }

    /// <summary>
    /// If FXCM returns 401/403, invalidate the session so the next call re-handshakes.
    /// </summary>
    private async Task HandleAuthFailureAsync(HttpResponseMessage response)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                or System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("FXCM returned {StatusCode} — invalidating session for re-handshake",
                (int)response.StatusCode);
            await _session.InvalidateSessionAsync();
        }
    }

    // ── Symbol/amount conversion ─────────────────────────────────────────────

    // FXCM non-standard symbols that don't follow the simple 6-char forex pair pattern.
    // Engine format → FXCM format. Metals, indices, energy, crypto, etc.
    private static readonly Dictionary<string, string> SymbolToFxcm = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XAUUSD"] = "XAU/USD",
        ["XAGUSD"] = "XAG/USD",
        ["XAUEUR"] = "XAU/EUR",
        ["XAGEUR"] = "XAG/EUR",
        ["BCOUSD"] = "UK100",      // Brent crude — use FXCM instrument name
        ["WTIUSD"] = "USOil",
        ["NGAS"]   = "NGAS",
        ["US30"]   = "US30",
        ["US500"]  = "SPX500",
        ["NAS100"] = "NAS100",
        ["UK100"]  = "UK100",
        ["DE30"]   = "GER30",
        ["JP225"]  = "JPN225",
        ["BTCUSD"] = "BTC/USD",
        ["ETHUSD"] = "ETH/USD",
        ["LTCUSD"] = "LTC/USD",
    };

    private static readonly Dictionary<string, string> FxcmToSymbol =
        SymbolToFxcm.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Converts engine symbol format to FXCM format.
    /// Handles standard 6-char forex pairs (e.g. "EURUSD" → "EUR/USD"),
    /// metals (e.g. "XAUUSD" → "XAU/USD"), and non-standard instruments.
    /// </summary>
    internal static string ToFxcmSymbol(string symbol)
    {
        if (SymbolToFxcm.TryGetValue(symbol, out var fxcm))
            return fxcm;
        if (symbol.Length == 6 && !symbol.Contains('/'))
            return $"{symbol[..3]}/{symbol[3..]}";
        return symbol;
    }

    /// <summary>
    /// Converts FXCM symbol format back to engine format.
    /// Handles standard forex pairs (e.g. "EUR/USD" → "EURUSD"),
    /// metals, and non-standard instruments via reverse lookup.
    /// </summary>
    internal static string FromFxcmSymbol(string fxcmSymbol)
    {
        if (FxcmToSymbol.TryGetValue(fxcmSymbol, out var engine))
            return engine;
        return fxcmSymbol.Replace("/", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts engine lot size to FXCM K-lot amount.
    /// Engine: 0.01 lots = 1K, 0.1 lots = 10K, 1.0 lots = 100K.
    /// Logs a warning if the amount is clamped to the FXCM minimum of 1K.
    /// </summary>
    internal int ToFxcmAmount(decimal lots)
    {
        var raw = (int)(lots * 100_000m / 1000m);
        if (raw < 1)
        {
            _logger.LogWarning(
                "FXCM ToFxcmAmount: {Lots} lots ({RawK}K) is below FXCM minimum of 1K — clamped to 1K",
                lots, raw);
            return 1;
        }
        return raw;
    }

    /// <summary>
    /// Converts FXCM amountK (thousands of units) to engine lot size.
    /// FXCM: 1K = 0.01 lots, 10K = 0.1 lots, 100K = 1.0 lot.
    /// </summary>
    internal static decimal AmountKToLots(decimal amountK) => amountK * 1000m / 100_000m;

    private static BrokerOrderResult Fail(string? brokerOrderId, string errorMessage) =>
        new(Success: false, BrokerOrderId: brokerOrderId, FilledPrice: null,
            FilledQuantity: null, ErrorMessage: errorMessage);

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("response", out var resp) &&
                resp.TryGetProperty("error", out var err))
            {
                var msg = err.GetString();
                if (!string.IsNullOrEmpty(msg))
                    return msg;
            }
        }
        catch { /* fall through */ }

        return body.Length > 200 ? body[..200] : body;
    }

    // ── FXCM response DTOs ───────────────────────────────────────────────────

    private sealed class FxcmOrderData
    {
        [JsonPropertyName("type")]
        public int? Type { get; set; }

        [JsonPropertyName("orderId")]
        public long? OrderId { get; set; }
    }

    private sealed class FxcmOrderResponse
    {
        [JsonPropertyName("response")]
        public FxcmResponseStatus? Response { get; set; }

        [JsonPropertyName("data")]
        public FxcmOrderData? Data { get; set; }
    }

    private sealed class FxcmAccountData
    {
        [JsonPropertyName("t")]
        public int? TableType { get; set; }

        [JsonPropertyName("accountId")]
        public string? AccountId { get; set; }

        [JsonPropertyName("balance")]
        public decimal Balance { get; set; }

        [JsonPropertyName("equity")]
        public decimal Equity { get; set; }

        [JsonPropertyName("usdMr")]
        public decimal UsdMr { get; set; }

        [JsonPropertyName("usableMargin")]
        public decimal UsableMargin { get; set; }

        [JsonPropertyName("mc")]
        public string? MarginCallState { get; set; }
    }

    private sealed class FxcmAccountModelResponse
    {
        [JsonPropertyName("response")]
        public FxcmResponseStatus? Response { get; set; }

        [JsonPropertyName("accounts")]
        public List<FxcmAccountData>? Accounts { get; set; }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// FXCM Data Feed — production implementation
// ═══════════════════════════════════════════════════════════════════════════════

[RegisterKeyedService(typeof(IBrokerDataFeed), BrokerType.Fxcm, ServiceLifetime.Singleton)]
public sealed class FxcmBrokerAdapter : IBrokerDataFeed
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IFxcmSessionManager _session;
    private readonly ILogger<FxcmBrokerAdapter> _logger;

    public FxcmBrokerAdapter(
        IFxcmSessionManager session,
        ILogger<FxcmBrokerAdapter> logger)
    {
        _session = session;
        _logger  = logger;
    }

    public async Task SubscribeAsync(
        IEnumerable<string> symbols, Func<Tick, Task> onTick, CancellationToken cancellationToken)
    {
        var fxcmSymbolsList = symbols.Select(FxcmOrderExecutor.ToFxcmSymbol).ToList();
        var fxcmSymbols     = new HashSet<string>(fxcmSymbolsList, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("FXCM SubscribeAsync: subscribing to {Count} symbols", fxcmSymbols.Count);

        // POST /subscribe — tell FXCM which symbols we want updates for
        using (var subClient = await _session.GetAuthenticatedClientAsync(cancellationToken))
        {
            foreach (var fxcmSymbol in fxcmSymbolsList)
            {
                var form = new Dictionary<string, string> { ["pairs"] = fxcmSymbol };

                try
                {
                    using var subResponse = await subClient.PostAsync(
                        "/subscribe", new FormUrlEncodedContent(form), cancellationToken);

                    if (!subResponse.IsSuccessStatusCode)
                    {
                        var body = await subResponse.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError("FXCM Subscribe failed for {Symbol}: HTTP {StatusCode} — {Body}",
                            fxcmSymbol, (int)subResponse.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FXCM Subscribe exception for {Symbol}", fxcmSymbol);
                }
            }
        }

        // Register a callback for real-time price events from the Socket.IO connection.
        // The session manager's socket receives "N" events with price data and dispatches
        // to all registered callbacks. This gives us true real-time streaming.
        var callbackId = _session.RegisterPriceCallback(async (fxcmSymbol, bid, ask, timestamp) =>
        {
            if (!fxcmSymbols.Contains(fxcmSymbol))
                return;

            var engineSymbol = FxcmOrderExecutor.FromFxcmSymbol(fxcmSymbol);
            await onTick(new Tick(engineSymbol, bid, ask, timestamp));
        });

        try
        {
            // Keep alive while cancellation is not requested.
            // If the socket is connected, prices arrive via the callback above.
            // If the socket disconnects, fall back to polling the Offer table.
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_session.IsSocketConnected)
                    {
                        // Socket is alive — prices arrive via the callback, just wait
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                    else
                    {
                        // Socket disconnected — poll the Offer table as fallback
                        _logger.LogDebug("FXCM socket disconnected, falling back to Offer table poll");

                        using var pollClient = await _session.GetAuthenticatedClientAsync(cancellationToken);

                        using var priceResponse = await pollClient.GetAsync(
                            "/trading/get_model?models=Offer", cancellationToken);

                        if (priceResponse.IsSuccessStatusCode)
                        {
                            var priceBody = await priceResponse.Content.ReadAsStringAsync(cancellationToken);
                            var priceResult = JsonSerializer.Deserialize<FxcmOfferModelResponse>(priceBody, JsonOptions);

                            if (priceResult?.Offers != null)
                            {
                                foreach (var offer in priceResult.Offers)
                                {
                                    if (offer.Currency == null || !fxcmSymbols.Contains(offer.Currency))
                                        continue;

                                    var engineSymbol = FxcmOrderExecutor.FromFxcmSymbol(offer.Currency);
                                    await onTick(new Tick(engineSymbol, offer.Sell, offer.Buy, DateTime.UtcNow));
                                }
                            }
                        }
                        else if (priceResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                                          or System.Net.HttpStatusCode.Forbidden)
                        {
                            await _session.InvalidateSessionAsync();
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FXCM price feed error — retrying in 2s");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        finally
        {
            _session.UnregisterPriceCallback(callbackId);
            await UnsubscribeAsync(fxcmSymbolsList);
        }
    }

    private async Task UnsubscribeAsync(List<string> fxcmSymbols)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client = await _session.GetAuthenticatedClientAsync(cts.Token);

            foreach (var fxcmSymbol in fxcmSymbols)
            {
                var form = new Dictionary<string, string> { ["pairs"] = fxcmSymbol };
                using var response = await client.PostAsync(
                    "/unsubscribe", new FormUrlEncodedContent(form), cts.Token);

                _logger.LogDebug("FXCM unsubscribed from {Symbol}: HTTP {StatusCode}",
                    fxcmSymbol, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FXCM unsubscribe failed (non-critical, session may be closing)");
        }
    }

    public async Task<IReadOnlyList<BrokerCandle>> GetCandlesAsync(
        string symbol, string timeframe, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var fxcmSymbol = FxcmOrderExecutor.ToFxcmSymbol(symbol);
        var periodId   = MapTimeframe(timeframe);

        try
        {
            using var client = await _session.GetAuthenticatedClientAsync(cancellationToken);

            // Step 1: Resolve offer ID
            var offerId = await ResolveOfferIdAsync(client, fxcmSymbol, cancellationToken);
            if (offerId == null)
            {
                _logger.LogWarning("FXCM GetCandles: could not resolve offer ID for {Symbol}", fxcmSymbol);
                return Array.Empty<BrokerCandle>();
            }

            // Step 2: GET /candles/{offer_id}/{period_id}?num=10000&from={epoch}&to={epoch}
            var fromEpoch = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeSeconds();
            var toEpoch   = new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeSeconds();
            var url = $"/candles/{offerId}/{periodId}?from={fromEpoch}&to={toEpoch}&num=10000";

            _logger.LogDebug(
                "FXCM GetCandles: symbol={Symbol} offerId={OfferId} period={Period}",
                fxcmSymbol, offerId, periodId);

            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                        or System.Net.HttpStatusCode.Forbidden)
                    await _session.InvalidateSessionAsync();

                _logger.LogError("FXCM GetCandles failed: HTTP {StatusCode} — {Body}",
                    (int)response.StatusCode, body);
                return Array.Empty<BrokerCandle>();
            }

            // Candles are arrays: [timestamp,BidOpen,BidClose,BidHigh,BidLow,AskOpen,AskClose,AskHigh,AskLow,TickQty]
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candles", out var candlesArray) ||
                candlesArray.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("FXCM GetCandles: no candles array in response");
                return Array.Empty<BrokerCandle>();
            }

            var candles = new List<BrokerCandle>();
            var candleDuration = GetTimeframeDuration(timeframe);
            var now = DateTime.UtcNow;

            foreach (var row in candlesArray.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 10)
                    continue;

                var timestamp = DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()).UtcDateTime;
                var bidOpen   = row[1].GetDecimal();
                var bidClose  = row[2].GetDecimal();
                var bidHigh   = row[3].GetDecimal();
                var bidLow    = row[4].GetDecimal();
                var tickQty   = row[9].GetDecimal();

                candles.Add(new BrokerCandle(
                    Symbol:    symbol,
                    Timeframe: timeframe,
                    Open:      bidOpen,
                    High:      bidHigh,
                    Low:       bidLow,
                    Close:     bidClose,
                    Volume:    tickQty,
                    Timestamp: timestamp,
                    IsClosed:  timestamp + candleDuration <= now));
            }

            _logger.LogInformation(
                "FXCM GetCandles: retrieved {Count} candles for {Symbol} ({Period})",
                candles.Count, fxcmSymbol, periodId);

            return candles;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FXCM GetCandles exception for {Symbol}", fxcmSymbol);
            return Array.Empty<BrokerCandle>();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> ResolveOfferIdAsync(
        HttpClient client, string fxcmSymbol, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(
                "/trading/get_model?models=Offer", ct);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                        or System.Net.HttpStatusCode.Forbidden)
                    await _session.InvalidateSessionAsync();
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<FxcmOfferModelResponse>(body, JsonOptions);

            var offer = result?.Offers?.FirstOrDefault(o =>
                string.Equals(o.Currency, fxcmSymbol, StringComparison.OrdinalIgnoreCase));

            return offer?.OfferId?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FXCM: could not resolve offer ID for {Symbol}", fxcmSymbol);
            return null;
        }
    }

    /// <summary>
    /// Maps engine timeframe strings to FXCM period identifiers.
    /// FXCM periods: m1, m5, m15, m30, H1, H2, H3, H4, H6, H8, D1, W1, M1
    /// </summary>
    internal static string MapTimeframe(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "M1"  => "m1",
        "M5"  => "m5",
        "M15" => "m15",
        "M30" => "m30",
        "H1"  => "H1",
        "H2"  => "H2",
        "H3"  => "H3",
        "H4"  => "H4",
        "H6"  => "H6",
        "H8"  => "H8",
        "D1"  => "D1",
        "W1"  => "W1",
        _     => "H1"
    };

    /// <summary>
    /// Returns the duration of a single candle for the given engine timeframe string.
    /// Used to determine whether a candle is closed (its period has fully elapsed).
    /// </summary>
    internal static TimeSpan GetTimeframeDuration(string timeframe) => timeframe.ToUpperInvariant() switch
    {
        "M1"  => TimeSpan.FromMinutes(1),
        "M5"  => TimeSpan.FromMinutes(5),
        "M15" => TimeSpan.FromMinutes(15),
        "M30" => TimeSpan.FromMinutes(30),
        "H1"  => TimeSpan.FromHours(1),
        "H2"  => TimeSpan.FromHours(2),
        "H3"  => TimeSpan.FromHours(3),
        "H4"  => TimeSpan.FromHours(4),
        "H6"  => TimeSpan.FromHours(6),
        "H8"  => TimeSpan.FromHours(8),
        "D1"  => TimeSpan.FromDays(1),
        "W1"  => TimeSpan.FromDays(7),
        _     => TimeSpan.FromHours(1)
    };

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed class FxcmOfferData
    {
        [JsonPropertyName("t")]
        public int? TableType { get; set; }

        [JsonPropertyName("offerId")]
        public long? OfferId { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("buy")]
        public decimal Buy { get; set; }

        [JsonPropertyName("sell")]
        public decimal Sell { get; set; }

        [JsonPropertyName("high")]
        public decimal High { get; set; }

        [JsonPropertyName("low")]
        public decimal Low { get; set; }

        [JsonPropertyName("ratePrecision")]
        public int? RatePrecision { get; set; }
    }

    private sealed class FxcmOfferModelResponse
    {
        [JsonPropertyName("response")]
        public FxcmResponseStatus? Response { get; set; }

        [JsonPropertyName("offers")]
        public List<FxcmOfferData>? Offers { get; set; }
    }
}

internal sealed class FxcmResponseStatus
{
    [JsonPropertyName("executed")]
    public bool? Executed { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
