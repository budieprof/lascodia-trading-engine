using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using SocketIOClient.Common;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

// ═══════════════════════════════════════════════════════════════════════════════
// FXCM Session Manager
// ═══════════════════════════════════════════════════════════════════════════════
//
// Manages a persistent Socket.IO connection to FXCM. This provides:
//   1. The socket_id needed for REST API auth: "Bearer {socket_id}{access_token}"
//   2. Real-time price streaming via socket events (no polling needed)
//   3. Real-time order/position table updates for orderId→tradeId resolution
//   4. Automatic ping/pong keepalive (handled by the SocketIOClient library)
//
// FXCM uses Socket.IO v2 protocol (EIO=3).

/// <summary>
/// Singleton that manages the FXCM Socket.IO connection, REST auth, price streaming,
/// and orderId→tradeId resolution.
/// </summary>
[RegisterKeyedService(typeof(IFxcmSessionManager), BrokerType.Fxcm, ServiceLifetime.Singleton)]
public sealed class FxcmSessionManager : IFxcmSessionManager, IAsyncDisposable, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FxcmSessionManager> _logger;

    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private FxcmSession? _currentSession;

    private readonly ConcurrentDictionary<string, (string TradeId, DateTime CreatedUtc)> _orderToTradeId = new();
    private static readonly TimeSpan MappingTtl = TimeSpan.FromHours(24);
    private DateTime _lastPurgeUtc = DateTime.UtcNow;

    /// <summary>Cache of close details received via socket ClosedPosition table updates or explicit registration.</summary>
    private readonly ConcurrentDictionary<string, (decimal ClosePrice, decimal? FilledLots, DateTime CreatedUtc)> _closeDetails = new();

    /// <summary>Tracks in-flight order submissions by internal Order.Id to prevent duplicate broker submissions.</summary>
    private readonly ConcurrentDictionary<long, (string BrokerOrderId, DateTime CreatedUtc)> _inFlightOrders = new();
    private static readonly TimeSpan InFlightTtl = TimeSpan.FromMinutes(5);

    /// <summary>Callbacks registered by FxcmBrokerAdapter for live price ticks.</summary>
    private readonly ConcurrentDictionary<string, Func<string, decimal, decimal, DateTime, Task>> _priceCallbacks = new();

    /// <summary>
    /// Rate limiter: FXCM enforces ~10 requests/second. We use a SemaphoreSlim to throttle
    /// concurrent REST calls and a timestamp to enforce minimum inter-request spacing.
    /// </summary>
    private readonly SemaphoreSlim _rateLimiter = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private const int MaxConcurrentRequests = 8;
    private long _lastRequestTicks = 0;
    private const long MinRequestIntervalTicks = TimeSpan.TicksPerSecond / 10; // 100ms = 10 req/s

    public FxcmSessionManager(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<FxcmSessionManager> logger)
    {
        _scopeFactory      = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    // ── Session management ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HttpClient> GetAuthenticatedClientAsync(CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct);
        var client  = _httpClientFactory.CreateClient();

        client.BaseAddress = new Uri(session.BaseUrl.TrimEnd('/'));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"{session.SocketId}{session.AccessToken}");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "request");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <inheritdoc/>
    public async Task InvalidateSessionAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_currentSession != null)
            {
                await DisconnectSocketAsync(_currentSession.Socket);
                _currentSession = null;
            }
            _logger.LogInformation("FXCM session invalidated — will reconnect on next call");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetAccountIdAsync(CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct);
        return session.AccountId;
    }

    /// <inheritdoc/>
    public bool IsSocketConnected
    {
        get
        {
            var session = _currentSession;
            return session?.Socket.Connected == true;
        }
    }

    private async Task<FxcmSession> EnsureSessionAsync(CancellationToken ct)
    {
        PurgeStaleMappings();

        var session = _currentSession;
        if (session != null && session.Socket.Connected)
            return session;

        await _sessionLock.WaitAsync(ct);
        try
        {
            session = _currentSession;
            if (session != null && session.Socket.Connected)
                return session;

            // Clean up old socket if it exists but disconnected
            if (session != null)
                await DisconnectSocketAsync(session.Socket);

            _currentSession = await EstablishSessionAsync(ct);
            return _currentSession;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<FxcmSession> EstablishSessionAsync(CancellationToken ct)
    {
        var creds = await GetCredentialsAsync(ct);

        _logger.LogInformation(
            "FXCM establishing Socket.IO connection: baseUrl={BaseUrl} accountId={AccountId}",
            creds.BaseUrl, creds.AccountId);

        var socket = new SocketIOClient.SocketIO(new Uri(creds.BaseUrl.TrimEnd('/')), new SocketIOOptions
        {
            EIO = EngineIO.V3,
            Query = new NameValueCollection
            {
                ["access_token"] = creds.AccessToken
            },
            Reconnection = true,
            ReconnectionAttempts = 20,
            ConnectionTimeout = TimeSpan.FromSeconds(15)
        });

        // Wire up event handlers before connecting
        WireSocketEvents(socket);

        // Connect
        var connectTcs = new TaskCompletionSource<bool>();
        using var ctReg = ct.Register(() => connectTcs.TrySetCanceled());

        socket.OnConnected += (_, _) => connectTcs.TrySetResult(true);
        socket.OnError += (_, e) => connectTcs.TrySetException(
            new InvalidOperationException($"FXCM Socket.IO connection error: {e}"));

        await socket.ConnectAsync();
        await connectTcs.Task;

        var socketId = socket.Id
            ?? throw new InvalidOperationException("FXCM Socket.IO connected but no session ID returned");

        _logger.LogInformation(
            "FXCM Socket.IO connected: sid={SocketId}",
            socketId.Length > 8 ? socketId[..8] + "..." : socketId);

        return new FxcmSession(
            SocketId: socketId,
            AccessToken: creds.AccessToken,
            BaseUrl: creds.BaseUrl,
            AccountId: creds.AccountId,
            Socket: socket);
    }

    private void WireSocketEvents(SocketIOClient.SocketIO socket)
    {
        socket.OnDisconnected += (_, reason) =>
        {
            _logger.LogWarning("FXCM Socket.IO disconnected: {Reason}", reason);
        };

        socket.OnReconnectAttempt += (_, attempt) =>
        {
            _logger.LogInformation("FXCM Socket.IO reconnecting: attempt {Attempt}", attempt);
        };

        // After reconnection, the socket gets a new sid. Update the session under lock
        // to avoid racing with EnsureSessionAsync.
        socket.OnConnected += async (_, _) =>
        {
            if (socket.Id == null) return;

            await _sessionLock.WaitAsync();
            try
            {
                if (_currentSession != null)
                {
                    _logger.LogInformation("FXCM Socket.IO reconnected: new sid={Sid}", socket.Id);
                    _currentSession = _currentSession with { SocketId = socket.Id };
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        };

        socket.OnError += (_, error) =>
        {
            _logger.LogError("FXCM Socket.IO error: {Error}", error);
        };

        // FXCM multiplexes all table updates over a single "N" event.
        // Price updates (Offer table) contain "Rates" + "Symbol".
        // Order table updates (t=3) contain orderId→tradeId mappings after fills.
        // A single handler dispatches to both parsers based on payload shape.
        socket.On("N", async ctx =>
        {
            await HandleSocketEventAsync(ctx);
        });
    }

    private async Task HandleSocketEventAsync(SocketIOClient.IEventContext ctx)
    {
        try
        {
            var raw = ctx.GetValue<JsonElement>(0);

            // Dispatch: price updates have "Rates"+"Symbol", table updates have "t"
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("t", out _))
                HandleTableUpdate(raw);

            // Always try price parsing — some payloads may be string-wrapped
            await HandlePriceUpdateAsync(raw);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FXCM: error handling socket event");
        }
    }

    private async Task HandlePriceUpdateAsync(JsonElement raw)
    {
        try
        {
            string? symbol = null;
            decimal bid = 0, ask = 0;
            DateTime timestamp = DateTime.UtcNow;

            if (raw.ValueKind == JsonValueKind.Object)
            {
                if (raw.TryGetProperty("Symbol", out var sym))
                    symbol = sym.GetString();

                if (raw.TryGetProperty("Rates", out var rates) && rates.ValueKind == JsonValueKind.Array)
                {
                    bid = rates[0].GetDecimal();
                    ask = rates[1].GetDecimal();
                }

                if (raw.TryGetProperty("Updated", out var upd))
                {
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(upd.GetInt64()).UtcDateTime;
                }
            }
            else if (raw.ValueKind == JsonValueKind.String)
            {
                var json = raw.GetString();
                if (json != null)
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Symbol", out var sym))
                        symbol = sym.GetString();
                    if (root.TryGetProperty("Rates", out var rates) && rates.ValueKind == JsonValueKind.Array)
                    {
                        bid = rates[0].GetDecimal();
                        ask = rates[1].GetDecimal();
                    }
                    if (root.TryGetProperty("Updated", out var upd))
                        timestamp = DateTimeOffset.FromUnixTimeSeconds(upd.GetInt64()).UtcDateTime;
                }
            }

            if (symbol != null && _priceCallbacks.Count > 0)
            {
                foreach (var cb in _priceCallbacks.Values)
                {
                    await cb(symbol, bid, ask, timestamp);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FXCM: error parsing price update from socket");
        }
    }

    private void HandleTableUpdate(JsonElement raw)
    {
        try
        {
            if (!raw.TryGetProperty("t", out var tableType))
                return;

            var t = tableType.GetInt32();

            // Order table updates (t=3) may contain tradeId after a fill.
            if (t == 3)
            {
                var orderId = raw.TryGetProperty("orderId", out var oid) ? oid.ToString() : null;
                var tradeId = raw.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;

                if (orderId != null && tradeId != null && tradeId != "0" && tradeId != "")
                {
                    _orderToTradeId[orderId] = (tradeId, DateTime.UtcNow);
                    _logger.LogDebug("FXCM socket: mapped orderId={OrderId} → tradeId={TradeId}", orderId, tradeId);
                }
            }

            // ClosedPosition table updates (t=5) contain close price and filled quantity.
            if (t == 5)
            {
                var tradeId = raw.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;

                if (tradeId != null && raw.TryGetProperty("close", out var closeElem))
                {
                    decimal? filledLots = null;
                    if (raw.TryGetProperty("amountK", out var amountK))
                        filledLots = FxcmOrderExecutor.AmountKToLots(amountK.GetDecimal());

                    _closeDetails[tradeId] = (closeElem.GetDecimal(), filledLots, DateTime.UtcNow);
                    _logger.LogDebug(
                        "FXCM socket: cached close details for tradeId={TradeId} closePrice={ClosePrice} filledLots={FilledLots}",
                        tradeId, closeElem.GetDecimal(), filledLots);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FXCM: error parsing table update from socket");
        }
    }

    // ── Price streaming ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string RegisterPriceCallback(Func<string, decimal, decimal, DateTime, Task> onPrice)
    {
        var id = Guid.NewGuid().ToString("N");
        _priceCallbacks[id] = onPrice;
        return id;
    }

    /// <inheritdoc/>
    public void UnregisterPriceCallback(string callbackId)
    {
        _priceCallbacks.TryRemove(callbackId, out _);
    }

    // ── orderId → tradeId resolution ─────────────────────────────────────────

    /// <inheritdoc/>
    public void RegisterOrderTradeMapping(string orderId, string tradeId)
    {
        _orderToTradeId[orderId] = (tradeId, DateTime.UtcNow);
        _logger.LogDebug("FXCM mapped orderId={OrderId} → tradeId={TradeId}", orderId, tradeId);
    }

    /// <inheritdoc/>
    public string? TryGetTradeId(string orderId)
    {
        if (_orderToTradeId.TryGetValue(orderId, out var entry))
            return entry.TradeId;
        return null;
    }

    /// <inheritdoc/>
    public async Task<string?> ResolveTradeIdAsync(
        string orderId, CancellationToken ct, int maxAttempts = 5, int delayMs = 500)
    {
        if (_orderToTradeId.TryGetValue(orderId, out var cached))
            return cached.TradeId;

        using var client = await GetAuthenticatedClientAsync(ct);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(
                    "/trading/get_model?models=OpenPosition", ct);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    JsonElement positions = default;
                    if (!root.TryGetProperty("open_positions", out positions) &&
                        !root.TryGetProperty("openPositions", out positions))
                    {
                        // skip
                    }

                    if (positions.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pos in positions.EnumerateArray())
                        {
                            var posTradeId = pos.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;
                            var posOrderId = pos.TryGetProperty("orderId", out var oid) ? oid.ToString() : null;

                            if (posTradeId != null && posOrderId != null)
                            {
                                _orderToTradeId[posOrderId] = (posTradeId, DateTime.UtcNow);

                                if (string.Equals(posOrderId, orderId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger.LogDebug(
                                        "FXCM resolved orderId={OrderId} → tradeId={TradeId} on attempt {Attempt}",
                                        orderId, posTradeId, attempt);
                                    return posTradeId;
                                }
                            }
                        }
                    }
                }

                if (attempt < maxAttempts)
                    await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FXCM ResolveTradeId attempt {Attempt}/{Max} failed for orderId={OrderId}",
                    attempt, maxAttempts, orderId);
                if (attempt < maxAttempts)
                    await Task.Delay(delayMs, ct);
            }
        }

        _logger.LogWarning(
            "FXCM could not resolve tradeId for orderId={OrderId} after {Attempts} attempts",
            orderId, maxAttempts);
        return null;
    }

    /// <inheritdoc/>
    public void RemoveMapping(string orderId)
    {
        _orderToTradeId.TryRemove(orderId, out _);
    }

    // ── Close details cache ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public (decimal ClosePrice, decimal? FilledLots)? TryGetCloseDetails(string tradeId)
    {
        if (_closeDetails.TryGetValue(tradeId, out var entry))
            return (entry.ClosePrice, entry.FilledLots);
        return null;
    }

    /// <inheritdoc/>
    public void RegisterCloseDetails(string tradeId, decimal closePrice, decimal? filledLots)
    {
        _closeDetails[tradeId] = (closePrice, filledLots, DateTime.UtcNow);
    }

    // ── In-flight order tracking ────────────────────────────────────────────

    /// <inheritdoc/>
    public bool TryMarkOrderInFlight(long orderId)
    {
        var now = DateTime.UtcNow;

        // Check if already in-flight and not expired
        if (_inFlightOrders.TryGetValue(orderId, out var existing) && now - existing.CreatedUtc < InFlightTtl)
            return false;

        _inFlightOrders[orderId] = (string.Empty, now);
        return true;
    }

    /// <inheritdoc/>
    public void CompleteInFlightOrder(long orderId, string brokerOrderId)
    {
        _inFlightOrders[orderId] = (brokerOrderId, DateTime.UtcNow);
    }

    /// <inheritdoc/>
    public void ClearInFlightOrder(long orderId)
    {
        _inFlightOrders.TryRemove(orderId, out _);
    }

    /// <inheritdoc/>
    public string? GetInFlightBrokerOrderId(long orderId)
    {
        if (_inFlightOrders.TryGetValue(orderId, out var entry)
            && !string.IsNullOrEmpty(entry.BrokerOrderId))
            return entry.BrokerOrderId;
        return null;
    }

    /// <summary>
    /// Removes orderId→tradeId mappings older than <see cref="MappingTtl"/>.
    /// Called opportunistically from <see cref="EnsureSessionAsync"/> to avoid unbounded growth.
    /// </summary>
    private void PurgeStaleMappings()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPurgeUtc < TimeSpan.FromHours(1))
            return;

        _lastPurgeUtc = now;
        var cutoff = now - MappingTtl;
        var staleKeys = _orderToTradeId
            .Where(kvp => kvp.Value.CreatedUtc < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
            _orderToTradeId.TryRemove(key, out _);

        if (staleKeys.Count > 0)
            _logger.LogDebug("FXCM purged {Count} stale orderId→tradeId mappings", staleKeys.Count);

        // Purge stale close details
        var staleCloseKeys = _closeDetails
            .Where(kvp => kvp.Value.CreatedUtc < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleCloseKeys)
            _closeDetails.TryRemove(key, out _);

        // Purge expired in-flight orders
        var inFlightCutoff = now - InFlightTtl;
        var staleInFlightKeys = _inFlightOrders
            .Where(kvp => kvp.Value.CreatedUtc < inFlightCutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in staleInFlightKeys)
            _inFlightOrders.TryRemove(key, out _);
    }

    // ── EngineConfig reader ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<int> GetConfigIntAsync(string key, int fallback, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var db           = readContext.GetDbContext();

            var entry = await db.Set<Domain.Entities.EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);

            if (entry != null && int.TryParse(entry.Value, out var parsed))
                return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FXCM GetConfigIntAsync failed for key={Key}, using fallback={Fallback}", key, fallback);
        }

        return fallback;
    }

    // ── Rate limiting ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task ThrottleAsync(CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        try
        {
            // Enforce minimum spacing between requests
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastRequestTicks);
            var elapsed = now - last;

            if (elapsed < MinRequestIntervalTicks)
            {
                var delayTicks = MinRequestIntervalTicks - elapsed;
                await Task.Delay(TimeSpan.FromTicks(delayTicks), ct);
            }

            Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<FxcmRawCredentials> GetCredentialsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db           = readContext.GetDbContext();

        var broker = await db.Set<Broker>()
            .FirstOrDefaultAsync(x => x.BrokerType == BrokerType.Fxcm && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("No FXCM broker configured in database");

        var account = await db.Set<TradingAccount>()
            .FirstOrDefaultAsync(x => x.BrokerId == broker.Id && x.IsActive && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("No active trading account found for the FXCM broker");

        if (string.IsNullOrEmpty(broker.ApiKey))
            throw new InvalidOperationException("FXCM broker has no API key configured");

        return new FxcmRawCredentials(
            AccessToken: broker.ApiKey,
            BaseUrl: broker.BaseUrl,
            AccountId: account.AccountId);
    }

    private async Task DisconnectSocketAsync(SocketIOClient.SocketIO socket)
    {
        try
        {
            if (socket.Connected)
                await socket.DisconnectAsync();
            socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FXCM: error disconnecting socket (non-critical)");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var session = _currentSession;
        if (session != null)
        {
            await DisconnectSocketAsync(session.Socket);
            _currentSession = null;
        }
        _sessionLock.Dispose();
        _rateLimiter.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ── Internal types ───────────────────────────────────────────────────────

    private record FxcmRawCredentials(string AccessToken, string BaseUrl, string AccountId);

    private record FxcmSession(
        string SocketId,
        string AccessToken,
        string BaseUrl,
        string AccountId,
        SocketIOClient.SocketIO Socket);
}

/// <summary>
/// Manages FXCM Socket.IO connection, REST auth, price streaming, and orderId→tradeId resolution.
/// </summary>
public interface IFxcmSessionManager
{
    /// <summary>
    /// Returns an HttpClient pre-configured with the FXCM base URL and
    /// a valid "Bearer {socket_id}{access_token}" authorization header.
    /// Establishes a Socket.IO connection if needed.
    /// </summary>
    Task<HttpClient> GetAuthenticatedClientAsync(CancellationToken ct);

    /// <summary>
    /// Forces the current session to be discarded and socket disconnected.
    /// The next call to <see cref="GetAuthenticatedClientAsync"/> will reconnect.
    /// </summary>
    Task InvalidateSessionAsync();

    /// <summary>Returns the active FXCM trading account ID.</summary>
    Task<string> GetAccountIdAsync(CancellationToken ct);

    /// <summary>Whether the Socket.IO connection is currently established.</summary>
    bool IsSocketConnected { get; }

    /// <summary>
    /// Registers a callback for real-time price updates from the FXCM socket.
    /// The callback receives (fxcmSymbol, bid, ask, timestamp).
    /// Returns a callback ID for unregistration.
    /// </summary>
    string RegisterPriceCallback(Func<string, decimal, decimal, DateTime, Task> onPrice);

    /// <summary>Unregisters a previously registered price callback.</summary>
    void UnregisterPriceCallback(string callbackId);

    /// <summary>Registers a known orderId→tradeId mapping.</summary>
    void RegisterOrderTradeMapping(string orderId, string tradeId);

    /// <summary>Returns the tradeId for a given orderId if already known, else null.</summary>
    string? TryGetTradeId(string orderId);

    /// <summary>
    /// Polls the FXCM OpenPosition table to discover the tradeId for a given orderId.
    /// </summary>
    Task<string?> ResolveTradeIdAsync(
        string orderId, CancellationToken ct, int maxAttempts = 5, int delayMs = 500);

    /// <summary>Removes an orderId mapping.</summary>
    void RemoveMapping(string orderId);

    /// <summary>Returns cached close details for a tradeId if available (from socket or prior poll), else null.</summary>
    (decimal ClosePrice, decimal? FilledLots)? TryGetCloseDetails(string tradeId);

    /// <summary>Caches close details for a tradeId (called after polling ClosedPosition table).</summary>
    void RegisterCloseDetails(string tradeId, decimal closePrice, decimal? filledLots);

    /// <summary>
    /// Attempts to mark an order as in-flight. Returns false if the order is already in-flight
    /// (idempotency guard to prevent duplicate broker submissions).
    /// </summary>
    bool TryMarkOrderInFlight(long orderId);

    /// <summary>Records the broker order ID for a completed in-flight order.</summary>
    void CompleteInFlightOrder(long orderId, string brokerOrderId);

    /// <summary>Removes the in-flight marker (e.g., on failure so retries are possible).</summary>
    void ClearInFlightOrder(long orderId);

    /// <summary>Returns the broker order ID if this order was already submitted, else null.</summary>
    string? GetInFlightBrokerOrderId(long orderId);

    /// <summary>
    /// Reads an integer value from the EngineConfig table by key.
    /// Returns <paramref name="fallback"/> if the key doesn't exist or can't be parsed.
    /// </summary>
    Task<int> GetConfigIntAsync(string key, int fallback, CancellationToken ct);

    /// <summary>
    /// Throttles outgoing REST API calls to stay within FXCM's rate limit (~10 req/s).
    /// Callers should await this before making an HTTP request.
    /// </summary>
    Task ThrottleAsync(CancellationToken ct);
}
