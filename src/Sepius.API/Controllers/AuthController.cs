using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sepius.API.Auth;
using Sepius.Application.Interfaces;

namespace Sepius.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly TokenService _tokens;

    public AuthController(IAuthService auth, TokenService tokens)
    {
        _auth = auth;
        _tokens = tokens;
    }

    [HttpPost("verify")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Verify([FromBody] LoginRequest request)
    {
        var valid = await _auth.VerifyPasswordAsync(request.Username, request.Password);
        if (!valid) return Unauthorized();

        var (token, expiresAt) = _tokens.Create(request.Username);
        return Ok(new { token, expiresAt });
    }
}

public record LoginRequest(string Username, string Password);
