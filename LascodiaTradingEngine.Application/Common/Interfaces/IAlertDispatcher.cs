using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IAlertDispatcher
{
    Task DispatchAsync(Alert alert, string message, CancellationToken ct);
}
