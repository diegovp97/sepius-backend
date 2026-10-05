using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Sepius.API.Auth;

public sealed class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _opts = options.Value;

    public static SymmetricSecurityKey BuildKey(string jwtKey) =>
        new(Encoding.UTF8.GetBytes(jwtKey));

    public (string Token, DateTimeOffset ExpiresAt) Create(string username)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(_opts.TokenHours);
        var jwt = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Issuer,
            claims: [new Claim(ClaimTypes.Name, username)],
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(BuildKey(_opts.JwtKey), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
