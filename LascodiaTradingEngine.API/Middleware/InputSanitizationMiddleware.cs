using System.Text.RegularExpressions;

namespace LascodiaTradingEngine.API.Middleware;

/// <summary>
/// Rejects requests containing common injection patterns in query strings and headers.
/// This is a defense-in-depth measure — FluentValidation handles field-level validation,
/// but this middleware catches payloads before they reach any handler.
/// </summary>
public sealed partial class InputSanitizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InputSanitizationMiddleware> _logger;

    public InputSanitizationMiddleware(RequestDelegate next, ILogger<InputSanitizationMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check query string for injection patterns
        var query = context.Request.QueryString.Value;
        if (!string.IsNullOrEmpty(query) && ContainsInjectionPattern(query))
        {
            _logger.LogWarning(
                "InputSanitization: blocked request with suspicious query string from {IP} — {Path}",
                context.Connection.RemoteIpAddress, context.Request.Path);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { message = "Request rejected: invalid characters in query string." });
            return;
        }

        // Check custom headers for injection (skip standard headers)
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                var value = header.Value.ToString();
                if (ContainsInjectionPattern(value))
                {
                    _logger.LogWarning(
                        "InputSanitization: blocked request with suspicious header '{Header}' from {IP}",
                        header.Key, context.Connection.RemoteIpAddress);
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { message = "Request rejected: invalid characters in headers." });
                    return;
                }
            }
        }

        await _next(context);
    }

    private static bool ContainsInjectionPattern(string input)
    {
        return ScriptTagPattern().IsMatch(input) ||
               SqlInjectionPattern().IsMatch(input);
    }

    [GeneratedRegex(@"<script|javascript:|on\w+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagPattern();

    [GeneratedRegex(@"('|""|;)\s*(DROP|ALTER|DELETE|INSERT|UPDATE|UNION|SELECT)\s", RegexOptions.IgnoreCase)]
    private static partial Regex SqlInjectionPattern();
}
