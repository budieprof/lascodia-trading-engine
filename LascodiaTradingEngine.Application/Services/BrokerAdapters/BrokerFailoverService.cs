using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Manages broker failover, allowing runtime switching between configured broker adapters.
/// Credentials and the active broker are loaded from the database.
/// </summary>
public class BrokerFailoverService : IBrokerFailover
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BrokerFailoverService> _logger;
    private string _activeBroker;

    public BrokerFailoverService(
        IServiceScopeFactory scopeFactory,
        ILogger<BrokerFailoverService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _activeBroker = "oanda"; // default until DB is read
        RefreshActiveAsync().GetAwaiter().GetResult();
    }

    public string ActiveBroker => _activeBroker;

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var broker = await readContext.GetDbContext()
            .Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.IsActive && !x.IsDeleted, ct);

        if (broker == null)
        {
            _logger.LogDebug("BrokerFailoverService.IsHealthyAsync: no active broker found in DB");
            return false;
        }

        bool healthy = broker.Status != BrokerStatus.Error;
        _logger.LogDebug(
            "BrokerFailoverService.IsHealthyAsync: broker '{Broker}' status='{Status}' healthy={Healthy}",
            broker.BrokerType, broker.Status, healthy);

        return healthy;
    }

    public async Task<bool> SwitchBrokerAsync(string brokerName, CancellationToken ct)
    {
        using var scope    = _scopeFactory.CreateScope();
        var writeContext   = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db             = writeContext.GetDbContext();

        if (!Enum.TryParse<BrokerType>(brokerName, ignoreCase: true, out var brokerType))
        {
            _logger.LogWarning(
                "BrokerFailoverService.SwitchBrokerAsync: invalid BrokerType '{BrokerName}'", brokerName);
            return false;
        }

        var target = await db.Set<Domain.Entities.Broker>()
            .FirstOrDefaultAsync(x => x.BrokerType == brokerType && !x.IsDeleted, ct);

        if (target == null)
        {
            _logger.LogWarning(
                "BrokerFailoverService.SwitchBrokerAsync: no broker with BrokerType='{BrokerName}' found in DB",
                brokerName);
            return false;
        }

        // Deactivate all
        var allActive = await db.Set<Domain.Entities.Broker>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        foreach (var b in allActive)
            b.IsActive = false;

        // Activate target
        target.IsActive = true;
        target.Status   = BrokerStatus.Connected;

        await writeContext.SaveChangesAsync(ct);

        string previous   = _activeBroker;
        _activeBroker     = brokerName;

        _logger.LogInformation(
            "BrokerFailoverService: switched active broker from '{Previous}' to '{New}'",
            previous, brokerName);

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Broker",
            EntityId     = target.Id,
            DecisionType = "BrokerSwitch",
            Outcome      = "Switched",
            Reason       = $"Active broker switched from '{previous}' to '{brokerName}'",
            Source       = "BrokerFailoverService"
        }, ct);

        return true;
    }

    private async Task RefreshActiveAsync()
    {
        try
        {
            using var scope  = _scopeFactory.CreateScope();
            var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            var broker = await readContext.GetDbContext()
                .Set<Domain.Entities.Broker>()
                .FirstOrDefaultAsync(x => x.IsActive && !x.IsDeleted);

            if (broker != null)
            {
                _activeBroker = broker.BrokerType.ToString().ToLowerInvariant();
                _logger.LogInformation(
                    "BrokerFailoverService: initialized active broker from DB as '{BrokerType}'",
                    _activeBroker);
            }
            else
            {
                _logger.LogWarning(
                    "BrokerFailoverService: no active broker found in DB, defaulting to 'oanda'");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BrokerFailoverService: failed to load active broker from DB, defaulting to 'oanda'");
        }
    }
}
