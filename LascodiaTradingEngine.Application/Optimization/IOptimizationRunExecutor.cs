using System.Diagnostics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

internal interface IOptimizationRunExecutor
{
    Task ExecuteAsync(
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        IIntegrationEventService eventService,
        Stopwatch sw,
        CancellationToken ct,
        CancellationToken runCt);
}
