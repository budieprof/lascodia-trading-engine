# Engine Design Docs — Admin UI Backend Asks

Companion to the admin-UI upgrade. This document captures the architectural decisions required **before** implementing the deep items surfaced as blockers by the UI work:

- **E1** — Real-time push from engine to browser (WebSocket / SSE).
- **E7** — Batch order cancel, including atomicity semantics.
- **E9** — RBAC / operator roles on the JWT.
- **E10** — Token revocation / explicit logout.

Each section has: **context**, **recommended approach**, **alternatives considered**, **migration notes**, **what not to do**, **rough effort**.

The four *additive* items (E2 drawdown history, E3 ML training diagnostics, E4 drift report, E11 Swagger discovery) are implemented alongside this document and don't need design review — they're thin reads of data that already exists.

---

## E1 — Real-time push to browser (WebSocket)

### Context

The admin UI polls ~15 endpoints at 5–60 s intervals for live state. A single operator generates ~50 HTTP requests/min idle. That scales poorly with operators, clogs the engine's HTTP pipeline, and wastes ~95 % of those requests returning identical payloads.

The engine already publishes 22+ integration events via `IEventBus` (RabbitMQ or Kafka) covering every state change the UI cares about. A relay layer that forwards a curated subset of those events to connected browsers replaces most polling with push.

### Recommended approach: SignalR hub + per-event relay handlers

1. **Add `Microsoft.AspNetCore.SignalR` to `LascodiaTradingEngine.API.csproj`.** Native ASP.NET Core 10, same team as the host, works with the existing `AddJwtBearer()` config once the `OnMessageReceived` handler is wired.

2. **One hub, scoped by trading account.** A single `TradingEngineRealtimeHub : Hub`, mapped at `/api/hubs/trading`. On `OnConnectedAsync`, read `TradingAccountId` from the principal and call `Groups.AddToGroupAsync(Context.ConnectionId, $"account:{accountId}")`. Every relay call targets that group, not all clients.

3. **Relay via `IIntegrationEventHandler<T>`.** For each relayed event type add a handler that:
   ```csharp
   public sealed class OrderCreatedBrowserRelay(IHubContext<TradingEngineRealtimeHub> hub)
       : IIntegrationEventHandler<OrderCreatedIntegrationEvent>
   {
       public Task Handle(OrderCreatedIntegrationEvent @event) =>
           hub.Clients
              .Group($"account:{@event.TradingAccountId}")
              .SendAsync("orderCreated", @event);
   }
   ```
   Register each in `DependencyInjection.cs` and subscribe to the bus at startup — same pattern as `SignalOrderBridgeWorker`.

4. **Bearer auth over the WebSocket handshake.** Browsers can't send `Authorization` headers on WebSocket upgrades. Extend the existing `JwtBearerOptions` in `Program.cs:125-162`:
   ```csharp
   options.Events.OnMessageReceived = ctx =>
   {
       var accessToken = ctx.Request.Query["access_token"];
       var path = ctx.HttpContext.Request.Path;
       if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/hubs"))
           ctx.Token = accessToken;
       return Task.CompletedTask;
   };
   ```
   Client calls `new HubConnectionBuilder().withUrl('/api/hubs/trading', { accessTokenFactory: () => token })`.

5. **Pipeline insertion.** `MapHub<>` goes in `SharedApplication/DependencyInjection.cs:RunAppPipeline()` between `UseAuthorization()` and `MapControllers()`.

6. **Events to relay** (full list):
   - `OrderCreated`, `OrderFilled`
   - `PositionOpened`, `PositionClosed`
   - `TradeSignalCreated`
   - `StrategyActivated`, `StrategyAutoPromoted`
   - `MLModelActivated`, `OptimizationCompleted`, `OptimizationApproved`, `BacktestCompleted`
   - `EAInstanceRegistered`, `EAInstanceDeregistered`, `EAInstanceDisconnected`
   - `VaRBreach`, `EmergencyFlatten`
   - `MarketDataAnomaly`, `StressTestCompleted`, `SymbolReassigned`, `StrategyGenerationCycleCompleted`

7. **Events explicitly not relayed:**
   - `PriceUpdatedIntegrationEvent` — publishes on every tick batch, 1–10+ events/sec per active symbol. Swamps the browser channel. If live prices are needed, a separate endpoint aggregates to 100 ms windows per symbol.

### Alternatives considered

| Option | Verdict |
|---|---|
| **Server-Sent Events (SSE)** | Simpler (plain HTTP, no upgrade), but one-way only and browsers limit connections per origin (6 per domain). SignalR also supports SSE as a fallback transport, so we get both by default. |
| **Raw `WebSocket` middleware** | Strips away too much (no reconnection, no groups, no fallback). All of that would be re-implemented badly. |
| **Poll the event log table directly** | `IntegrationEventLogEF` is durable and already stores every event — client could long-poll with `since` cursor. Simpler infra, but 1 s polling round-trip vs. sub-100 ms push is a felt UX difference on high-frequency panels like open positions. |
| **Push via Kafka/RabbitMQ directly** (client connects to broker) | Breaks the security boundary; browsers can't safely hold broker credentials. |

### Migration notes

- **No behaviour change for existing clients.** Polling endpoints stay. Clients opt into the hub by connecting.
- **Testing** reuses the existing `PostgresFixture` + `WebApplicationFactory` pattern. Add a `HubConnection` test that:
  1. Logs in, gets JWT.
  2. Connects to hub with `accessTokenFactory`.
  3. Triggers an event via HTTP POST.
  4. Asserts the event arrives on the hub in under 500 ms.
- **Observability:** emit `engine.hub.message.sent` and `engine.hub.group.size` counters to the existing OpenTelemetry pipeline.

### What not to do

- **Don't relay `PriceUpdatedIntegrationEvent`.** Ever.
- **Don't use SignalR groups keyed on user identity alone** — groups are `account:{TradingAccountId}`. Two users sharing a TradingAccount legitimately see the same stream.
- **Don't hold hub connection objects in long-lived services.** Use `IHubContext<THub>` instead — it survives server restarts and scale-out without leaking connection handles.
- **Don't make the hub chatty with request/response RPCs.** It's a push channel. Commands go through REST.

### Effort

~1 week. 2 days for the hub + auth + per-event relay handlers, 2 days for integration tests, 1 day for observability and the scale-out story (see below).

### Scale-out caveat

SignalR in-process is single-node. Multi-node deployments need the Redis backplane (`Microsoft.AspNetCore.SignalR.StackExchangeRedis`). Until Redis is in the infra picture, document "one API node for WebSocket clients" as a constraint. Swapping in the backplane later is a single DI line.

---

## E7 — Batch order cancel

### Context

The admin UI exposes a bulk-action toolbar on the order list (already shipped). Operators select N orders and hit Cancel. Today they'd have to fire N `POST /order/{id}/cancel` requests. We need a single call that handles the batch.

### Recommended approach: best-effort, per-order result report

**Endpoint:** `POST /order/cancel/batch`

**Request:**
```json
{ "orderIds": [12, 34, 56], "reason": "Ops: flattening to reduce exposure" }
```

**Response:**
```json
{
  "data": {
    "total": 3,
    "cancelled": 2,
    "failed": 1,
    "results": [
      { "orderId": 12, "status": "Cancelled" },
      { "orderId": 34, "status": "Cancelled" },
      { "orderId": 56, "status": "Failed", "reason": "Order already Filled" }
    ]
  },
  "status": true,
  "message": "Batch complete: 2 cancelled, 1 failed",
  "responseCode": "00"
}
```

**Atomicity choice: best-effort, not transactional.** Each order's cancel runs independently (same semantics as today's single-cancel handler at `CancelOrderCommand.cs`). Reasons an order fails (already filled, wrong owner, no active EA) apply independently; rolling back the successes because one failed would be actively harmful in a live-trading context.

**Implementation sketch:**
```csharp
public sealed record BatchCancelOrdersCommand : IRequest<ResponseData<BatchCancelResult>>
{
    public required IReadOnlyList<long> OrderIds { get; init; }
    public string? Reason { get; init; }
}

public sealed class BatchCancelOrdersCommandHandler(
    ISender mediator, // reuse CancelOrderCommand per-item
    ILogger<BatchCancelOrdersCommandHandler> log)
    : IRequestHandler<BatchCancelOrdersCommand, ResponseData<BatchCancelResult>>
{
    public async Task<ResponseData<BatchCancelResult>> Handle(
        BatchCancelOrdersCommand request, CancellationToken ct)
    {
        var results = new List<BatchCancelItem>(request.OrderIds.Count);
        foreach (var id in request.OrderIds.Distinct())
        {
            var r = await mediator.Send(new CancelOrderCommand { Id = id }, ct);
            results.Add(new BatchCancelItem
            {
                OrderId = id,
                Status = r.status ? "Cancelled" : "Failed",
                Reason = r.status ? null : r.message,
            });
        }
        // …summarise counts, return envelope
    }
}
```

Handler is thin — it reuses `CancelOrderCommand` per-item so ownership, status, and EA-command-queue logic don't get duplicated or drift.

### What about atomic transactional batch?

Would require a new `IUnitOfWork` scope wrapping N `SaveChangesAsync` calls, *plus* transactional rollback of the `EACommand` queue inserts, *plus* a compensating action if the broker has already acked a partial batch. Real cost, real bugs, and the failure mode (roll back 49 cancels because order #50 was already filled) is operationally worse than best-effort. Punt unless someone asks for it.

### Rate-budget risk

Batch of 50 cancels = 50 `EACommand` rows + 50 `SaveChangesAsync` calls in a tight loop. Observe the broker's rate-limit policy at the EA layer (the engine doesn't enforce a per-broker budget today). **Action:** cap `OrderIds.Count` at 50 via `FluentValidation`. Anything larger is a scripting job, not an operator click.

### Audit trail

Single-cancel doesn't write a `DecisionLog` today. Batch cancel should — one `LogDecisionCommand` at the end with `DecisionType="BatchCancel"`, `ContextJson` containing the per-order results. Enough for post-incident forensics without doubling write volume on every click.

### Migration notes

- **Add `BatchCancelOrdersCommand` + handler** in `/LascodiaTradingEngine.Application/Orders/Commands/BatchCancelOrders/`.
- **FluentValidation rules:** `OrderIds.NotEmpty()`, `OrderIds.Count <= 50`, each `> 0`.
- **Controller action** in `OrderController`: `POST /order/cancel/batch`.
- **Test** with Testcontainers — seed 3 orders, cancel them all, assert 3 `Cancelled` rows + 3 `EACommand` rows, assert audit-log entry.

### Effort

~2 days including tests.

---

## E9 — RBAC / operator roles

### Context

Every authenticated user can engage the global kill switch, approve optimizations, rollback ML models, edit engine config, or bulk-cancel orders. The JWT today carries `tradingAccountId` and `accountType` (Demo / Real / Contest) — no role claim, no policy beyond "is authenticated."

This is fine for a single-operator deployment. The first co-operator invite creates real exposure.

### Recommended approach: claim-based roles seeded by an operator table

**Roles (initial set):**

| Role | Can |
|---|---|
| **Viewer** | Read everything. No mutations. |
| **Trader** | Manage their own account, approve/reject trade signals, pause/activate strategies they own. |
| **Analyst** | Trigger ML training, hyperparam searches, queue backtests/walk-forwards. |
| **Operator** | Engine config, kill switches, EA instance management, batch order cancel, ML rollback, paper-trading toggle, risk profile CRUD. |
| **Admin** | Everything. Manage operators. |

Keep the role set small and static for v1. Dynamic policy editing is a rabbit hole.

**Storage:** Add an `OperatorRole` table:
```sql
CREATE TABLE OperatorRole (
    Id BIGINT PRIMARY KEY,
    TradingAccountId BIGINT NOT NULL REFERENCES TradingAccount(Id),
    Role VARCHAR(50) NOT NULL,  -- 'Viewer' | 'Trader' | 'Analyst' | 'Operator' | 'Admin' | 'EA'
    AssignedAt TIMESTAMP NOT NULL,
    AssignedByAccountId BIGINT NULL REFERENCES TradingAccount(Id),
    IsDeleted BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (TradingAccountId, Role)
);
CREATE INDEX ix_OperatorRole_Account ON OperatorRole (TradingAccountId) WHERE IsDeleted = FALSE;
```

One account can hold multiple roles; the JWT carries the union. Seed the first account with `Admin` via migration.

**EA gets its own role.** `TradingAccountTokenGenerator` checks the `loginSource`:
- `loginSource="ea"` → always `role=EA`, ignore the `OperatorRole` table.
- `loginSource="web"` → roles come from `OperatorRole`, falling back to `Viewer` if none set.

This is critical — the EA must not be accidentally locked out by a badly-set role policy. See the audit's EA-auth-constraints section.

**JWT change** (`TradingAccountTokenGenerator.cs:35-48`):
```csharp
claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
```

**Policy definitions** (new file `SharedApplication/Authorization/Policies.cs`):
```csharp
public static class Policies
{
    public const string Viewer   = nameof(Viewer);
    public const string Trader   = nameof(Trader);
    public const string Analyst  = nameof(Analyst);
    public const string Operator = nameof(Operator);
    public const string Admin    = nameof(Admin);

    public static void Register(AuthorizationOptions o)
    {
        // Policies cascade: Admin satisfies everything below it.
        o.AddPolicy(Viewer,   p => p.RequireRole("Viewer", "Trader", "Analyst", "Operator", "Admin", "EA"));
        o.AddPolicy(Trader,   p => p.RequireRole("Trader", "Operator", "Admin"));
        o.AddPolicy(Analyst,  p => p.RequireRole("Analyst", "Operator", "Admin"));
        o.AddPolicy(Operator, p => p.RequireRole("Operator", "Admin"));
        o.AddPolicy(Admin,    p => p.RequireRole("Admin"));
    }
}
```

**Controller gating** — straight `[Authorize(Policy = Policies.Operator)]` attributes on the destructive actions listed in the auth audit (kill-switch, engine config upsert, batch cancel, rollback, etc.). EA-scoped controllers stay on plain `[Authorize]` or the current `apiScope` so the EA role passes.

### Migration notes

- **Existing tokens stop working** the moment policies roll out on destructive endpoints — they have no role claim. Options:
  1. Grace period: seed every existing `TradingAccount` with `Operator` role in the migration, deploy, then audit and downgrade.
  2. Hard cutover with a short password-reset flow. Not realistic for a production operator tool.
  Go with option 1.
- **EA tokens must be reissued after deploy** so they carry `role=EA`. Alternatively, the `TradingAccountTokenGenerator` change can be backwards-compatible: if the JWT lacks a role claim, treat as `Viewer`. That breaks the EA — so actually the cleanest answer is to detect `loginSource="ea"` at *validation* time as a fallback, not at issuance. Decide at implementation.
- **Audit trail on role assignments:** every `OperatorRole` insert/delete writes a `DecisionLog` entry (`DecisionType="RoleAssignment"`).

### What not to do

- **Don't overload `AccountType` (Demo / Real / Contest) as roles.** That's an account-instrument flag, not a permission model. Mixing them creates ambiguity within three months.
- **Don't encode permissions directly in the JWT.** Roles only. Permissions map from roles server-side. Token size stays small; permission matrix can evolve without reissuing tokens.
- **Don't add a "deny" role.** Least-privilege means absence of role, not presence of "deny."

### Effort

~2 weeks. The DB + migration + token change is a day. The policy decoration across ~20 destructive endpoints is a day. Test coverage across role matrix is the long tail — pairs of (role, endpoint) is a grid of ~100 cases.

---

## E10 — Token revocation / explicit logout

### Context

Tokens today live 8 hours with no server-side revocation. A leaked laptop or MITM capture means 8 hours of uninterrupted access. The frontend "logout" button just drops the token locally.

### Recommended approach: DB-backed blacklist by `jti`, MemoryCache hot layer

**Storage:**
```sql
CREATE TABLE RevokedToken (
    Jti CHAR(36) PRIMARY KEY,       -- JWT ID claim
    TradingAccountId BIGINT NOT NULL REFERENCES TradingAccount(Id),
    ExpiresAt TIMESTAMP NOT NULL,   -- = original token's exp, for GC
    RevokedAt TIMESTAMP NOT NULL,
    Reason VARCHAR(200) NULL        -- 'UserLogout' | 'AdminForceLogout' | 'Compromised' | ...
);
CREATE INDEX ix_RevokedToken_Account_Expires ON RevokedToken (TradingAccountId, ExpiresAt);
```

Use `Jti` (already in the token per `TradingAccountTokenGenerator.cs:41`) as the primary key. Keep `TradingAccountId` alongside for "revoke all my tokens" scenarios.

**Endpoint:** `POST /auth/logout`
- Reads `jti` + `exp` + `tradingAccountId` from the calling principal.
- Inserts a `RevokedToken` row.
- Returns `"00"` / success regardless (idempotent).

**Optional: revoke-all endpoint** `POST /auth/logout/all` — inserts one row per *currently unrevoked* known-jti, or more practically, adds a `TokenIssuedAfter` column to `TradingAccount` and rejects any token with `iat < TokenIssuedAfter`. The latter is cleaner but requires schema + migration. The basic per-jti revoke is enough for v1.

**Validation hook** — extend `JwtBearerOptions.Events.OnTokenValidated` in `Program.cs:125-162`:
```csharp
options.Events.OnTokenValidated = async ctx =>
{
    var jti = ctx.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
    if (jti is null) { ctx.Fail("Missing jti"); return; }

    var cache = ctx.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
    if (cache.TryGetValue($"revoked:{jti}", out _)) { ctx.Fail("Token revoked"); return; }

    var db = ctx.HttpContext.RequestServices.GetRequiredService<IReadApplicationDbContext>();
    var isRevoked = await db.RevokedTokens.AnyAsync(r => r.Jti == jti);
    if (isRevoked)
    {
        cache.Set($"revoked:{jti}", true, TimeSpan.FromMinutes(5));
        ctx.Fail("Token revoked");
    }
};
```

**Cleanup job** — a scoped `BackgroundService` runs daily, deletes `RevokedToken` rows where `ExpiresAt < now`. Table stays small.

### Alternatives considered

| Option | Verdict |
|---|---|
| **Short-lived access token + refresh token** | Correct long-term answer, much bigger change. A blacklist is a bridge that works today. |
| **Redis-based blacklist** | Faster than DB lookup, but we don't have Redis in the infra yet. `IMemoryCache` over DB is good enough for single-node latencies. |
| **Signing-key rotation** | Revokes every token issued before the rotation — fine for incident response, too coarse for per-user logout. |

### Performance note

Every authenticated request now does one DB lookup (or cache hit). Cache TTL of 5 min + `IMemoryCache` backing gives essentially free checks after warmup. Worst case — cold cache — is a single indexed PK lookup per request; tens of microseconds.

### What not to do

- **Don't keep revoked tokens forever.** The daily cleanup is load-bearing.
- **Don't require revocation check to succeed for the request to proceed.** If the DB is down, fail open (log, accept) rather than lock everyone out. This is a trade-off — document it.
- **Don't forget the `jti` in the existing token**. The generator already writes it (`TradingAccountTokenGenerator.cs:41`). Verify before shipping.

### Effort

~3 days. DB + migration + endpoint + cleanup worker + a small handful of integration tests. Writing tests for "token X issued at Y, revoked at Z, request at T" is the tricky part.

---

## Sequencing

If resourcing is one person:

1. **E11 + E2 + E4 + E3** — additive, low-risk, ~2 days total. Ship alongside this doc.
2. **E7 batch cancel** — straightforward, ~2 days.
3. **E10 token revocation** — ~3 days. Unblocks proper logout in the UI, cheap security win.
4. **E9 RBAC** — ~2 weeks. Coordinate with UI so role-aware action gating can land on the same release.
5. **E1 WebSocket** — ~1 week. Hold until RBAC is in place; the hub needs roles in the JWT to scope properly.

All items except E1 can ship on the current SignalR-less, role-less baseline. E1 is the only one with a dependency (RBAC) to sequence.
