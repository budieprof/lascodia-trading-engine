using System.Text.Json;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

internal sealed class ValidationRunException : Exception
{
    internal ValidationRunException(
        ValidationFailureCode failureCode,
        string message,
        bool isTransient = false,
        string? failureDetailsJson = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
        IsTransient = isTransient;
        FailureDetailsJson = failureDetailsJson;
    }

    internal ValidationFailureCode FailureCode { get; }

    internal bool IsTransient { get; }

    internal string? FailureDetailsJson { get; }

    internal static string SerializeDetails(object payload)
        => JsonSerializer.Serialize(payload);
}
