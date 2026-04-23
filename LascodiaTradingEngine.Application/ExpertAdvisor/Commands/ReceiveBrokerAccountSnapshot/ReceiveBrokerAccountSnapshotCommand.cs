using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveBrokerAccountSnapshot;

/// <summary>
/// Receives an account-level broker state snapshot from an EA instance.
/// This feed is intentionally separate from TradingAccount balance sync so reconciliation
/// compares the engine's persisted account state with an independently reported broker sample.
/// </summary>
public sealed class ReceiveBrokerAccountSnapshotCommand : IRequest<ResponseData<string>>
{
    public required string InstanceId { get; set; }
    public decimal Balance { get; set; }
    public decimal Equity { get; set; }
    public decimal MarginUsed { get; set; }
    public decimal FreeMargin { get; set; }

    /// <summary>
    /// Broker-reported deposit currency (ISO-4217). Optional — when absent,
    /// the handler stamps the owning <see cref="TradingAccount.Currency"/>.
    /// </summary>
    public string? Currency { get; set; }

    public DateTime? ReportedAt { get; set; }
}

public sealed class ReceiveBrokerAccountSnapshotCommandValidator
    : AbstractValidator<ReceiveBrokerAccountSnapshotCommand>
{
    public ReceiveBrokerAccountSnapshotCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");

        RuleFor(x => x.Balance)
            .GreaterThanOrEqualTo(0m).WithMessage("Balance must be greater than or equal to zero");

        RuleFor(x => x.Equity)
            .GreaterThanOrEqualTo(0m).WithMessage("Equity must be greater than or equal to zero");

        RuleFor(x => x.MarginUsed)
            .GreaterThanOrEqualTo(0m).WithMessage("MarginUsed must be greater than or equal to zero");

        RuleFor(x => x.FreeMargin)
            .GreaterThanOrEqualTo(0m).WithMessage("FreeMargin must be greater than or equal to zero");

        RuleFor(x => x.Currency!)
            .MaximumLength(10).WithMessage("Currency must be at most 10 characters")
            .When(x => !string.IsNullOrEmpty(x.Currency));
    }
}

public sealed class ReceiveBrokerAccountSnapshotCommandHandler
    : IRequestHandler<ReceiveBrokerAccountSnapshotCommand, ResponseData<string>>
{
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ReceiveBrokerAccountSnapshotCommandHandler(
        IWriteApplicationDbContext context,
        IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(
        ReceiveBrokerAccountSnapshotCommand request,
        CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var nowUtc = DateTime.UtcNow;
        var reportedAtUtc = NormalizeUtc(request.ReportedAt ?? nowUtc);
        if (reportedAtUtc > nowUtc.Add(FutureTolerance))
            return ResponseData<string>.Init(null, false, "ReportedAt is too far in the future", "-11");

        var dbContext = _context.GetDbContext();
        var eaInstance = await dbContext.Set<EAInstance>()
            .FirstOrDefaultAsync(
                x => x.InstanceId == request.InstanceId
                  && (x.Status == EAInstanceStatus.Active || x.Status == EAInstanceStatus.Disconnected)
                  && !x.IsDeleted,
                cancellationToken);

        if (eaInstance is null)
            return ResponseData<string>.Init(null, false, "EA instance not found or not active", "-14");

        if (eaInstance.Status == EAInstanceStatus.Disconnected)
            eaInstance.Status = EAInstanceStatus.Active;

        eaInstance.LastHeartbeat = nowUtc;

        string currency = request.Currency?.Trim() ?? string.Empty;
        if (currency.Length == 0)
        {
            currency = await dbContext.Set<TradingAccount>()
                .Where(a => a.Id == eaInstance.TradingAccountId && !a.IsDeleted)
                .Select(a => a.Currency)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        }

        dbContext.Set<BrokerAccountSnapshot>().Add(new BrokerAccountSnapshot
        {
            TradingAccountId = eaInstance.TradingAccountId,
            InstanceId = request.InstanceId,
            Balance = request.Balance,
            Equity = request.Equity,
            MarginUsed = request.MarginUsed,
            FreeMargin = request.FreeMargin,
            Currency = currency,
            ReportedAt = reportedAtUtc,
        });

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Snapshot recorded", true, "Successful", "00");
    }

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
}
