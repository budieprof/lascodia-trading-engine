using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

/// <summary>
/// HTTP-level integration coverage for the endpoints shipped as part of E2/E3/E4/E7/E10/E11.
/// Uses <see cref="PostgresFixture"/> for schema bring-up and the shared
/// <see cref="ApiWebApplicationFactory"/> for authenticated clients.
///
/// <para>
/// The PR ships one concrete working test (drawdown-history round-trip) plus
/// scaffolds for the remaining endpoints. Scaffolds are <c>[Fact(Skip=...)]</c>
/// so the suite stays green while making the intent discoverable — a follow-up
/// can fill in the Arrange/Act/Assert without re-deriving the fixture pattern.
/// </para>
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
        using var client = factory.CreateAuthenticatedClient();

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
        // Query handler orders by RecordedAt desc — most recent first.
        Assert.True(
            payload.data.data[0].RecordedAt >= payload.data.data[^1].RecordedAt,
            "Snapshots should be newest-first.");
    }

    // ── E4 Drift report ─────────────────────────────────────────────────

    [Fact(Skip = "Scaffold — fill in Arrange by seeding Alert rows with AlertType.MLModelDegraded + DetectorType JSON, then assert parsed detectorType on the DTO.")]
    public Task DriftReport_Filters_By_DetectorType_And_Parses_Detector_From_ConditionJson() => Task.CompletedTask;

    // ── E3 Training diagnostics ────────────────────────────────────────

    [Fact(Skip = "Scaffold — seed an MLTrainingRun with advanced metrics + JSON blobs, GET /ml-model/training/{id}/diagnostics, assert the DTO carries them.")]
    public Task MLTrainingDiagnostics_Returns_Advanced_Metrics_For_Completed_Run() => Task.CompletedTask;

    // ── E7 Batch order cancel ──────────────────────────────────────────

    [Fact(Skip = "Scaffold — seed 3 cancellable + 1 Filled order under one TradingAccount, POST /order/cancel/batch with all four ids, assert 3 cancelled / 1 failed and 3 EACommand rows queued.")]
    public Task BatchCancelOrders_Cancels_Eligible_And_Reports_Per_Order_Result() => Task.CompletedTask;

    // ── E10 Token revocation ───────────────────────────────────────────

    [Fact(Skip = "Scaffold — login to get a JWT, POST /auth/logout, then reuse the same JWT on any authenticated endpoint and assert 401.")]
    public Task Logout_Revokes_Jti_So_Same_Token_Cannot_Be_Reused() => Task.CompletedTask;

    // ── E9 RBAC policy enforcement ─────────────────────────────────────

    [Fact(Skip = "Scaffold — issue a Viewer-role token, hit POST /ml-model/rollback (Operator-gated), assert 403. Then upgrade the account to Operator and repeat, asserting 200.")]
    public Task MLRollback_Requires_Operator_Policy() => Task.CompletedTask;

    [Fact(Skip = "Scaffold — Admin-only. Grant → List → Revoke round-trip via /admin/operator-roles; assert only Admin tokens succeed and the table reflects every mutation.")]
    public Task OperatorRole_Grant_List_Revoke_RoundTrip_With_Admin_Token() => Task.CompletedTask;

    // ── E11 Swagger ────────────────────────────────────────────────────

    [Fact(Skip = "Scaffold — GET /swagger/v1/swagger.json and assert the Bearer security scheme is present + applied to at least one authenticated operation.")]
    public Task Swagger_Document_Exposes_Bearer_Security_Scheme() => Task.CompletedTask;

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

    private static async Task<T> ReadAsAsync<T>(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        return payload ?? throw new InvalidOperationException("Expected response payload.");
    }

    // ── envelope / DTO projections (duplicated per-file so one suite isn't a silent dep on another) ──

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
}
