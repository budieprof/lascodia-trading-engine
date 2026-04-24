using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using LascodiaTradingEngine.API.Controllers.v1;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.UnitTest.Application.Security;

public sealed class EAIngestAuthorizationContractTests
{
    public static IEnumerable<object[]> EAIngestActions()
    {
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.Register)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.Deregister)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.Heartbeat)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceiveSymbolSpecs)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceiveTradingSessions)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceivePositionSnapshot)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceiveBrokerAccountSnapshot)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceiveOrderSnapshot)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceiveDealSnapshot)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceiveOrderBookSnapshot)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ProcessReconciliation)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.GetPendingCommands)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.AcknowledgeCommand)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ProcessSignalFeedback)];
        yield return [typeof(ExpertAdvisorController), nameof(ExpertAdvisorController.ReceivePositionDelta)];
        yield return [typeof(OrderController), nameof(OrderController.CreateFromSignal)];
        yield return [typeof(OrderController), nameof(OrderController.ExecutionReport)];
        yield return [typeof(OrderController), nameof(OrderController.ExecutionReportBatch)];
        yield return [typeof(TradeSignalController), nameof(TradeSignalController.GetPendingExecution)];
    }

    [Theory]
    [MemberData(nameof(EAIngestActions))]
    public void EAIngestAction_RequiresEAIngestPolicy(Type controllerType, string actionName)
    {
        var action = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == actionName);

        var policies = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(attribute => attribute.Policy)
            .ToArray();

        Assert.Contains(Policies.EAIngest, policies);
    }
}
