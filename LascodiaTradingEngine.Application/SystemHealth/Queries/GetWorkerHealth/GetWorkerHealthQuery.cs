using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetWorkerHealth;

/// <summary>Returns current health snapshots for all background workers.</summary>
public class GetWorkerHealthQuery : IRequest<ResponseData<IReadOnlyList<WorkerHealthSnapshot>>>
{
}

/// <summary>Retrieves health snapshots from the worker health monitor for all registered background workers.</summary>
public class GetWorkerHealthQueryHandler
    : IRequestHandler<GetWorkerHealthQuery, ResponseData<IReadOnlyList<WorkerHealthSnapshot>>>
{
    private readonly IWorkerHealthMonitor _healthMonitor;

    public GetWorkerHealthQueryHandler(IWorkerHealthMonitor healthMonitor)
    {
        _healthMonitor = healthMonitor;
    }

    public Task<ResponseData<IReadOnlyList<WorkerHealthSnapshot>>> Handle(
        GetWorkerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var snapshots = _healthMonitor.GetCurrentSnapshots();
        return Task.FromResult(
            ResponseData<IReadOnlyList<WorkerHealthSnapshot>>.Init(snapshots, true, "Successful", "00"));
    }
}
