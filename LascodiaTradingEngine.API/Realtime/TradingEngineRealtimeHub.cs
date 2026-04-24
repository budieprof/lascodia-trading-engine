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

    /// <summary>
    /// On connect, read the <c>tradingAccountId</c> claim from the principal and add the
    /// caller's connection to <c>account:{tradingAccountId}</c>. Connections without the
    /// claim are aborted — every authenticated browser session is scoped to one account.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var accountClaim = Context.User?.FindFirst("tradingAccountId")?.Value;
        if (!long.TryParse(accountClaim, out var accountId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupForAccount(accountId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var accountClaim = Context.User?.FindFirst("tradingAccountId")?.Value;
        if (long.TryParse(accountClaim, out var accountId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForAccount(accountId));

        await base.OnDisconnectedAsync(exception);
    }
}
