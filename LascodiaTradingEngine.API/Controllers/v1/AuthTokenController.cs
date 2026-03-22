using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Development-only endpoint for generating JWT tokens.
/// This controller is only registered when the application runs in the Development environment.
/// In production, tokens should be issued by your identity provider.
/// </summary>
[Route("api/v1/lascodia-trading-engine/auth")]
[ApiController]
public class AuthTokenController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public AuthTokenController(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment   = environment;
    }

    /// <summary>
    /// Generates a signed JWT token for development and testing.
    /// Only available in the Development environment.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public IActionResult GenerateToken([FromBody] GenerateTokenRequest request)
    {
        if (!_environment.IsDevelopment())
            return NotFound();

        var jwtSection = _configuration.GetSection("JwtSettings");
        var secretKey  = jwtSection["SecretKey"];
        var issuer     = jwtSection["Issuer"]   ?? "lascodia-trading-engine";
        var audience   = jwtSection["Audience"] ?? "lascodia-trading-engine-api";
        var expirationMinutes = int.TryParse(jwtSection["ExpirationMinutes"], out var exp) ? exp : 480;

        if (string.IsNullOrEmpty(secretKey))
            return BadRequest(new { message = "JwtSettings:SecretKey is not configured." });

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        // Include the custom claims that CurrentUserService.GetUserFromJwtToken() expects
        var claims = new List<Claim>
        {
            new("passportId", request.UserId ?? "dev-user-1"),
            new("firstName",  request.FirstName ?? "Dev"),
            new("lastName",   request.LastName ?? "User"),
            new("email",      request.Email ?? "dev@lascodia.com"),
            new("mobileNo",   request.PhoneNumber ?? ""),
            new(JwtRegisteredClaimNames.Sub, request.UserId ?? "dev-user-1"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (request.BusinessId.HasValue)
            claims.Add(new Claim("businessId", request.BusinessId.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            expires:            DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new
        {
            token      = tokenString,
            expiresAt  = token.ValidTo,
            tokenType  = "Bearer"
        });
    }
}

/// <summary>
/// Request payload for the dev token generation endpoint.
/// All fields are optional — sensible defaults are used when omitted.
/// </summary>
public class GenerateTokenRequest
{
    public string? UserId      { get; set; }
    public string? FirstName   { get; set; }
    public string? LastName    { get; set; }
    public string? Email       { get; set; }
    public string? PhoneNumber { get; set; }
    public int?    BusinessId  { get; set; }
}
