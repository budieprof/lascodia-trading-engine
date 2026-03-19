using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Services.Alerts.Options;

/// <summary>
/// Telegram Bot API configuration for the Telegram alert channel.
/// Bound from the <c>TelegramAlertOptions</c> section in appsettings.json.
/// </summary>
/// <remarks>
/// The <see cref="Alert.Destination"/> field on each alert stores the target <c>chat_id</c>
/// (a user, group, or channel ID). The bot must be a member of any group/channel it posts to.
/// Obtain a token from @BotFather on Telegram.
/// </remarks>
public class TelegramAlertOptions : ConfigurationOption<TelegramAlertOptions>
{
    /// <summary>Telegram bot token issued by @BotFather (e.g. "123456:ABC-DEF...").</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Request timeout in seconds for Telegram API calls. Defaults to 10.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}
