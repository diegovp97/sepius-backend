using Sepius.Application.DTOs;
using Sepius.Domain.Entities;

namespace Sepius.Application.Interfaces;

/// <summary>
/// Contrato para interactuar con YouTube (subir, listar, borrar).
/// </summary>
public interface IYouTubeUploadService
{
    /// <summary>
    /// Sube el archivo de video al canal de YouTube configurado.
    /// Devuelve el ID del video de YouTube si tiene éxito, o null si falla.
    /// </summary>
    Task<string?> UploadAsync(Recording recording, CancellationToken ct = default);

    /// <summary>
    /// Lista los videos subidos al canal de YouTube autenticado.
    /// </summary>
    Task<List<YouTubeVideoDto>> GetMyVideosAsync(CancellationToken ct = default);

    /// <summary>
    /// Borra un video de YouTube por su ID.
    /// </summary>
    Task<bool> DeleteVideoAsync(string videoId, CancellationToken ct = default);

    /// <summary>
    /// Busca y borra videos de YouTube por título.
    /// </summary>
    Task<int> DeleteByTitleAsync(string title, CancellationToken ct = default);
}
