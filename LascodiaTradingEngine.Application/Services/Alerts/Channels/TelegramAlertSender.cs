using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Services.Alerts.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts.Channels;

/// <summary>
/// Delivers alert notifications via the Telegram Bot API to the chat_id configured in
/// <see cref="TelegramAlertOptions.ChatId"/>.
/// If the options are not configured (BotToken or ChatId is empty) the sender logs a warning and skips delivery.
/// </summary>
/// <remarks>
/// The bot must be invited to any group/channel before it can post.
/// </remarks>
[RegisterService(ServiceLifetime.Scoped, typeof(IAlertChannelSender))]
public class TelegramAlertSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Telegram;
    public string Destination => _options.ChatId;

    private readonly IHttpClientFactory             _httpClientFactory;
    private readonly TelegramAlertOptions           _options;
    private readonly ILogger<TelegramAlertSender>   _logger;

    private const string TelegramApiBase = "https://api.telegram.org";

    public TelegramAlertSender(
        IHttpClientFactory              httpClientFactory,
        TelegramAlertOptions            options,
        ILogger<TelegramAlertSender>    logger)
    {
        _httpClientFactory = httpClientFactory;
        _options           = options;
        _logger            = logger;
    }

    public async Task SendAsync(Alert alert, string message, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "TelegramAlertSender: BotToken is not configured — skipping delivery for alert {AlertId}. " +
                "Set TelegramAlertOptions.BotToken in appsettings.json.",
                alert.Id);
            return;
        }

        var text = FormatMessage(alert, message);
        var url  = $"{TelegramApiBase}/bot{_options.BotToken}/sendMessage";

        var payload = new
        {
            chat_id    = _options.ChatId,
            text       = text,
            parse_mode = "HTML"
        };

        var client = _httpClientFactory.CreateClient("AlertTelegram");

        try
        {
            var response = await client.PostAsJsonAsync(url, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "TelegramAlertSender: Telegram API returned {StatusCode} for alert {AlertId}. Body: {Body}",
                    (int)response.StatusCode, alert.Id, body);
            }
            else
            {
                _logger.LogDebug(
                    "TelegramAlertSender: alert {AlertId} delivered to chat_id={ChatId}",
                    alert.Id, _options.ChatId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TelegramAlertSender: failed to send alert {AlertId} to chat_id={ChatId}",
                alert.Id, _options.ChatId);
        }
    }

    private static string FormatMessage(Alert alert, string message) =>
        $"<b>🔔 Lascodia Alert</b>\n" +
        $"<b>Type:</b> {alert.AlertType}\n" +
        $"<b>Symbol:</b> {alert.Symbol}\n" +
        $"<b>Message:</b> {message}\n" +
        $"<i>{DateTime.UtcNow:u}</i>";
}
