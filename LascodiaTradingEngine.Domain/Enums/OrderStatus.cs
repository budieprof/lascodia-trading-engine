namespace LascodiaTradingEngine.Domain.Enums;

public enum OrderStatus
{
    Pending     = 0,
    Submitted   = 1,
    PartialFill = 2,
    Filled      = 3,
    Cancelled   = 4,
    Rejected    = 5,
    Expired     = 6
}
