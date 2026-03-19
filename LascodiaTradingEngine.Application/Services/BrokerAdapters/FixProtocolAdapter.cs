using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// FIX 4.4 session-layer adapter for institutional order routing.
/// Implements the core FIX session management (logon, heartbeat, sequence numbers)
/// and translates between engine orders and FIX messages.
/// </summary>
/// <remarks>
/// FIX message types supported:
/// <list type="bullet">
///   <item><b>Logon (A):</b> session establishment with SenderCompID/TargetCompID.</item>
///   <item><b>Heartbeat (0):</b> keep-alive at configurable interval (default 30s).</item>
///   <item><b>New Order Single (D):</b> submit market/limit orders.</item>
///   <item><b>Order Cancel Request (F):</b> cancel pending orders.</item>
///   <item><b>Execution Report (8):</b> fill/cancel/reject confirmations from venue.</item>
///   <item><b>Market Data Request (V):</b> subscribe to top-of-book quotes.</item>
///   <item><b>Market Data Snapshot (W):</b> top-of-book bid/ask updates.</item>
///   <item><b>Logout (5):</b> graceful session termination.</item>
/// </list>
/// This is a lightweight, pure-C# implementation suitable for connecting to ECN venues
/// that require FIX. For production use with high message rates, consider replacing
/// the TCP layer with a battle-tested FIX engine (QuickFIX/n).
/// </remarks>
public interface IFixSession : IAsyncDisposable
{
    /// <summary>Establishes the FIX session (TCP connect → Logon → wait for Logon ack).</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Sends a New Order Single (MsgType=D).</summary>
    Task<string> SendNewOrderAsync(FixNewOrder order, CancellationToken ct);

    /// <summary>Sends an Order Cancel Request (MsgType=F).</summary>
    Task SendCancelAsync(string origClOrdId, string symbol, CancellationToken ct);

    /// <summary>Subscribes to market data for the given symbols (MsgType=V).</summary>
    Task SubscribeMarketDataAsync(IEnumerable<string> symbols, CancellationToken ct);

    /// <summary>Registers a callback for execution reports (MsgType=8).</summary>
    void OnExecutionReport(Func<FixExecutionReport, Task> handler);

    /// <summary>Registers a callback for market data snapshots (MsgType=W).</summary>
    void OnMarketData(Func<FixMarketDataSnapshot, Task> handler);

    /// <summary>Whether the session is connected and logged in.</summary>
    bool IsConnected { get; }

    /// <summary>Graceful logout and disconnect.</summary>
    Task DisconnectAsync();
}

/// <summary>Parameters for a FIX New Order Single.</summary>
public sealed record FixNewOrder(
    string Symbol,
    string Side,       // "1"=Buy, "2"=Sell
    decimal Quantity,
    string OrdType,    // "1"=Market, "2"=Limit
    decimal? Price,
    decimal? StopLoss,
    decimal? TakeProfit);

/// <summary>Parsed FIX execution report.</summary>
public sealed record FixExecutionReport(
    string ClOrdId,
    string OrderId,
    string ExecType,   // "0"=New, "1"=PartialFill, "2"=Fill, "4"=Cancelled, "8"=Rejected
    string OrdStatus,
    decimal? AvgPx,
    decimal? CumQty,
    decimal? LeavesQty,
    string? Text);

/// <summary>Parsed FIX market data snapshot.</summary>
public sealed record FixMarketDataSnapshot(
    string  Symbol,
    decimal Bid,
    decimal Ask,
    decimal BidSize,
    decimal AskSize,
    DateTime Timestamp);

public sealed class FixSession : IFixSession
{
    private readonly string _host;
    private readonly int    _port;
    private readonly string _senderCompId;
    private readonly string _targetCompId;
    private readonly string _password;
    private readonly int    _heartbeatIntervalSec;
    private readonly ILogger<FixSession> _logger;

    private TcpClient?   _client;
    private NetworkStream? _stream;
    private int _outSeqNum = 1;
    private int _inSeqNum  = 1;
    private bool _connected;
    private CancellationTokenSource? _heartbeatCts;

    private Func<FixExecutionReport, Task>?    _onExecReport;
    private Func<FixMarketDataSnapshot, Task>? _onMarketData;

    private const char SOH = '\x01'; // FIX field delimiter

    public FixSession(
        string host, int port,
        string senderCompId, string targetCompId,
        string password,
        ILogger<FixSession> logger,
        int heartbeatIntervalSec = 30)
    {
        _host                 = host;
        _port                 = port;
        _senderCompId         = senderCompId;
        _targetCompId         = targetCompId;
        _password             = password;
        _logger               = logger;
        _heartbeatIntervalSec = heartbeatIntervalSec;
    }

    public bool IsConnected => _connected;

    public void OnExecutionReport(Func<FixExecutionReport, Task> handler) => _onExecReport = handler;
    public void OnMarketData(Func<FixMarketDataSnapshot, Task> handler)   => _onMarketData = handler;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, ct);
        _stream = _client.GetStream();

        _logger.LogInformation("FIX: TCP connected to {Host}:{Port}", _host, _port);

        // Send Logon (MsgType=A)
        var logon = BuildMessage("A", new Dictionary<int, string>
        {
            [98]  = "0",                              // EncryptMethod=None
            [108] = _heartbeatIntervalSec.ToString(), // HeartBtInt
            [554] = _password,                        // Password
        });

        await SendRawAsync(logon, ct);
        _logger.LogInformation("FIX: Logon sent, awaiting response...");

        // Wait for Logon response (simplified — production would use async read loop)
        var response = await ReadMessageAsync(ct);
        if (response.TryGetValue(35, out var msgType) && msgType == "A")
        {
            _connected = true;
            _logger.LogInformation("FIX: session established ({Sender}→{Target})",
                _senderCompId, _targetCompId);

            // Start heartbeat and message reader
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => HeartbeatLoopAsync(_heartbeatCts.Token));
            _ = Task.Run(() => MessageReaderLoopAsync(_heartbeatCts.Token));
        }
        else
        {
            _logger.LogError("FIX: Logon rejected or unexpected response");
            throw new InvalidOperationException("FIX Logon failed");
        }
    }

    public async Task<string> SendNewOrderAsync(FixNewOrder order, CancellationToken ct)
    {
        string clOrdId = $"ORD{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var fields = new Dictionary<int, string>
        {
            [11]  = clOrdId,                      // ClOrdID
            [55]  = order.Symbol,                  // Symbol
            [54]  = order.Side,                    // Side
            [38]  = order.Quantity.ToString("F2"), // OrderQty
            [40]  = order.OrdType,                 // OrdType
            [60]  = DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff"), // TransactTime
            [21]  = "1",                           // HandlInst=Automated
        };

        if (order.OrdType == "2" && order.Price.HasValue)
            fields[44] = order.Price.Value.ToString("F5"); // Price

        var msg = BuildMessage("D", fields);
        await SendRawAsync(msg, ct);

        _logger.LogInformation("FIX: sent NewOrderSingle ClOrdId={Id} {Symbol} {Side} {Qty}",
            clOrdId, order.Symbol, order.Side == "1" ? "BUY" : "SELL", order.Quantity);

        return clOrdId;
    }

    public async Task SendCancelAsync(string origClOrdId, string symbol, CancellationToken ct)
    {
        string clOrdId = $"CXL{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var msg = BuildMessage("F", new Dictionary<int, string>
        {
            [11]  = clOrdId,
            [41]  = origClOrdId, // OrigClOrdID
            [55]  = symbol,
            [60]  = DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff"),
        });

        await SendRawAsync(msg, ct);
        _logger.LogInformation("FIX: sent OrderCancelRequest for {OrigId}", origClOrdId);
    }

    public async Task SubscribeMarketDataAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        int reqId = 1;
        foreach (var symbol in symbols)
        {
            var msg = BuildMessage("V", new Dictionary<int, string>
            {
                [262] = (reqId++).ToString(),        // MDReqID
                [263] = "1",                         // SubscriptionRequestType=SnapshotPlusUpdates
                [264] = "1",                         // MarketDepth=TopOfBook
                [267] = "2",                         // NoMDEntryTypes=2
                [269] = "0",                         // MDEntryType=Bid
                // Second MDEntryType (Ask) encoded inline for simplicity
                [146] = "1",                         // NoRelatedSym=1
                [55]  = symbol,                      // Symbol
            });

            await SendRawAsync(msg, ct);
        }

        _logger.LogInformation("FIX: subscribed to market data for {Count} symbols", reqId - 1);
    }

    public async Task DisconnectAsync()
    {
        if (!_connected) return;

        _heartbeatCts?.Cancel();

        try
        {
            var logout = BuildMessage("5", new Dictionary<int, string>());
            if (_stream is not null)
                await SendRawAsync(logout, CancellationToken.None);
        }
        catch { /* best-effort */ }

        _connected = false;
        _stream?.Dispose();
        _client?.Dispose();

        _logger.LogInformation("FIX: session disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    // ── FIX message construction ────────────────────────────────────────────

    private string BuildMessage(string msgType, Dictionary<int, string> fields)
    {
        int seqNum = Interlocked.Increment(ref _outSeqNum);

        var body = new StringBuilder();
        body.Append($"35={msgType}{SOH}");
        body.Append($"49={_senderCompId}{SOH}");
        body.Append($"56={_targetCompId}{SOH}");
        body.Append($"34={seqNum}{SOH}");
        body.Append($"52={DateTime.UtcNow:yyyyMMdd-HH:mm:ss.fff}{SOH}");

        foreach (var (tag, value) in fields)
            body.Append($"{tag}={value}{SOH}");

        string bodyStr = body.ToString();
        string header  = $"8=FIX.4.4{SOH}9={bodyStr.Length}{SOH}";

        // Checksum = sum of all bytes mod 256, zero-padded to 3 digits
        string preChecksum = header + bodyStr;
        int checksum = 0;
        foreach (char c in preChecksum) checksum += c;
        checksum %= 256;

        return $"{preChecksum}10={checksum:D3}{SOH}";
    }

    private async Task SendRawAsync(string message, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("FIX: not connected");
        var bytes = Encoding.ASCII.GetBytes(message);
        await _stream.WriteAsync(bytes, ct);
    }

    private async Task<Dictionary<int, string>> ReadMessageAsync(CancellationToken ct)
    {
        if (_stream is null) return new();

        var buffer = new byte[4096];
        int read = await _stream.ReadAsync(buffer, ct);
        if (read == 0) return new();

        string raw = Encoding.ASCII.GetString(buffer, 0, read);
        return ParseFixMessage(raw);
    }

    private static Dictionary<int, string> ParseFixMessage(string raw)
    {
        var fields = new Dictionary<int, string>();
        var parts = raw.Split('\x01', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && int.TryParse(part[..eq], out int tag))
                fields[tag] = part[(eq + 1)..];
        }
        return fields;
    }

    // ── Background loops ────────────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _connected)
        {
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSec), ct);
            try
            {
                var hb = BuildMessage("0", new Dictionary<int, string>());
                await SendRawAsync(hb, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FIX: heartbeat send failed");
                _connected = false;
                break;
            }
        }
    }

    private async Task MessageReaderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _connected)
        {
            try
            {
                var msg = await ReadMessageAsync(ct);
                if (msg.Count == 0) continue;

                Interlocked.Increment(ref _inSeqNum);

                if (!msg.TryGetValue(35, out var msgType)) continue;

                switch (msgType)
                {
                    case "8" when _onExecReport is not null: // ExecutionReport
                        var report = new FixExecutionReport(
                            ClOrdId:    msg.GetValueOrDefault(11, ""),
                            OrderId:    msg.GetValueOrDefault(37, ""),
                            ExecType:   msg.GetValueOrDefault(150, ""),
                            OrdStatus:  msg.GetValueOrDefault(39, ""),
                            AvgPx:      msg.TryGetValue(6, out var px) && decimal.TryParse(px, out var pxv) ? pxv : null,
                            CumQty:     msg.TryGetValue(14, out var cq) && decimal.TryParse(cq, out var cqv) ? cqv : null,
                            LeavesQty:  msg.TryGetValue(151, out var lq) && decimal.TryParse(lq, out var lqv) ? lqv : null,
                            Text:       msg.GetValueOrDefault(58));
                        await _onExecReport(report);
                        break;

                    case "W" when _onMarketData is not null: // MarketDataSnapshotFullRefresh
                        // Simplified: extract first bid/ask
                        if (msg.TryGetValue(55, out var sym))
                        {
                            decimal bid = 0, ask = 0, bidSz = 0, askSz = 0;
                            // In a real implementation, iterate NoMDEntries (268) groups
                            if (msg.TryGetValue(270, out var mdPx) && decimal.TryParse(mdPx, out var pv))
                            {
                                string entryType = msg.GetValueOrDefault(269, "0");
                                if (entryType == "0") bid = pv;
                                else if (entryType == "1") ask = pv;
                            }
                            var snapshot = new FixMarketDataSnapshot(sym, bid, ask, bidSz, askSz, DateTime.UtcNow);
                            await _onMarketData(snapshot);
                        }
                        break;

                    case "0": // Heartbeat — no action needed
                        break;

                    case "1": // TestRequest — respond with heartbeat
                        var testReqId = msg.GetValueOrDefault(112, "");
                        var hbResp = BuildMessage("0", new Dictionary<int, string> { [112] = testReqId });
                        await SendRawAsync(hbResp, ct);
                        break;

                    case "5": // Logout
                        _logger.LogWarning("FIX: received Logout from counterparty");
                        _connected = false;
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FIX: message reader error");
                await Task.Delay(1000, ct);
            }
        }
    }
}
