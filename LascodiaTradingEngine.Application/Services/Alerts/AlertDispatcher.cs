using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts;

/// <summary>
/// Routes a triggered alert to the appropriate <see cref="IAlertChannelSender"/>
/// based on <see cref="Alert.Channel"/>. Each channel has its own dedicated sender
/// (Webhook, Email, Telegram) registered in DI.
/// </summary>
public class AlertDispatcher : IAlertDispatcher
{
    private readonly IReadOnlyDictionary<AlertChannel, IAlertChannelSender> _senders;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IEnumerable<IAlertChannelSender> senders,
        ILogger<AlertDispatcher>         logger)
    {
        _senders = senders.ToDictionary(s => s.Channel);
        _logger  = logger;
    }

    public async Task DispatchAsync(Alert alert, string message, CancellationToken ct)
    {
        if (_senders.TryGetValue(alert.Channel, out var sender))
        {
            await sender.SendAsync(alert, message, ct);
        }
        else
        {
            _logger.LogWarning(
                "AlertDispatcher: no sender registered for channel {Channel} (alert {AlertId})",
                alert.Channel, alert.Id);
        }
    }
}
