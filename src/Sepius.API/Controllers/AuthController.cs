using Microsoft.AspNetCore.Mvc;
using Sepius.Application.Interfaces;

namespace Sepius.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) => _auth = auth;

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] LoginRequest request)
    {
        var valid = await _auth.VerifyPasswordAsync(request.Username, request.Password);
        if (!valid) return Unauthorized();
        return Ok(new { token = "ok" });
    }
}

public record LoginRequest(string Username, string Password);
