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
        var user = await _db.AuthUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null)
        {
            _logger.LogWarning("Auth attempt for unknown user: {Username}", username);
            return false;
        }

        var hash = HashPassword(password);
        var valid = user.PasswordHash == hash;
        if (!valid)
            _logger.LogWarning("Invalid password for user: {Username}", username);

        return valid;
    }

    public static string HashPassword(string password)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(bytes);
    }
}
