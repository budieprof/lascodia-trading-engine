using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Services.Alerts.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts.Channels;

/// <summary>
/// Delivers alert notifications by HTTP POST to the URL configured in <see cref="WebhookAlertOptions.Url"/>.
/// The request body is a JSON object with Symbol, AlertType, Message, and Timestamp.
/// An optional <c>X-Lascodia-Secret</c> header is included when <see cref="WebhookAlertOptions.SharedSecret"/> is set.
/// If the URL is not configured, the sender logs a warning and skips delivery.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IAlertChannelSender))]
public class WebhookAlertSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Webhook;
    public string Destination => _options.Url;

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
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "WebhookAlertSender: Url is not configured — skipping delivery for alert {AlertId}. " +
                "Set WebhookAlertOptions.Url in appsettings.json.",
                alert.Id);
            return;
        }

        var client = _httpClientFactory.CreateClient("AlertWebhook");

        var payload = new
        {
            AlertId   = alert.Id,
            Symbol    = alert.Symbol,
            AlertType = alert.AlertType.ToString(),
            Message   = message,
            Timestamp = DateTime.UtcNow
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url);
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
                    _options.Url, (int)response.StatusCode, alert.Id);
            }
            else
            {
                _logger.LogDebug(
                    "WebhookAlertSender: alert {AlertId} delivered to {Url}",
                    alert.Id, _options.Url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WebhookAlertSender: failed to POST alert {AlertId} to {Url}",
                alert.Id, _options.Url);
        }
    }
}
