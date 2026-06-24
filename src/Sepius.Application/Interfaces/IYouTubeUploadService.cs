using Sepius.Domain.Entities;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Contrato para subir grabaciones completadas a YouTube.
/// </summary>
public interface IYouTubeUploadService
{
    /// <summary>
    /// Sube el archivo de video al canal de YouTube configurado.
    /// Devuelve el ID del video de YouTube si tiene éxito, o null si falla.
    /// </summary>
    Task<string?> UploadAsync(Recording recording, CancellationToken ct = default);
}
