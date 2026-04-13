using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts;

/// <summary>
/// Delivers a triggered alert notification via a specific transport channel.
/// One implementation exists per <see cref="AlertChannel"/> value and is resolved
/// at dispatch time by <see cref="AlertDispatcher"/>.
/// </summary>
public interface IAlertChannelSender
{
    /// <summary>The channel this sender handles.</summary>
    AlertChannel Channel { get; }

    /// <summary>Send the notification to the destination configured in this channel's options.</summary>
    Task SendAsync(Alert alert, string message, CancellationToken ct);
}
