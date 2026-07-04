namespace Sepius.Application.Interfaces;

public interface IAuthService
{
    Task<bool> VerifyPasswordAsync(string username, string password);
}
