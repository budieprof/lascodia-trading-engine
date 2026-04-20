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

            // Log level ladder:
            //   5xx (server error)           → Error — operator must act
            //   Slow 2xx/3xx (>2000 ms)       → Warning — performance issue worth surfacing
            //   4xx (client error, incl 401)  → Information — client-side issue; not noise-worthy
            //   Fast success                  → Debug
            // Previously 4xx was escalated to Warning, which flooded the log at ~5/min
            // during EA authentication retry cycles (POST /ea/orderbook/snapshot → 401 in 0ms).
            if (statusCode >= 500)
                _logger.LogError("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms", method, path, statusCode, elapsedMs);
            else if (elapsedMs > 2000)
                _logger.LogWarning("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms (slow)", method, path, statusCode, elapsedMs);
            else if (statusCode >= 400)
                _logger.LogInformation("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms", method, path, statusCode, elapsedMs);
            else
                _logger.LogDebug("HTTP {Method} {Path} → {StatusCode} in {ElapsedMs}ms", method, path, statusCode, elapsedMs);
        }
    }
}
