using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sepius.Application.Interfaces;
using Sepius.Infrastructure.Persistence;

namespace Sepius.Infrastructure.Services;

public class AuthService : IAuthService
{
    // Formato almacenado: pbkdf2-sha256$<iteraciones>$<salt b64>$<hash b64>
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

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
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT password_hash FROM auth_users WHERE username = @u";
            AddParam(cmd, "@u", username);
            var stored = await cmd.ExecuteScalarAsync() as string;

            // Se calcula siempre un hash para que el tiempo de respuesta no delate si el usuario existe.
            var valid = Verify(password, stored);
            if (!valid)
                _logger.LogWarning("Intento de login fallido para el usuario: {Username}", username);
            return valid;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task SetPasswordAsync(string username, string password)
    {
        var hash = Hash(password);
        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE auth_users SET password_hash = @h WHERE username = @u;
                INSERT INTO auth_users (username, password_hash)
                SELECT @u, @h WHERE NOT EXISTS (SELECT 1 FROM auth_users WHERE username = @u);";
            AddParam(cmd, "@u", username);
            AddParam(cmd, "@h", hash);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations))
        {
            // Usuario inexistente o formato antiguo: se gasta el mismo tiempo y se rechaza.
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[SaltSize], Iterations, HashAlgorithmName.SHA256, HashSize);
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
