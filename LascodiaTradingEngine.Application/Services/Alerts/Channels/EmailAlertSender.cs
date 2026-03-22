using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Services.Alerts.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Alerts.Channels;

/// <summary>
/// Delivers alert notifications by SMTP email to the address stored in <see cref="Alert.Destination"/>.
/// SMTP credentials are read from <see cref="EmailAlertOptions"/> (appsettings EmailAlertOptions section).
/// If the options are not configured (Host is empty) the sender logs a warning and skips delivery.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IAlertChannelSender))]
public class EmailAlertSender : IAlertChannelSender
{
    public AlertChannel Channel => AlertChannel.Email;

    private readonly EmailAlertOptions          _options;
    private readonly ILogger<EmailAlertSender>  _logger;

    public EmailAlertSender(
        EmailAlertOptions           options,
        ILogger<EmailAlertSender>   logger)
    {
        _options = options;
        _logger  = logger;
    }

    public async Task SendAsync(Alert alert, string message, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "EmailAlertSender: SMTP is not configured — skipping delivery for alert {AlertId}. " +
                "Set EmailAlertOptions.Host and EmailAlertOptions.FromAddress in appsettings.json.",
                alert.Id);
            return;
        }

        var subject = $"[Lascodia Alert] {alert.AlertType} – {alert.Symbol}";

        var body = $"""
            Alert triggered: {alert.AlertType}
            Symbol:          {alert.Symbol}
            Message:         {message}
            Timestamp (UTC): {DateTime.UtcNow:u}

            --
            Lascodia Trading Engine
            """;

        using var smtpClient = BuildSmtpClient();
        using var mailMessage = new MailMessage
        {
            From       = new MailAddress(_options.FromAddress, _options.FromName),
            Subject    = subject,
            Body       = body,
            IsBodyHtml = false
        };
        mailMessage.To.Add(alert.Destination);

        try
        {
            await smtpClient.SendMailAsync(mailMessage, ct);
            _logger.LogDebug(
                "EmailAlertSender: alert {AlertId} delivered to {Destination}",
                alert.Id, alert.Destination);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EmailAlertSender: failed to send alert {AlertId} to {Destination}",
                alert.Id, alert.Destination);
        }
    }

    private SmtpClient BuildSmtpClient() => new(_options.Host, _options.Port)
    {
        EnableSsl   = _options.EnableSsl,
        Credentials = string.IsNullOrWhiteSpace(_options.Username)
            ? null
            : new NetworkCredential(_options.Username, _options.Password)
    };
}
