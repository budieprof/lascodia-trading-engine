using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StressTest.Commands.RunStressTest;

/// <summary>Manually triggers a stress test scenario against a specific account.</summary>
public class RunStressTestCommand : IRequest<ResponseData<long>>
{
    public long ScenarioId { get; set; }
    public long TradingAccountId { get; set; }
}

public class RunStressTestCommandValidator : AbstractValidator<RunStressTestCommand>
{
    public RunStressTestCommandValidator()
    {
        RuleFor(x => x.ScenarioId).GreaterThan(0);
        RuleFor(x => x.TradingAccountId).GreaterThan(0);
    }
}

public class RunStressTestCommandHandler : IRequestHandler<RunStressTestCommand, ResponseData<long>>
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly IStressTestEngine _stressEngine;

    public RunStressTestCommandHandler(
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        IStressTestEngine stressEngine)
    {
        _readContext  = readContext;
        _writeContext = writeContext;
        _stressEngine = stressEngine;
    }

    public async Task<ResponseData<long>> Handle(
        RunStressTestCommand request,
        CancellationToken cancellationToken)
    {
        var scenario = await _readContext.GetDbContext()
            .Set<StressTestScenario>()
            .FirstOrDefaultAsync(s => s.Id == request.ScenarioId && !s.IsDeleted, cancellationToken);

        if (scenario is null)
            return ResponseData<long>.Init(0, false, "Scenario not found", "-14");

        var account = await _readContext.GetDbContext()
            .Set<TradingAccount>()
            .FirstOrDefaultAsync(a => a.Id == request.TradingAccountId && !a.IsDeleted, cancellationToken);

        if (account is null)
            return ResponseData<long>.Init(0, false, "Account not found", "-14");

        var positions = await _readContext.GetDbContext()
            .Set<Position>()
            .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
            .ToListAsync(cancellationToken);

        var result = await _stressEngine.RunScenarioAsync(scenario, account, positions, cancellationToken);

        await _writeContext.GetDbContext().Set<StressTestResult>().AddAsync(result, cancellationToken);
        await _writeContext.GetDbContext().SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(result.Id, true, "Stress test completed", "00");
    }
}
