using System.Diagnostics;

namespace LascodiaTradingEngine.API.Middleware;

/// <summary>
/// Logs method, path, status code, and elapsed time for every HTTP request.
/// Skips health check and metrics endpoints to avoid log noise.
/// </summary>
public class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _logger;

    public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        // Skip health checks and metrics scrape (high frequency, no value in timing)
        if (path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var elapsedMs = sw.ElapsedMilliseconds;
            var statusCode = context.Response.StatusCode;
            var method = context.Request.Method;

            // Use appropriate log level based on response status and latency
            if (statusCode >= 500)
                _logger.LogError("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms", method, path, statusCode, elapsedMs);
            else if (statusCode >= 400 || elapsedMs > 2000)
                _logger.LogWarning("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms", method, path, statusCode, elapsedMs);
            else
                _logger.LogDebug("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms", method, path, statusCode, elapsedMs);
        }
    }
}
