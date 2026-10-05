namespace Sepius.Application.Interfaces;

public interface IAuthService
{
    Task<bool> VerifyPasswordAsync(string username, string password);

    /// <summary>Crea o actualiza el usuario con la contraseña dada (se guarda con PBKDF2 + sal).</summary>
    Task SetPasswordAsync(string username, string password);
}
