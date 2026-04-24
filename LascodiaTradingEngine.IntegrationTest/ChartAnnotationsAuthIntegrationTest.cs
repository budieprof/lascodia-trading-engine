using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// Integration coverage for the chart-annotation CRUD endpoints, the
/// cookie-session probes (<c>whoami</c>, <c>ws-ticket</c>) that back the
/// admin UI's realtime flow, and the SignalR presence hub. Presence runs
/// over long-polling because <c>TestServer</c> doesn't ship a WebSocket
/// transport.
/// </summary>
public class ChartAnnotationsAuthIntegrationTest : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PostgresFixture _fixture;

    public ChartAnnotationsAuthIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Chart annotations ───────────────────────────────────────────────

    [Fact]
    public async Task ChartAnnotation_Create_List_Returns_Created_Row()
    {
        await ResetDatabaseAsync();
        await SeedTradingAccountAsync(id: 1);

        using var factory = CreateFactory();
        using var client  = factory.CreateAuthenticatedClient(OperatorRoleNames.Trader);

        var annotatedAt = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc);
        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/chart-annotations",
            new
            {
                target      = "drawdown",
                symbol      = "EURUSD",
                annotatedAt,
                body        = "Manual pause during news spike.",
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);
        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/chart-annotations/list",
            new
            {
                currentPage      = 1,
                itemCountPerPage = 10,
                filter           = new { target = "drawdown" },
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<ChartAnnotationApiDto>>>(listResponse);
        Assert.True(listPayload.status);

        var row = Assert.Single(listPayload.data!.data);
        Assert.Equal("drawdown",                        row.Target);
        Assert.Equal("EURUSD",                          row.Symbol);
        Assert.Equal("Manual pause during news spike.", row.Body);
        Assert.Equal(1L,                                row.CreatedBy);
    }

    [Fact]
    public async Task ChartAnnotation_Create_Denied_For_Viewer_Token()
    {
        await ResetDatabaseAsync();
        await SeedTradingAccountAsync(id: 1);

        using var factory = CreateFactory();
        using var viewer  = factory.CreateAuthenticatedClient(OperatorRoleNames.Viewer);

        var response = await viewer.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/chart-annotations",
            new
            {
                target      = "drawdown",
                annotatedAt = DateTime.UtcNow,
                body        = "Should be forbidden.",
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ChartAnnotation_Update_Rejects_Non_Author()
    {
        await ResetDatabaseAsync();
        await SeedTradingAccountAsync(id: 1);

        // Direct DB seed with CreatedBy=2 — the TestAuthHandler caller is
        // accountId=1, so the author-only rule must reject this.
        long annotationId;
        await using (var ctx = CreateWriteContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            var entity = new ChartAnnotation
            {
                Target      = "drawdown",
                Symbol      = null,
                AnnotatedAt = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc),
                Body        = "Seeded by another operator.",
                CreatedBy   = 2,
                CreatedAt   = DateTime.UtcNow,
            };
            ctx.Set<ChartAnnotation>().Add(entity);
            await ctx.SaveChangesAsync();
            annotationId = entity.Id;
        }

        using var factory = CreateFactory();
        using var client  = factory.CreateAuthenticatedClient(OperatorRoleNames.Trader);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/lascodia-trading-engine/chart-annotations/{annotationId}",
            new { body = "Attempted edit by non-author." });

        response.EnsureSuccessStatusCode();
        var payload = await ReadAsAsync<ResponseEnvelope<string>>(response);
        Assert.False(payload.status);
        Assert.Contains("author", payload.message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ── whoami ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhoAmI_Returns_AccountId_And_Role_Claims()
    {
        using var factory = CreateFactory();
        using var client  = factory.CreateAuthenticatedClient(OperatorRoleNames.Operator, OperatorRoleNames.Admin);

        var response = await client.GetAsync("/api/v1/lascodia-trading-engine/auth/whoami");
        response.EnsureSuccessStatusCode();

        var payload = await ReadAsAsync<ResponseEnvelope<WhoAmIApiDto>>(response);
        Assert.True(payload.status);
        Assert.Equal(1L, payload.data!.TradingAccountId);
        Assert.Contains(OperatorRoleNames.Operator, payload.data.Roles);
        Assert.Contains(OperatorRoleNames.Admin,    payload.data.Roles);
    }

    [Fact]
    public async Task WhoAmI_Rejects_Anonymous_Caller()
    {
        using var factory = CreateFactory();
        using var anon    = factory.CreateClient();

        var response = await anon.GetAsync("/api/v1/lascodia-trading-engine/auth/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── ws-ticket ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WsTicket_Echoes_Bearer_For_Authenticated_Caller()
    {
        using var factory = CreateFactory();
        using var client  = factory.CreateAuthenticatedClient(OperatorRoleNames.Trader);

        var response = await client.GetAsync("/api/v1/lascodia-trading-engine/auth/ws-ticket");
        response.EnsureSuccessStatusCode();

        var payload = await ReadAsAsync<ResponseEnvelope<WsTicketApiDto>>(response);
        Assert.True(payload.status);
        Assert.False(string.IsNullOrWhiteSpace(payload.data!.Token));
    }

    [Fact]
    public async Task WsTicket_Rejects_Anonymous_Caller()
    {
        using var factory = CreateFactory();
        using var anon    = factory.CreateClient();

        var response = await anon.GetAsync("/api/v1/lascodia-trading-engine/auth/ws-ticket");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Presence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Presence_EnterRoom_Broadcasts_PresenceJoined_To_Other_Clients()
    {
        using var factory = CreateFactory();

        // TestServer doesn't ship WebSockets, so SignalR negotiates down to long
        // polling via `HttpMessageHandlerFactory` — the same technique the framework
        // documents for in-memory hub integration tests.
        var connectionA = BuildHubConnection(factory);
        var connectionB = BuildHubConnection(factory);

        var joinedByB = new TaskCompletionSource<JoinEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionB.On<JoinEvent>("presenceJoined", evt => joinedByB.TrySetResult(evt));

        await connectionA.StartAsync();
        await connectionB.StartAsync();

        // B enters first so it's a room member when A broadcasts; SignalR's group
        // membership is "must be a member to receive", and the broadcaster itself
        // is included, so both orderings work — but entering B first also proves
        // the bookkeeping fires for subsequent joiners.
        await connectionB.InvokeAsync("EnterRoom", "kill-switches");
        await connectionA.InvokeAsync("EnterRoom", "kill-switches");

        var winner = await Task.WhenAny(joinedByB.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(joinedByB.Task, winner);
        var payload = await joinedByB.Task;
        Assert.Equal(1L,               payload.AccountId);
        Assert.Equal("kill-switches",  payload.RouteKey);

        await connectionA.DisposeAsync();
        await connectionB.DisposeAsync();
    }

    private static HubConnection BuildHubConnection(ApiWebApplicationFactory factory)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/api/hubs/trading"), options =>
            {
                options.HttpMessageHandlerFactory       = _ => factory.Server.CreateHandler();
                options.Transports                      = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.Headers["Authorization"]        = TestAuthHandler.AuthorizationHeaderValue;
                options.Headers[TestAuthHandler.RolesHeader] = OperatorRoleNames.Operator;
            })
            .Build();
    }

    private sealed class JoinEvent
    {
        public long   AccountId { get; set; }
        public string RouteKey  { get; set; } = string.Empty;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private ApiWebApplicationFactory CreateFactory() => new(_fixture.ConnectionString);

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task ResetDatabaseAsync()
    {
        await using var ctx = CreateWriteContext();
        await ctx.Database.EnsureDeletedAsync();
    }

    private async Task SeedTradingAccountAsync(long id)
    {
        await using var ctx = CreateWriteContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Set<TradingAccount>().Add(new TradingAccount
        {
            Id           = id,
            AccountId    = $"acct-{id}",
            BrokerServer = "Test",
            BrokerName   = "Test",
            AccountName  = "Integration",
            Currency     = "USD",
            AccountType  = AccountType.Demo,
            IsActive     = true,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<T> ReadAsAsync<T>(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        return payload ?? throw new InvalidOperationException("Expected response payload.");
    }

    // ── envelope / DTO projections ─────────────────────────────────────

    private sealed class ResponseEnvelope<T>
    {
        public T?      data         { get; set; }
        public bool    status       { get; set; }
        public string? message      { get; set; }
        public string? responseCode { get; set; }
    }

    private sealed class PagedEnvelope<T>
    {
        public List<T>        data  { get; set; } = [];
        public PagerEnvelope? pager { get; set; }
    }

    private sealed class PagerEnvelope
    {
        public int TotalItemCount    { get; set; }
        public int CurrentPage       { get; set; }
        public int ItemCountPerPage  { get; set; }
    }

    private sealed class ChartAnnotationApiDto
    {
        public long      Id          { get; set; }
        public string    Target      { get; set; } = string.Empty;
        public string?   Symbol      { get; set; }
        public DateTime  AnnotatedAt { get; set; }
        public string    Body        { get; set; } = string.Empty;
        public long      CreatedBy   { get; set; }
        public DateTime  CreatedAt   { get; set; }
        public DateTime? UpdatedAt   { get; set; }
    }

    private sealed class WhoAmIApiDto
    {
        public long     TradingAccountId { get; set; }
        public string[] Roles            { get; set; } = [];
    }

    private sealed class WsTicketApiDto
    {
        public string Token { get; set; } = string.Empty;
    }
}
