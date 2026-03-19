using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Brokers.Commands.DeleteBroker;

// ── Command ───────────────────────────────────────────────────────────────────

public class DeleteBrokerCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class DeleteBrokerCommandHandler : IRequestHandler<DeleteBrokerCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteBrokerCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeleteBrokerCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Broker not found", "-14");

        if (entity.IsActive)
            return ResponseData<string>.Init(null, false, "Cannot delete the active broker", "-11");

        entity.IsDeleted = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
