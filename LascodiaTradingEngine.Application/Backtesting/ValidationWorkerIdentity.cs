namespace LascodiaTradingEngine.Application.Backtesting;

public interface IValidationWorkerIdentity
{
    string InstanceId { get; }
}

internal sealed class ValidationWorkerIdentity : IValidationWorkerIdentity
{
    public ValidationWorkerIdentity()
    {
        InstanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    public string InstanceId { get; }
}
