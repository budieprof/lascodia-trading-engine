using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands;

internal static class ExecutionReportStatusMapper
{
    private static readonly HashSet<string> KnownStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Filled",
        "PartialFill",
        "Rejected",
        "Failed",
        "Cancelled",
        "Dispatched",
        "TransientRetry",
        "SpreadDeferred",
        "Closed",
        "Reversed",
        "Expired",
        "Unmatched",
        "UnmatchedFill",
        "UnmatchedClose",
        "EvictedUnmatched",
        "Duplicate",
        "None",
    };

    public const string ValidStatusMessage =
        "Status must be one of: Filled, PartialFill, Rejected, Failed, Cancelled, Dispatched, TransientRetry, SpreadDeferred, Closed, Reversed, Expired, Unmatched, UnmatchedFill, UnmatchedClose, EvictedUnmatched, Duplicate, or None";

    public static bool IsKnown(string? status)
        => !string.IsNullOrWhiteSpace(status) && KnownStatuses.Contains(status.Trim());

    public static bool TryMapToOrderStatus(string? status, out OrderStatus orderStatus)
    {
        switch (status?.Trim().ToUpperInvariant())
        {
            case "FILLED":
            case "UNMATCHEDFILL":
                orderStatus = OrderStatus.Filled;
                return true;

            case "PARTIALFILL":
                orderStatus = OrderStatus.PartialFill;
                return true;

            case "REJECTED":
            case "FAILED":
                orderStatus = OrderStatus.Rejected;
                return true;

            case "CANCELLED":
                orderStatus = OrderStatus.Cancelled;
                return true;

            case "EXPIRED":
                orderStatus = OrderStatus.Expired;
                return true;

            case "DISPATCHED":
                orderStatus = OrderStatus.Submitted;
                return true;

            default:
                orderStatus = default;
                return false;
        }
    }
}
