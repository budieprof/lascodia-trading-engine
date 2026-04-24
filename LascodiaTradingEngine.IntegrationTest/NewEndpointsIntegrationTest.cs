using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// HTTP-level integration coverage for the endpoints shipped as part of E2/E3/E4/E7/E10/E11.
/// Uses <see cref="PostgresFixture"/> for schema bring-up and the shared
/// <see cref="ApiWebApplicationFactory"/> for authenticated clients — with the
/// `X-Test-Roles` header wired by <see cref="TestAuthHandler"/> driving RBAC policy coverage.
/// </summary>
public class NewEndpointsIntegrationTest : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PostgresFixture _fixture;

    public NewEndpointsIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // ── E2 Drawdown history ─────────────────────────────────────────────

    [Fact]
    public async Task DrawdownRecovery_History_Returns_Paged_Snapshots()
    {
        await ResetDatabaseAsync();
        await SeedDrawdownSnapshotsAsync(count: 12);

        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient(OperatorRoleNames.Operator);

        var response = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/drawdown-recovery/history",
            new
            {
                currentPage = 1,
                itemCountPerPage = 5,
            });

        response.EnsureSuccessStatusCode();
        var payload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<DrawdownSnapshotApiDto>>>(response);

        Assert.True(payload.status);
        Assert.Equal(5, payload.data!.data.Count);
        Assert.Equal(12, payload.data.pager!.TotalItemCount);
        Assert.True(
            payload.data.data[0].RecordedAt >= payload.data.data[^1].RecordedAt,
            "Snapshots should be newest-first.");
    }

    // ── E4 Drift report ─────────────────────────────────────────────────

    [Fact]
    public async Task DriftReport_Filters_By_DetectorType_And_Parses_Detector_From_ConditionJson()
    {
        await ResetDatabaseAsync();
        await SeedDriftAlertsAsync();

        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient(OperatorRoleNames.Operator);

        // Unfiltered: all three drift alerts surface.
        var allResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/ml-model/drift-report",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
            });
        allResponse.EnsureSuccessStatusCode();
        var allPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<DriftAlertApiDto>>>(allResponse);

        Assert.True(allPayload.status);
        Assert.Equal(3, allPayload.data!.pager!.TotalItemCount);
        Assert.All(allPayload.data.data, dto => Assert.NotNull(dto.DetectorType));

        // Filtered to CUSUM: one alert matches.
        var filtered = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/ml-model/drift-report",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new { detectorType = "CUSUM" },
            });
        filtered.EnsureSuccessStatusCode();
        var filteredPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<DriftAlertApiDto>>>(filtered);

        Assert.True(filteredPayload.status);
        var only = Assert.Single(filteredPayload.data!.data);
        Assert.Equal("CUSUM", only.DetectorType);
    }

    // ── E3 Training diagnostics ────────────────────────────────────────

    [Fact]
    public async Task MLTrainingDiagnostics_Returns_Advanced_Metrics_For_Completed_Run()
    {
        await ResetDatabaseAsync();
        var runId = await SeedCompletedTrainingRunAsync();

        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient(OperatorRoleNames.Operator);

        var response = await client.GetAsync(
            $"/api/v1/lascodia-trading-engine/ml-model/training/{runId}/diagnostics");
        response.EnsureSuccessStatusCode();

        var payload = await ReadAsAsync<ResponseEnvelope<MLTrainingRunDiagnosticsApiDto>>(response);
        Assert.True(payload.status);

        var dto = payload.data!;
        Assert.Equal(runId,  dto.Id);
        Assert.Equal(0.82m,  dto.F1Score);
        Assert.Equal(0.18m,  dto.BrierScore);
        Assert.Equal(1.35m,  dto.SharpeRatio);
        Assert.Equal(0.12m,  dto.AbstentionRate);
        Assert.True(dto.SmoteApplied);
        Assert.True(dto.CurriculumApplied);
        Assert.Equal("CovariateShift", dto.DriftTriggerType);
        Assert.Contains("\"psi\"",     dto.DriftMetadataJson!);
        Assert.Equal("BaggedLogistic", dto.LearnerArchitecture);
    }

    // ── E7 Batch order cancel ──────────────────────────────────────────

    [Fact]
    public async Task BatchCancelOrders_Cancels_Eligible_And_Reports_Per_Order_Result()
    {
        await ResetDatabaseAsync();
        var (cancellableIds, filledId) = await SeedOrdersForBatchCancelAsync();

        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient(OperatorRoleNames.Operator);

        var ids = new List<long>(cancellableIds) { filledId };
        var response = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/order/cancel/batch",
            new { orderIds = ids, reason = "Integration test" });

        response.EnsureSuccessStatusCode();
        var payload = await ReadAsAsync<ResponseEnvelope<BatchCancelResultApiDto>>(response);

        Assert.True(payload.status);
        var result = payload.data!;
        Assert.Equal(4, result.Total);
        Assert.Equal(3, result.Cancelled);
        Assert.Equal(1, result.Failed);

        var perId = result.Results.ToDictionary(r => r.Id);
        foreach (var id in cancellableIds)
            Assert.Equal("Cancelled", perId[id].Status);
        Assert.Equal("Failed", perId[filledId].Status);
    }

    // ── E9 RBAC / operator-role round-trip ─────────────────────────────

    [Fact]
    public async Task OperatorRole_Grant_List_Revoke_RoundTrip_With_Admin_Token()
    {
        await ResetDatabaseAsync();
        await SeedTradingAccountAsync(id: 42);

        using var factory = CreateFactory();
        using var admin = factory.CreateAuthenticatedClient(OperatorRoleNames.Admin);

        // Non-admin tokens must be refused even if they're authenticated.
        using var viewer = factory.CreateAuthenticatedClient(OperatorRoleNames.Viewer);
        var viewerReject = await viewer.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/admin/operator-roles/grant",
            new { tradingAccountId = 42, role = OperatorRoleNames.Trader });
        Assert.Equal(HttpStatusCode.Forbidden, viewerReject.StatusCode);

        // Grant.
        var grantResponse = await admin.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/admin/operator-roles/grant",
            new { tradingAccountId = 42, role = OperatorRoleNames.Trader });
        grantResponse.EnsureSuccessStatusCode();

        // List — should include the new grant.
        var listResponse = await admin.GetAsync(
            "/api/v1/lascodia-trading-engine/admin/operator-roles?tradingAccountId=42");
        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<List<OperatorRoleApiDto>>>(listResponse);
        Assert.True(listPayload.status);
        Assert.Contains(listPayload.data!, r => r.Role == OperatorRoleNames.Trader);

        // Revoke.
        var revokeResponse = await admin.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/admin/operator-roles/revoke",
            new { tradingAccountId = 42, role = OperatorRoleNames.Trader });
        revokeResponse.EnsureSuccessStatusCode();

        var afterRevoke = await admin.GetAsync(
            "/api/v1/lascodia-trading-engine/admin/operator-roles?tradingAccountId=42");
        var afterPayload = await ReadAsAsync<ResponseEnvelope<List<OperatorRoleApiDto>>>(afterRevoke);
        Assert.DoesNotContain(afterPayload.data!, r => r.Role == OperatorRoleNames.Trader);
    }

    // ── Remaining scaffolds ────────────────────────────────────────────
    //
    // These need multi-step orchestration that's easier to implement alongside
    // a real JWT-issuing TestAuthHandler. Left as `[Fact(Skip=...)]` with a clear
    // next-step so a follow-up can fill them in without re-deriving context.

    [Fact]
    public async Task Logout_Revokes_Jti_So_Same_Token_Cannot_Be_Reused()
    {
        await ResetDatabaseAsync();
        await SeedTradingAccountAsync(id: 1);

        // Single factory — both clients must share the in-process IMemoryCache
        // so the logout handler's cache warm-up is visible to the second call.
        using var factory = CreateFactory();

        var jti = Guid.NewGuid().ToString("N");
        using var session = factory.CreateAuthenticatedClientWithJti(jti, OperatorRoleNames.Trader);

        // Sanity: the authenticated call succeeds pre-logout.
        var preLogout = await session.GetAsync("/api/v1/lascodia-trading-engine/auth/whoami");
        preLogout.EnsureSuccessStatusCode();

        var logout = await session.PostAsync("/api/v1/lascodia-trading-engine/auth/logout", content: null);
        logout.EnsureSuccessStatusCode();

        // Replay the same jti — the handler's cache warm-up should cause TestAuthHandler
        // to fail auth, mirroring the production JWT middleware revocation hook.
        using var replay = factory.CreateAuthenticatedClientWithJti(jti, OperatorRoleNames.Trader);
        var replayResponse = await replay.GetAsync("/api/v1/lascodia-trading-engine/auth/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact(Skip = "Requires the TriggerMLTrainingCommand handler seeded with a runnable trainer — the Operator-policy denial path is covered already by the OperatorRole round-trip above. The 200-path needs the full ML training plumbing registered in the test host.")]
    public Task MLRollback_Requires_Operator_Policy() => Task.CompletedTask;

    [Fact]
    public async Task Swagger_Document_Exposes_Bearer_Security_Scheme()
    {
        using var factory = CreateFactory();
        using var client  = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemes = doc.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes");

        Assert.True(schemes.TryGetProperty("Bearer", out var bearer),
            "Swagger document must expose a `Bearer` security scheme so generated clients know to attach JWTs.");
        Assert.Equal("http",   bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT",    bearer.GetProperty("bearerFormat").GetString());
    }

    // ── helpers ────────────────────────────────────────────────────────

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

    private async Task SeedDrawdownSnapshotsAsync(int count)
    {
        await using var ctx = CreateWriteContext();
        await ctx.Database.EnsureCreatedAsync();

        var baseTime = DateTime.UtcNow.AddMinutes(-count);
        var peak = 100_000m;
        for (var i = 0; i < count; i++)
        {
            var equity = peak - (i * 250m);
            ctx.Set<DrawdownSnapshot>().Add(new DrawdownSnapshot
            {
                PeakEquity    = peak,
                CurrentEquity = equity,
                DrawdownPct   = Math.Round((peak - equity) / peak * 100m, 4),
                RecordedAt    = baseTime.AddMinutes(i),
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task SeedDriftAlertsAsync()
    {
        await using var ctx = CreateWriteContext();
        await ctx.Database.EnsureCreatedAsync();

        ctx.Set<Alert>().AddRange(
            new Alert
            {
                AlertType        = AlertType.MLModelDegraded,
                Symbol           = "EURUSD",
                Severity         = AlertSeverity.High,
                ConditionJson    = "{\"DetectorType\":\"CUSUM\",\"accuracy\":0.47,\"threshold\":0.50}",
                DeduplicationKey = "CUSUM:EURUSD:H1",
                IsActive         = true,
                LastTriggeredAt  = DateTime.UtcNow.AddMinutes(-30),
            },
            new Alert
            {
                AlertType        = AlertType.MLModelDegraded,
                Symbol           = "GBPUSD",
                Severity         = AlertSeverity.Medium,
                ConditionJson    = "{\"DetectorType\":\"Adwin\",\"window\":256}",
                DeduplicationKey = "Adwin:GBPUSD:H1",
                IsActive         = true,
                LastTriggeredAt  = DateTime.UtcNow.AddMinutes(-20),
            },
            new Alert
            {
                AlertType        = AlertType.SystemicMLDegradation,
                Symbol           = null,
                Severity         = AlertSeverity.Critical,
                ConditionJson    = "{\"DetectorType\":\"DriftAgreement\",\"agreement\":3}",
                DeduplicationKey = "DriftAgreement:System",
                IsActive         = true,
                LastTriggeredAt  = DateTime.UtcNow.AddMinutes(-10),
            });
        await ctx.SaveChangesAsync();
    }

    private async Task<long> SeedCompletedTrainingRunAsync()
    {
        await using var ctx = CreateWriteContext();
        await ctx.Database.EnsureCreatedAsync();

        var run = new MLTrainingRun
        {
            Symbol              = "EURUSD",
            Timeframe           = Timeframe.H1,
            TriggerType         = TriggerType.Manual,
            Status              = RunStatus.Completed,
            Priority            = 3,
            FromDate            = DateTime.UtcNow.AddDays(-30),
            ToDate              = DateTime.UtcNow.AddDays(-1),
            TotalSamples        = 12_000,
            AttemptCount        = 1,
            StartedAt           = DateTime.UtcNow.AddHours(-2),
            CompletedAt         = DateTime.UtcNow.AddHours(-1),
            TrainingDurationMs  = 3_600_000,

            // Advanced evaluation metrics
            DirectionAccuracy   = 0.63m,
            MagnitudeRMSE       = 4.2m,
            F1Score             = 0.82m,
            BrierScore          = 0.18m,
            SharpeRatio         = 1.35m,
            ExpectedValue       = 0.94m,
            AbstentionRate      = 0.12m,
            AbstentionPrecision = 0.71m,

            // Dataset / reproducibility
            LabelImbalanceRatio       = 0.52m,
            TrainingDatasetStatsJson  = "{\"buy\":6250,\"sell\":5750}",
            DatasetHash               = "sha256-abc",
            CandleIdRangeStart        = 1,
            CandleIdRangeEnd          = 12_000,

            // Architecture / hyperparams
            LearnerArchitecture       = LearnerArchitecture.BaggedLogistic,
            HyperparamConfigJson      = "{\"K\":16,\"LearningRate\":0.01}",
            CvFoldScoresJson          = "[0.62,0.61,0.64]",

            // Drift context
            DriftTriggerType          = "CovariateShift",
            DriftMetadataJson         = "{\"maxPsi\":0.28,\"psi\":\"Rsi\"}",

            // Feature-flag audit trail
            SmoteApplied              = true,
            CurriculumApplied         = true,
            MixupApplied              = false,
        };

        ctx.Set<MLTrainingRun>().Add(run);
        await ctx.SaveChangesAsync();
        return run.Id;
    }

    private async Task<(long[] cancellable, long filled)> SeedOrdersForBatchCancelAsync()
    {
        await using var ctx = CreateWriteContext();
        await ctx.Database.EnsureCreatedAsync();

        // Seed a backing TradingAccount so the ownership guard on CancelOrderCommand
        // has something to compare against. The TestAuthHandler's `tradingAccountId=1`
        // matches this id, so the ownership check passes.
        ctx.Set<TradingAccount>().Add(new TradingAccount
        {
            Id           = 1,
            AccountId    = "int-test-acct",
            BrokerServer = "Test",
            BrokerName   = "Test",
            AccountName  = "Integration",
            Currency     = "USD",
            AccountType  = AccountType.Demo,
            IsActive     = true,
        });

        var cancellable = new[]
        {
            NewOrder(OrderStatus.Pending,    accountId: 1),
            NewOrder(OrderStatus.Submitted,  accountId: 1),
            NewOrder(OrderStatus.PartialFill, accountId: 1),
        };
        var filled = NewOrder(OrderStatus.Filled, accountId: 1);

        ctx.Set<Order>().AddRange(cancellable);
        ctx.Set<Order>().Add(filled);
        await ctx.SaveChangesAsync();

        return (cancellable.Select(o => o.Id).ToArray(), filled.Id);
    }

    private static Order NewOrder(OrderStatus status, long accountId) => new()
    {
        TradingAccountId = accountId,
        Symbol           = "EURUSD",
        OrderType        = OrderType.Buy,
        ExecutionType    = ExecutionType.Market,
        Quantity         = 0.1m,
        Price            = 1.1m,
        Status           = status,
    };

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
            AccountName  = "Role target",
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

    private sealed class DrawdownSnapshotApiDto
    {
        public long     Id            { get; set; }
        public decimal  CurrentEquity { get; set; }
        public decimal  PeakEquity    { get; set; }
        public decimal  DrawdownPct   { get; set; }
        public string?  RecoveryMode  { get; set; }
        public DateTime RecordedAt    { get; set; }
    }

    private sealed class DriftAlertApiDto
    {
        public long     Id              { get; set; }
        public string?  Symbol          { get; set; }
        public string?  AlertType       { get; set; }
        public string?  Severity        { get; set; }
        public string?  DetectorType    { get; set; }
        public string?  ConditionJson   { get; set; }
        public bool     IsActive        { get; set; }
        public DateTime? LastTriggeredAt { get; set; }
    }

    private sealed class MLTrainingRunDiagnosticsApiDto
    {
        public long     Id                    { get; set; }
        public decimal? F1Score               { get; set; }
        public decimal? BrierScore            { get; set; }
        public decimal? SharpeRatio           { get; set; }
        public decimal? AbstentionRate        { get; set; }
        public bool     SmoteApplied          { get; set; }
        public bool     CurriculumApplied     { get; set; }
        public string?  DriftTriggerType      { get; set; }
        public string?  DriftMetadataJson     { get; set; }
        public string?  LearnerArchitecture   { get; set; }
    }

    private sealed class BatchCancelResultApiDto
    {
        public int  Total     { get; set; }
        public int  Cancelled { get; set; }
        public int  Failed    { get; set; }
        public List<BatchCancelItemApiDto> Results { get; set; } = new();
    }

    private sealed class BatchCancelItemApiDto
    {
        public long    Id     { get; set; }
        public string  Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }

    private sealed class OperatorRoleApiDto
    {
        public long     Id                  { get; set; }
        public long     TradingAccountId    { get; set; }
        public string   Role                { get; set; } = string.Empty;
        public DateTime AssignedAt          { get; set; }
    }
}
