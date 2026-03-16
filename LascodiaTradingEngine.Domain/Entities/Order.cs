using Lascodia.Trading.Engine.SharedDomain.Common;
using Lascodia.Trading.Engine.SharedDomain.Filters;

namespace LascodiaTradingEngine.Domain.Entities;

public class Order : Entity<long>
{
    public int BusinessId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;   // Buy / Sell
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = "Pending";

    [NotRequired]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
