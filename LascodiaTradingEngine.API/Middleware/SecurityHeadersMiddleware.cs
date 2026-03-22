namespace LascodiaTradingEngine.API.Middleware;

/// <summary>
/// Adds security response headers to every HTTP response.
/// These headers protect against common web attacks (XSS, clickjacking, MIME sniffing).
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Prevent MIME type sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // Prevent clickjacking
        headers["X-Frame-Options"] = "DENY";

        // XSS protection (legacy browsers)
        headers["X-XSS-Protection"] = "1; mode=block";

        // Referrer policy — don't leak URLs
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Permissions policy — disable unused browser features
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        // Content Security Policy — relax for Swagger UI, strict for API endpoints
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:";
        }
        else
        {
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        }

        // HSTS — enforce HTTPS (only in production, 1 year)
        if (!context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        await _next(context);
    }
}
