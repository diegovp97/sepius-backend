namespace Sepius.API.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Auth";

    /// <summary>Clave HMAC para firmar los tokens (mínimo 32 caracteres). Viene de Auth__JwtKey.</summary>
    public string JwtKey { get; set; } = string.Empty;

    /// <summary>Contraseña del usuario admin. Viene de Auth__AdminPassword; se guarda hasheada en la BD al arrancar.</summary>
    public string AdminPassword { get; set; } = string.Empty;

    public string Issuer { get; set; } = "sepius-api";

    public int TokenHours { get; set; } = 12;
}
