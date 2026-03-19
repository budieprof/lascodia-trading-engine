using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Brokers.Commands.UpdateBroker;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateBrokerCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long    Id          { get; set; }
    public string?  Name        { get; set; }
    public string?  BrokerType  { get; set; }
    public string?  Environment { get; set; }
    public string?  BaseUrl     { get; set; }
    public string?  ApiKey      { get; set; }
    public string?  ApiSecret   { get; set; }
    public bool?    IsPaper     { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateBrokerCommandHandler : IRequestHandler<UpdateBrokerCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateBrokerCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateBrokerCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Broker not found", "-14");

        if (request.Name        != null) entity.Name        = request.Name;
        if (request.BrokerType  != null) entity.BrokerType  = Enum.Parse<BrokerType>(request.BrokerType, ignoreCase: true);
        if (request.Environment != null) entity.Environment = Enum.Parse<BrokerEnvironment>(request.Environment, ignoreCase: true);
        if (request.BaseUrl     != null) entity.BaseUrl     = request.BaseUrl;
        if (request.ApiKey      != null) entity.ApiKey      = request.ApiKey;
        if (request.ApiSecret   != null) entity.ApiSecret   = request.ApiSecret;
        if (request.IsPaper     != null) entity.IsPaper     = request.IsPaper.Value;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
