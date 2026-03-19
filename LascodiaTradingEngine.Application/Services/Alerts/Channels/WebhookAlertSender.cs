using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Services.Alerts.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts.Channels;

/// <summary>
/// Delivers alert notifications by HTTP POST to the URL stored in <see cref="Alert.Destination"/>.
/// The request body is a JSON object with Symbol, AlertType, Message, and Timestamp.
/// An optional <c>X-Lascodia-Secret</c> header is included when <see cref="WebhookAlertOptions.SharedSecret"/> is set.
/// </summary>
public class WebhookAlertSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Webhook;

    private readonly IHttpClientFactory     _httpClientFactory;
    private readonly WebhookAlertOptions    _options;
    private readonly ILogger<WebhookAlertSender> _logger;

    public WebhookAlertSender(
        IHttpClientFactory          httpClientFactory,
        WebhookAlertOptions         options,
        ILogger<WebhookAlertSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options           = options;
        _logger            = logger;
    }

    public async Task SendAsync(Alert alert, string message, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("AlertWebhook");

        var payload = new
        {
            AlertId   = alert.Id,
            Symbol    = alert.Symbol,
            AlertType = alert.AlertType.ToString(),
            Message   = message,
            Timestamp = DateTime.UtcNow
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, alert.Destination);
        request.Content = JsonContent.Create(payload);

        if (!string.IsNullOrWhiteSpace(_options.SharedSecret))
            request.Headers.TryAddWithoutValidation("X-Lascodia-Secret", _options.SharedSecret);

        try
        {
            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "WebhookAlertSender: POST to {Url} returned {StatusCode} for alert {AlertId}",
                    alert.Destination, (int)response.StatusCode, alert.Id);
            }
            else
            {
                _logger.LogDebug(
                    "WebhookAlertSender: alert {AlertId} delivered to {Url}",
                    alert.Id, alert.Destination);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WebhookAlertSender: failed to POST alert {AlertId} to {Url}",
                alert.Id, alert.Destination);
        }
    }
}
