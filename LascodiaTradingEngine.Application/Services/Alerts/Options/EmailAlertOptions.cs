using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Services.Alerts.Options;

/// <summary>
/// SMTP configuration for the Email alert channel.
/// Bound from the <c>EmailAlertOptions</c> section in appsettings.json.
/// </summary>
public class EmailAlertOptions : ConfigurationOption<EmailAlertOptions>
{
    /// <summary>SMTP server hostname (e.g. "smtp.sendgrid.net").</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP port. Defaults to 587 (STARTTLS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>SMTP username / API key username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>SMTP password / API key.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether to use SSL/TLS. Defaults to <c>true</c>.</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>The recipient email address for alert notifications.</summary>
    public string ToAddress { get; set; } = string.Empty;

    /// <summary>The From address that appears in the email header.</summary>
    public string FromAddress { get; set; } = "alerts@lascodia.com";

    /// <summary>The display name that accompanies <see cref="FromAddress"/>.</summary>
    public string FromName { get; set; } = "Lascodia Trading Engine";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress) && !string.IsNullOrWhiteSpace(ToAddress);
}
