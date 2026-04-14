using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Application.Strategies.Queries.DTOs;
using LascodiaTradingEngine.Application.Strategies.Queries.GetPagedStrategies;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.IntegrationTest;

public class StrategyQueriesIntegrationTest : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public StrategyQueriesIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private ReadApplicationDbContext CreateReadContext()
    {
        var options = new DbContextOptionsBuilder<ReadApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new ReadApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task EnsureMigratedAsync()
    {
        await using var context = CreateWriteContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task GetPagedStrategiesQuery_FiltersByScreeningMetadataFields()
    {
        await EnsureMigratedAsync();
        await SeedStrategiesAsync();

        await using var readCtx = CreateReadContext();
        var handler = new GetPagedStrategiesQueryHandler(readCtx, BuildMapper());

        var query = new GetPagedStrategiesQuery
        {
            CurrentPage = 1,
            ItemCountPerPage = 10,
            Filter = new StrategyQueryFilter
            {
                HasScreeningMetadata = true,
                GenerationSource = "Reserve",
                ObservedRegime = "Trending",
                ReserveTargetRegime = "Ranging",
                AutoPromotedOnly = true,
            },
        };

        var response = await handler.Handle(query, CancellationToken.None);

        Assert.True(response.status);
        Assert.Single(response.data!.data);
        var dto = response.data.data[0];
        Assert.Equal("Reserve", dto.ScreeningMetadata!.GenerationSource);
        Assert.Equal(MarketRegimeEnum.Trending.ToString(), dto.ScreeningMetadata.ObservedRegime);
        Assert.Equal(MarketRegimeEnum.Ranging.ToString(), dto.ScreeningMetadata.ReserveTargetRegime);
        Assert.True(dto.ScreeningMetadata.IsAutoPromoted);
    }

    [Fact]
    public async Task GetPagedStrategiesQuery_CanReturnOnlyManualStrategies_WhenScreeningMetadataIsAbsent()
    {
        await EnsureMigratedAsync();
        await SeedStrategiesAsync();

        await using var readCtx = CreateReadContext();
        var handler = new GetPagedStrategiesQueryHandler(readCtx, BuildMapper());

        var query = new GetPagedStrategiesQuery
        {
            CurrentPage = 1,
            ItemCountPerPage = 10,
            Filter = new StrategyQueryFilter
            {
                HasScreeningMetadata = false,
            },
        };

        var response = await handler.Handle(query, CancellationToken.None);

        Assert.True(response.status);
        Assert.Single(response.data!.data);
        var dto = response.data.data[0];
        Assert.Equal("Manual-EURUSD-H1", dto.Name);
        Assert.Null(dto.ScreeningMetadata);
    }

    private async Task SeedStrategiesAsync()
    {
        await using var context = CreateWriteContext();
        await context.Set<Strategy>().IgnoreQueryFilters().ExecuteDeleteAsync();

        context.Set<Strategy>().AddRange(
            new Strategy
            {
                Name = "Manual-EURUSD-H1",
                Description = "Manual strategy",
                StrategyType = StrategyType.Custom,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = "{}",
                Status = StrategyStatus.Active,
                CreatedAt = DateTime.UtcNow.AddDays(-10),
            },
            new Strategy
            {
                Name = "Auto-MovingAverageCrossover-EURUSD-H1",
                Description = "Primary auto",
                StrategyType = StrategyType.MovingAverageCrossover,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = "{\"Template\":\"Primary\"}",
                Status = StrategyStatus.Paused,
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                ScreeningMetricsJson = new ScreeningMetrics
                {
                    Regime = MarketRegimeEnum.Trending.ToString(),
                    ObservedRegime = MarketRegimeEnum.Trending.ToString(),
                    GenerationSource = "Primary",
                    IsWinRate = 0.70,
                    IsProfitFactor = 1.8,
                    IsSharpeRatio = 1.4,
                    OosWinRate = 0.66,
                    OosProfitFactor = 1.6,
                    OosSharpeRatio = 1.1,
                    IsAutoPromoted = false,
                }.ToJson(),
            },
            new Strategy
            {
                Name = "Auto-Reserve-RSIReversion-EURUSD-H1",
                Description = "Reserve auto",
                StrategyType = StrategyType.RSIReversion,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = "{\"Template\":\"Reserve\"}",
                Status = StrategyStatus.Paused,
                CreatedAt = DateTime.UtcNow.AddDays(-4),
                ScreeningMetricsJson = new ScreeningMetrics
                {
                    Regime = MarketRegimeEnum.Ranging.ToString(),
                    ObservedRegime = MarketRegimeEnum.Trending.ToString(),
                    GenerationSource = "Reserve",
                    ReserveTargetRegime = MarketRegimeEnum.Ranging.ToString(),
                    IsWinRate = 0.72,
                    IsProfitFactor = 1.9,
                    IsSharpeRatio = 1.5,
                    OosWinRate = 0.68,
                    OosProfitFactor = 1.7,
                    OosSharpeRatio = 1.2,
                    IsAutoPromoted = true,
                }.ToJson(),
            });

        await context.SaveChangesAsync();
    }

    private static IMapper BuildMapper()
    {
        var expression = new MapperConfigurationExpression();
        expression.AddProfile(new MappingProfile(new HttpContextAccessor(), typeof(StrategyDto).Assembly));
        var configuration = new MapperConfiguration(expression, NullLoggerFactory.Instance);
        configuration.AssertConfigurationIsValid();
        return configuration.CreateMapper();
    }
}
