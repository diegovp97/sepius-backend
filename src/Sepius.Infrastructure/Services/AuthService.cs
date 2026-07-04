using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.Persistence;

namespace Sepius.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuthService> _logger;

    public AuthService(AppDbContext db, ILogger<AuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> VerifyPasswordAsync(string username, string password)
    {
        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT password_hash FROM auth_users WHERE username = @u";
        var p = cmd.CreateParameter();
        p.ParameterName = "@u";
        p.Value = username;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync();
        await conn.CloseAsync();

        if (result is null)
        {
            _logger.LogWarning("Auth attempt for unknown user: {Username}", username);
            return false;
        }

        var hash = HashPassword(password);
        var valid = (string)result == hash;
        if (!valid)
            _logger.LogWarning("Invalid password for user: {Username}", username);

        return valid;
    }

    private static string HashPassword(string password)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(bytes);
    }
}
