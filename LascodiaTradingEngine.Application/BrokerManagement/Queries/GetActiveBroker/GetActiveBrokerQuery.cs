using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.BrokerManagement.Queries.GetActiveBroker;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetActiveBrokerQuery : IRequest<ResponseData<string>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetActiveBrokerQueryHandler : IRequestHandler<GetActiveBrokerQuery, ResponseData<string>>
{
    private readonly IBrokerFailover _brokerFailover;

    public GetActiveBrokerQueryHandler(IBrokerFailover brokerFailover)
    {
        _brokerFailover = brokerFailover;
    }

    public Task<ResponseData<string>> Handle(
        GetActiveBrokerQuery request, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            ResponseData<string>.Init(_brokerFailover.ActiveBroker, true, "Successful", "00"));
    }
}
