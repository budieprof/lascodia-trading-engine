using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.RateLimiting.Queries.GetApiQuotaStatus;

// ── DTO (inline) ──────────────────────────────────────────────────────────────

public class ApiQuotaStatusDto
{
    public string BrokerKey       { get; set; } = string.Empty;
    public int    MaxRequests      { get; set; }
    public int    RemainingRequests { get; set; }
    public bool   IsThrottled     { get; set; }
}

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetApiQuotaStatusQuery : IRequest<ResponseData<ApiQuotaStatusDto>>
{
    public string BrokerKey             { get; set; } = string.Empty;
    public int    MaxRequestsPerMinute  { get; set; } = 60;
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetApiQuotaStatusQueryHandler
    : IRequestHandler<GetApiQuotaStatusQuery, ResponseData<ApiQuotaStatusDto>>
{
    private readonly IRateLimiter _rateLimiter;

    public GetApiQuotaStatusQueryHandler(IRateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    public async Task<ResponseData<ApiQuotaStatusDto>> Handle(
        GetApiQuotaStatusQuery request, CancellationToken cancellationToken)
    {
        var window    = TimeSpan.FromMinutes(1);
        var remaining = await _rateLimiter.GetRemainingAsync(
            request.BrokerKey, request.MaxRequestsPerMinute, window, cancellationToken);

        var dto = new ApiQuotaStatusDto
        {
            BrokerKey        = request.BrokerKey,
            MaxRequests       = request.MaxRequestsPerMinute,
            RemainingRequests = remaining,
            IsThrottled      = remaining == 0
        };

        return ResponseData<ApiQuotaStatusDto>.Init(dto, true, "Successful", "00");
    }
}
