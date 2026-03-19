using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Brokers.Commands.ActivateBroker;

// ── Command ───────────────────────────────────────────────────────────────────

public class ActivateBrokerCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ActivateBrokerCommandHandler : IRequestHandler<ActivateBrokerCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ActivateBrokerCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ActivateBrokerCommand request, CancellationToken cancellationToken)
    {
        var target = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (target == null)
            return ResponseData<string>.Init(null, false, "Broker not found", "-14");

        // Deactivate all brokers
        var allBrokers = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var broker in allBrokers)
            broker.IsActive = false;

        // Activate target
        target.IsActive = true;
        target.Status   = BrokerStatus.Connected;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Activated", true, "Successful", "00");
    }
}
