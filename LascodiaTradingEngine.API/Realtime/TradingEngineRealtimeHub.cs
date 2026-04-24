using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LascodiaTradingEngine.API.Realtime;

/// <summary>
/// Single SignalR hub the admin UI subscribes to for live engine events. Each connection is
/// auto-joined to a per-account group so the per-event relay handlers can push to
/// <c>account:{TradingAccountId}</c> rather than broadcasting to every client.
/// </summary>
/// <remarks>
/// <para>
/// Read-only push channel — clients do not invoke server methods here. All command-style
/// interactions go through the REST API. Designed-in choice; see DESIGN_DOCS.md §E1.
/// </para>
/// <para>
/// JWT auth runs over the WebSocket upgrade via <c>access_token</c> query string handled
/// by <c>JwtBearerOptions.OnMessageReceived</c> in <c>Program.cs</c>; browsers can't send
/// <c>Authorization</c> headers on the upgrade request.
/// </para>
/// </remarks>
[Authorize]
public class TradingEngineRealtimeHub : Hub
{
    /// <summary>Format the SignalR group name for a given trading account id.</summary>
    public static string GroupForAccount(long tradingAccountId) => $"account:{tradingAccountId}";

    /// <summary>Format the SignalR group name for a presence room keyed on a route path.</summary>
    public static string GroupForRoom(string routeKey) => $"room:{routeKey}";

    /// <summary>
    /// Per-connection bookkeeping so we can emit <c>presenceLeft</c> for every
    /// room the operator was in when their connection drops — without iterating
    /// every known room on disconnect. Keyed by <c>ConnectionId</c> because one
    /// tab = one connection, and a tab can occupy multiple rooms (e.g. detail
    /// page opened in a split layout).
    /// </summary>
    private static readonly ConcurrentDictionary<string, HashSet<string>> Rooms = new();

    /// <summary>
    /// On connect, read the <c>tradingAccountId</c> claim from the principal and add the
    /// caller's connection to <c>account:{tradingAccountId}</c>. Tokens without the claim
    /// (e.g. the shared-library dev <c>/auth/token</c> endpoint) are kept connected but
    /// not subscribed to any account group — they only receive the broadcast stream
    /// (<c>orderFilled</c>, <c>positionOpened</c>, etc.), which is what dev dashboards
    /// actually consume. Aborting these connections made the dev banner show
    /// "Live updates offline" on every page load.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var accountClaim = Context.User?.FindFirst("tradingAccountId")?.Value;
        if (long.TryParse(accountClaim, out var accountId))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupForAccount(accountId));

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var accountClaim = Context.User?.FindFirst("tradingAccountId")?.Value;
        if (long.TryParse(accountClaim, out var accountId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForAccount(accountId));

        // Broadcast a `presenceLeft` for every room this connection was in so
        // peers see the departure even when the tab closed without an explicit
        // LeaveRoom.
        if (Rooms.TryRemove(Context.ConnectionId, out var rooms) && long.TryParse(accountClaim, out var id))
        {
            foreach (var routeKey in rooms)
            {
                await Clients.Group(GroupForRoom(routeKey))
                    .SendAsync("presenceLeft", new { accountId = id, routeKey });
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client-invoked. Adds the connection to a presence room and broadcasts
    /// <c>presenceJoined</c> to every other member. No-op if the connection
    /// is already in the room.
    /// </summary>
    public async Task EnterRoom(string routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey)) return;
        if (!long.TryParse(Context.User?.FindFirst("tradingAccountId")?.Value, out var accountId)) return;

        var set = Rooms.GetOrAdd(Context.ConnectionId, _ => new HashSet<string>());
        lock (set)
        {
            if (!set.Add(routeKey)) return; // already in room
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForRoom(routeKey));
        await Clients.Group(GroupForRoom(routeKey))
            .SendAsync("presenceJoined", new { accountId, routeKey });
    }

    /// <summary>
    /// Client-invoked. Removes the connection from a presence room and broadcasts
    /// <c>presenceLeft</c>. Idempotent.
    /// </summary>
    public async Task LeaveRoom(string routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey)) return;
        if (!long.TryParse(Context.User?.FindFirst("tradingAccountId")?.Value, out var accountId)) return;

        if (Rooms.TryGetValue(Context.ConnectionId, out var set))
        {
            lock (set) { set.Remove(routeKey); }
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForRoom(routeKey));
        await Clients.Group(GroupForRoom(routeKey))
            .SendAsync("presenceLeft", new { accountId, routeKey });
    }
}
