using System.Net.WebSockets;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public sealed class EACommandPushWorkerTest
{
    [Fact]
    public async Task RunCycleAsync_DoesNotCacheDisconnectedCommands_AndPushesAfterInstanceConnects()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<EACommand>().Add(NewCommand(1, "EA-1", now));
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, firstCycle.PushedCount);
        Assert.Empty(bridge.PushedCommands);

        bridge.SetConnected("EA-1");

        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, secondCycle.PushedCount);
        Assert.Single(bridge.PushedCommands);
        Assert.Equal(1L, bridge.PushedCommands[0].CommandId);
    }

    [Fact]
    public async Task RunCycleAsync_RequeuedCommand_IsRepushedWhenRetryCountIncreases()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        bridge.SetConnected("EA-1");
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        var command = NewCommand(1, "EA-1", now);
        ctx.Set<EACommand>().Add(command);
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, firstCycle.PushedCount);

        command.RetryCount = 1;
        await ctx.SaveChangesAsync();

        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, secondCycle.PushedCount);
        Assert.Equal([0, 1], bridge.PushedCommands.Select(x => x.RetryCount).ToArray());
    }

    [Fact]
    public async Task RunCycleAsync_PushesConnectedCommands_EvenWhenOlderDisconnectedBacklogExists()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        bridge.SetConnected("EA-LIVE");
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        for (int i = 0; i < 55; i++)
        {
            ctx.Set<EACommand>().Add(NewCommand(i + 1, "EA-OFFLINE", now.AddMinutes(-120 + i)));
        }

        for (int i = 0; i < 5; i++)
        {
            ctx.Set<EACommand>().Add(NewCommand(100 + i, "EA-LIVE", now.AddMinutes(-5 + i)));
        }

        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        var cycle = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(5, cycle.PushedCount);
        Assert.Equal([100L, 101L, 102L, 103L, 104L],
            bridge.PushedCommands.Select(x => x.CommandId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task RunCycleAsync_PagesPastAlreadyPushedCommands_ToReachRemainingPendingCommands()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        bridge.SetConnected("EA-1");
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        for (int i = 0; i < EACommandPushWorker.MaxPushesPerCycle + 5; i++)
        {
            ctx.Set<EACommand>().Add(NewCommand(i + 1, "EA-1", now.AddSeconds(i)));
        }

        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(EACommandPushWorker.MaxPushesPerCycle, firstCycle.PushedCount);
        Assert.Equal(5, secondCycle.PushedCount);
        Assert.Equal(EACommandPushWorker.MaxPushesPerCycle + 5, bridge.PushedCommands.Select(x => x.CommandId).Distinct().Count());
    }

    [Fact]
    public async Task RunCycleAsync_DeepPendingBacklog_AdvancesAcrossCyclesWithoutRestartingFromHead()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        bridge.SetConnected("EA-1");
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        for (int i = 0; i < 600; i++)
        {
            ctx.Set<EACommand>().Add(NewCommand(i + 1, "EA-1", now.AddSeconds(i)));
        }

        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        for (int i = 0; i < 10; i++)
        {
            var cycle = await worker.RunCycleAsync(CancellationToken.None);
            Assert.Equal(EACommandPushWorker.MaxPushesPerCycle, cycle.PushedCount);
        }

        var eleventhCycle = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(EACommandPushWorker.MaxPushesPerCycle, eleventhCycle.PushedCount);
        Assert.Equal([501L, 502L, 503L, 504L, 505L],
            bridge.PushedCommands
                .Skip(500)
                .Take(5)
                .Select(x => x.CommandId)
                .ToArray());
    }

    [Fact]
    public async Task RunCycleAsync_RequeuedOlderCommand_IsRepushedImmediatelyEvenAfterCursorMovedPastIt()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        bridge.SetConnected("EA-1");
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        for (int i = 0; i < 120; i++)
        {
            ctx.Set<EACommand>().Add(NewCommand(i + 1, "EA-1", now.AddSeconds(i)));
        }

        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);

        var requeued = await ctx.Set<EACommand>().SingleAsync(x => x.Id == 5);
        requeued.RetryCount = 1;
        await ctx.SaveChangesAsync();

        var cycle = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Contains(
            bridge.PushedCommands,
            x => x.CommandId == 5 && x.RetryCount == 1);
        Assert.True(cycle.RequeuedRepushCount >= 1);
    }

    [Fact]
    public async Task RunCycleAsync_ExpiresCommandsOlderThanThreshold()
    {
        var now = new DateTime(2026, 04, 24, 12, 0, 0, DateTimeKind.Utc);
        var bridge = new FakeWebSocketBridge();
        bridge.SetConnected("EA-1");
        var (ctx, conn) = NewContext();
        using var _ctx = ctx;
        using var _conn = conn;

        ctx.Set<EACommand>().Add(NewCommand(1, "EA-1", now.AddHours(-25)));
        await ctx.SaveChangesAsync();

        using var provider = BuildProvider(ctx);
        var worker = CreateWorker(provider, bridge, now);

        var cycle = await worker.RunCycleAsync(CancellationToken.None);
        var expired = await ctx.Set<EACommand>().SingleAsync();

        Assert.Equal(1, cycle.ExpiredCount);
        Assert.True(expired.Acknowledged);
        Assert.Contains("Expired", expired.AckResult, StringComparison.Ordinal);
        Assert.Empty(bridge.PushedCommands);
    }

    private static EACommandPushWorker CreateWorker(
        ServiceProvider provider,
        IWebSocketBridge bridge,
        DateTime nowUtc)
        => new(
            NullLogger<EACommandPushWorker>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            bridge,
            new WebSocketBridgeOptions { Enabled = true },
            new EACommandPushFixedTimeProvider(nowUtc));

    private static ServiceProvider BuildProvider(EACommandPushTestContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(ctx);
        services.AddSingleton<IReadApplicationDbContext>(ctx);
        return services.BuildServiceProvider();
    }

    private static (EACommandPushTestContext Ctx, SqliteConnection Conn) NewContext()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<EACommandPushTestContext>()
            .UseSqlite(conn)
            .Options;

        var ctx = new EACommandPushTestContext(options);
        ctx.Database.EnsureCreated();
        return (ctx, conn);
    }

    private static EACommand NewCommand(long id, string instanceId, DateTime createdAtUtc)
        => new()
        {
            Id = id,
            TargetInstanceId = instanceId,
            CommandType = EACommandType.UpdateTrailing,
            Symbol = "EURUSD",
            CreatedAt = createdAtUtc,
            Parameters = "{\"stopLoss\":1.05}"
        };
}

internal sealed class EACommandPushTestContext : DbContext,
    IReadApplicationDbContext, IWriteApplicationDbContext
{
    public EACommandPushTestContext(DbContextOptions<EACommandPushTestContext> options)
        : base(options)
    {
    }

    public DbContext GetDbContext() => this;

    public new int SaveChanges() => base.SaveChanges();

    public new Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => base.SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EACommand>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasQueryFilter(x => !x.IsDeleted);
        });
    }
}

internal sealed class EACommandPushFixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public EACommandPushFixedTimeProvider(DateTime nowUtc)
    {
        _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class FakeWebSocketBridge : IWebSocketBridge
{
    private readonly HashSet<string> _connected = new(StringComparer.Ordinal);

    public List<PushedCommandSnapshot> PushedCommands { get; } = [];

    public IReadOnlyCollection<string> GetConnectedInstanceIds() => _connected.ToArray();

    public bool IsConnected(string instanceId) => _connected.Contains(instanceId);

    public Task<bool> PushCommandAsync(string instanceId, EACommand command, CancellationToken ct)
    {
        if (!_connected.Contains(instanceId))
            return Task.FromResult(false);

        PushedCommands.Add(new PushedCommandSnapshot(command.Id, instanceId, command.RetryCount));
        return Task.FromResult(true);
    }

    public void RegisterConnection(string instanceId, WebSocket socket) => _connected.Add(instanceId);

    public void UnregisterConnection(string instanceId) => _connected.Remove(instanceId);

    public void SetConnected(params string[] instanceIds)
    {
        _connected.Clear();
        foreach (var instanceId in instanceIds)
            _connected.Add(instanceId);
    }
}

internal readonly record struct PushedCommandSnapshot(long CommandId, string InstanceId, int RetryCount);
