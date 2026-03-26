using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LascodiaTradingEngine.Application.Bridge.DTOs;
using LascodiaTradingEngine.Application.Bridge.Options;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReportBatch;
using LascodiaTradingEngine.Application.TradeSignals.Queries.GetPendingSignalsByAccount;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Runs a persistent TCP listener that EA instances connect to for low-latency
/// signal delivery and execution report ingestion.
///
/// Architecture:
/// - One <see cref="SharedSignalPollerAsync"/> background loop polls the DB once
///   per cycle (O(1) regardless of connection count) and fans out to all matching
///   account sessions via <see cref="ITcpBridgeSessionRegistry"/>.
/// - Each accepted connection gets a <see cref="HandleConnectionAsync"/> task that
///   performs JWT auth, registers the session, and runs the report receive loop.
/// </summary>
public class TcpBridgeWorker : BackgroundService
{
    private static readonly TimeSpan SignalPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectDelay    = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly BridgeOptions _options;
    private readonly ITcpBridgeSessionRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TcpBridgeWorker> _logger;

    public TcpBridgeWorker(
        IOptions<BridgeOptions> options,
        ITcpBridgeSessionRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<TcpBridgeWorker> logger)
    {
        _options     = options.Value;
        _registry    = registry;
        _scopeFactory = scopeFactory;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("TcpBridgeWorker disabled (BridgeOptions.Enabled=false)");
            return;
        }

        _logger.LogInformation("TcpBridgeWorker starting on {BindAddress}:{Port}",
            _options.BindAddress, _options.Port);

        // Run signal poller and TCP listener concurrently
        await Task.WhenAll(
            SharedSignalPollerAsync(stoppingToken),
            TcpListenerLoopAsync(stoppingToken));
    }

    // ── TCP listener ──────────────────────────────────────────────────────────

    private async Task TcpListenerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                var bindAddress = IPAddress.TryParse(_options.BindAddress, out var ip)
                    ? ip : IPAddress.Any;

                listener = new TcpListener(bindAddress, _options.Port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start(_options.TcpBacklog);

                _logger.LogInformation("TcpBridgeWorker listening on {BindAddress}:{Port}",
                    _options.BindAddress, _options.Port);

                while (!ct.IsCancellationRequested)
                {
                    var tcpClient = await listener.AcceptTcpClientAsync(ct);

                    if (_registry.TotalSessionCount >= _options.MaxTotalConnections)
                    {
                        _logger.LogWarning("Bridge: max total connections ({Max}) reached — rejecting {Remote}",
                            _options.MaxTotalConnections,
                            tcpClient.Client.RemoteEndPoint);
                        tcpClient.Dispose();
                        continue;
                    }

                    // Fire-and-forget per-connection task
                    _ = HandleConnectionAsync(tcpClient, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TcpBridgeWorker: listener error, retrying in {Delay}", ReconnectDelay);
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            finally
            {
                listener?.Stop();
            }
        }
    }

    // ── Per-connection handler ────────────────────────────────────────────────

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var remote    = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var sessionId = Guid.NewGuid().ToString("N");
        long accountId = 0;

        using (client)
        {
        await using var stream = client.GetStream();
            try
            {
                // ── Auth handshake ──────────────────────────────────────────
                var authLine = await ReadLineAsync(stream, ct);
                if (authLine is null)
                {
                    _logger.LogDebug("Bridge: {Remote} disconnected before sending auth", remote);
                    return;
                }

                var authMsg = JsonSerializer.Deserialize<BridgeAuthMessage>(authLine, JsonOpts);
                if (authMsg?.Token is null)
                {
                    await WriteLineAsync(stream,
                        JsonSerializer.Serialize(new BridgeAuthFailMessage("auth_fail", "Missing token"), JsonOpts), ct);
                    return;
                }

                accountId = ValidateBridgeAuth(authMsg.Token);
                if (accountId <= 0)
                {
                    await WriteLineAsync(stream,
                        JsonSerializer.Serialize(new BridgeAuthFailMessage("auth_fail", "Invalid or expired token"), JsonOpts), ct);
                    return;
                }

                // ── Per-account connection limit ────────────────────────────
                if (_registry.SessionCountForAccount(accountId) >= _options.MaxConnectionsPerAccount)
                {
                    _logger.LogWarning("Bridge: account {AccountId} at connection limit ({Max}) — rejecting {Remote}",
                        accountId, _options.MaxConnectionsPerAccount, remote);
                    await WriteLineAsync(stream,
                        JsonSerializer.Serialize(new BridgeAuthFailMessage("auth_fail", "Too many connections for this account"), JsonOpts), ct);
                    return;
                }

                // ── Register session ────────────────────────────────────────
                _registry.RegisterSession(sessionId, accountId,
                    json => WriteLineAsync(stream, json, ct));

                await WriteLineAsync(stream,
                    JsonSerializer.Serialize(new BridgeAuthOkMessage("auth_ok"), JsonOpts), ct);

                _logger.LogInformation("Bridge: session {SessionId} opened for account {AccountId} from {Remote}",
                    sessionId, accountId, remote);

                // ── Report receive loop ─────────────────────────────────────
                await ReportReceiveLoopAsync(stream, sessionId, accountId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                _logger.LogDebug("Bridge: session {SessionId} (account={AccountId}) disconnected: {Msg}",
                    sessionId, accountId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bridge: session {SessionId} (account={AccountId}) error",
                    sessionId, accountId);
            }
            finally
            {
                if (accountId > 0)
                {
                    _registry.UnregisterSession(sessionId);
                    _logger.LogInformation("Bridge: session {SessionId} closed (account={AccountId})",
                        sessionId, accountId);
                }
            }
        }
    }

    private async Task ReportReceiveLoopAsync(
        NetworkStream stream, string sessionId, long accountId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineAsync(stream, ct);
            if (line is null) return; // EOF / disconnect

            string? msgType = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                doc.RootElement.TryGetProperty("type", out var typeProp);
                msgType = typeProp.GetString();
            }
            catch { /* ignore malformed JSON */ }

            if (msgType == "report")
            {
                await ProcessReportAsync(line, accountId, ct);
            }
            else if (msgType == "reauth")
            {
                // JWT refresh — validate and update accountId if changed (should stay same)
                var authMsg = JsonSerializer.Deserialize<BridgeAuthMessage>(line, JsonOpts);
                if (authMsg?.Token is not null && ValidateBridgeAuth(authMsg.Token) == accountId)
                    await WriteLineAsync(stream,
                        JsonSerializer.Serialize(new BridgeAuthOkMessage("auth_ok"), JsonOpts), ct);
                // Else: ignore (close will happen when next push fails)
            }
            else if (msgType == "ping")
            {
                await WriteLineAsync(stream,
                    JsonSerializer.Serialize(new BridgePongMessage("pong"), JsonOpts), ct);
            }
            // Unknown message types silently ignored
        }
    }

    // ── Report processing ─────────────────────────────────────────────────────

    private async Task ProcessReportAsync(string reportJson, long senderAccountId, CancellationToken ct)
    {
        BridgeReportMessage? report;
        try
        {
            report = JsonSerializer.Deserialize<BridgeReportMessage>(reportJson, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bridge: malformed report JSON from account {AccountId}", senderAccountId);
            return;
        }

        if (report is null || report.EngineOrderId <= 0) return;

        // Ownership check: verify the order belongs to the sending account
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

        var order = await db.Set<Domain.Entities.Order>()
            .AsNoTracking()
            .Where(o => o.Id == report.EngineOrderId && o.TradingAccountId == senderAccountId)
            .Select(o => new { o.Id, o.TradingAccountId })
            .FirstOrDefaultAsync(ct);

        if (order is null)
        {
            _logger.LogWarning("Bridge: account {AccountId} sent report for order {OrderId} — not owned, discarding",
                senderAccountId, report.EngineOrderId);
            return;
        }

        // Dispatch via existing SubmitExecutionReportBatch command
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var statusStr = report.Status switch
        {
            1  => "Filled",
            2  => "Rejected",
            3  => "Cancelled",
            _  => "Filled"
        };

        var cmd = new SubmitExecutionReportBatchCommand
        {
            Reports =
            [
                new ExecutionReportItem
                {
                    OrderId         = report.EngineOrderId,
                    BrokerOrderId   = report.Mt5OrderTicket > 0 ? report.Mt5OrderTicket.ToString() : null,
                    FilledPrice     = report.FilledPrice > 0 ? (decimal)report.FilledPrice : null,
                    FilledQuantity  = report.FilledVolume > 0 ? (decimal)report.FilledVolume : null,
                    Status          = statusStr,
                    RejectionReason = !string.IsNullOrEmpty(report.ErrorMessage) ? report.ErrorMessage : null,
                    FilledAt        = report.Timestamp > 0
                                    ? DateTimeOffset.FromUnixTimeSeconds(report.Timestamp).UtcDateTime
                                    : null,
                }
            ]
        };

        await mediator.Send(cmd, ct);
    }

    // ── Shared signal poller ──────────────────────────────────────────────────

    /// <summary>
    /// Single shared DB poll loop. Runs O(1) DB queries regardless of how many EA
    /// connections are active. Fans out results to matching sessions via the registry.
    /// </summary>
    private async Task SharedSignalPollerAsync(CancellationToken ct)
    {
        _logger.LogInformation("TcpBridgeWorker: shared signal poller starting");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SignalPollInterval, ct);

                if (_registry.TotalSessionCount == 0) continue;

                using var scope   = _scopeFactory.CreateScope();
                var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
                var result        = await mediator.Send(new GetPendingSignalsByAccountQuery(), ct);

                if (!result.Status || result.Data is null || result.Data.Count == 0)
                    continue;

                foreach (var item in result.Data)
                {
                    if (_registry.SessionCountForAccount(item.AccountId) == 0)
                        continue;

                    var msg = new BridgeSignalMessage(
                        Type:              "signal",
                        Id:                item.SignalId,
                        Symbol:            item.Symbol,
                        Direction:         item.Direction,
                        ExecutionType:     item.ExecutionType,
                        EntryPrice:        item.EntryPrice,
                        StopLoss:          item.StopLoss,
                        TakeProfit:        item.TakeProfit,
                        LotSize:           item.LotSize,
                        Confidence:        item.Confidence,
                        StrategyId:        item.StrategyId,
                        StrategyName:      item.StrategyName,
                        ExpiresAt:         item.ExpiresAtUnix,
                        TrailingEnabled:   false,
                        TrailingType:      0,
                        TrailingPeriod:    0,
                        TrailingMultiplier: 0.0,
                        TrailingTimeframe: string.Empty,
                        Notes:             string.Empty,
                        EngineOrderId:     item.EngineOrderId,
                        PartialFillCount:  0,
                        CreatedAt:         item.CreatedAtUnix);

                    var json = JsonSerializer.Serialize(msg, JsonOpts);
                    await _registry.PushToAccountAsync(item.AccountId, json, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TcpBridgeWorker: signal poller error");
            }
        }

        _logger.LogInformation("TcpBridgeWorker: shared signal poller stopped");
    }

    // ── JWT validation ────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the JWT and extracts the trading account ID from the "tradingAccountId" claim.
    /// Returns the account ID on success, 0 on failure.
    /// </summary>
    private static long ValidateBridgeAuth(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token)) return 0;

            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo < DateTime.UtcNow) return 0;

            // Claim name matches TradingAccountTokenGenerator.cs line 31: "tradingAccountId"
            var claim = jwt.Claims.FirstOrDefault(c => c.Type == "tradingAccountId");
            if (claim is null) return 0;

            return long.TryParse(claim.Value, out var id) ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ── I/O helpers ───────────────────────────────────────────────────────────

    private static async Task WriteLineAsync(
        NetworkStream stream, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string?> ReadLineAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var sb  = new StringBuilder(512);
        var buf = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(buf, ct);
            if (read == 0) return null; // EOF
            char c = (char)buf[0];
            if (c == '\n') return sb.ToString();
            if (c != '\r') sb.Append(c);
            if (sb.Length > 8192) return null; // Oversized message — disconnect
        }
    }
}
