using Sepius.Domain.Entities;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Copia de seguridad de las grabaciones en Google Drive.
/// </summary>
public interface IDriveUploadService
{
    /// <summary>
    /// Sube el .mp4 a Drive (carpeta Sepius/&lt;canal&gt;). Es idempotente: si ya existe un
    /// fichero con el mismo nombre y tamaño, devuelve su ID sin volver a subirlo.
    /// Devuelve el ID del fichero en Drive, o null si falla.
    /// </summary>
    Task<string?> UploadAsync(Recording recording, CancellationToken ct = default);
}
