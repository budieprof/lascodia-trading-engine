using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Services.Alerts.Options;

/// <summary>
/// HTTP configuration for the Webhook alert channel.
/// Bound from the <c>WebhookAlertOptions</c> section in appsettings.json.
/// </summary>
/// <remarks>
/// The <see cref="Alert.Destination"/> field on each alert stores the full callback URL
/// that will receive a POST request when the alert fires.
/// </remarks>
public class WebhookAlertOptions : ConfigurationOption<WebhookAlertOptions>
{
    /// <summary>Request timeout in seconds for outbound webhook calls. Defaults to 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Optional shared secret added as an <c>X-Lascodia-Secret</c> header so receivers
    /// can verify the request originated from this engine.
    /// Leave empty to omit the header.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;
}
