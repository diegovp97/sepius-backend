using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.YouTube;

/// <summary>
/// Sube grabaciones completadas a YouTube usando la Data API v3.
///
/// AUTENTICACIÓN:
/// Usa OAuth2 con flujo "installed application". La primera vez que se ejecute,
/// abrirá el navegador para que el usuario autorice la app. El token se guarda
/// en disco (TokenStorePath) y se renueva automáticamente.
///
/// PREREQUISITOS:
/// 1. Crear un proyecto en Google Cloud Console.
/// 2. Habilitar "YouTube Data API v3".
/// 3. Crear credenciales OAuth2 de tipo "Aplicación de escritorio".
/// 4. Descargar client_secrets.json y poner la ruta en appsettings.json.
/// </summary>
public sealed class YouTubeUploadService : IYouTubeUploadService
{
    private readonly YouTubeOptions _options;
    private readonly ILogger<YouTubeUploadService> _logger;

    public YouTubeUploadService(
        IOptions<YouTubeOptions> options,
        ILogger<YouTubeUploadService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> UploadAsync(Recording recording, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Subida a YouTube deshabilitada (YouTube:Enabled = false).");
            return null;
        }

        if (!File.Exists(recording.FilePath))
        {
            _logger.LogWarning(
                "No se puede subir '{File}': el archivo no existe.", recording.FilePath);
            return null;
        }

        if (!File.Exists(_options.ClientSecretsPath))
        {
            _logger.LogError(
                "No se encontró client_secrets.json en '{Path}'. " +
                "Descárgalo desde Google Cloud Console y configura YouTube:ClientSecretsPath.",
                _options.ClientSecretsPath);
            return null;
        }

        try
        {
            _logger.LogInformation(
                "Iniciando subida a YouTube: '{File}' ({SizeMB:F1} MB)",
                recording.FileName,
                recording.FileSizeBytes / 1_048_576.0);

            var credential = await AuthorizeAsync(ct);
            var youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Sepius"
            });

            var video = BuildVideoMetadata(recording);
            using var fileStream = new FileStream(
                recording.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 262_144,
                useAsync: true);

            var insertRequest = youtubeService.Videos.Insert(
                video,
                "snippet,status",
                fileStream,
                "video/*");

            insertRequest.ChunkSize = ResumableUpload.MinimumChunkSize * 4; // 1 MB chunks
            insertRequest.ProgressChanged += progress => LogProgress(progress, recording.FileName);
            insertRequest.ResponseReceived += response =>
                _logger.LogInformation(
                    "Video '{File}' subido correctamente. ID: {VideoId} | URL: https://youtu.be/{VideoId}",
                    recording.FileName, response.Id, response.Id);

            var result = await insertRequest.UploadAsync(ct);

            if (result.Status == UploadStatus.Failed)
            {
                _logger.LogError(result.Exception,
                    "Error al subir '{File}' a YouTube.", recording.FileName);
                return null;
            }

            return insertRequest.ResponseBody?.Id;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Subida a YouTube cancelada para '{File}'.", recording.FileName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al subir '{File}' a YouTube.", recording.FileName);
            return null;
        }
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken ct)
    {
        await using var secretsStream = File.OpenRead(_options.ClientSecretsPath);
        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(secretsStream).Secrets,
            [YouTubeService.Scope.YoutubeUpload],
            "user",
            ct,
            new Google.Apis.Util.Store.FileDataStore(_options.TokenStorePath, fullPath: false));
    }

    private Video BuildVideoMetadata(Recording recording)
    {
        var startedAt = recording.StartedAt.ToLocalTime();
        var title = $"{recording.ChannelName} - {startedAt:yyyy-MM-dd HH:mm}";
        var description =
            $"Stream de {recording.ChannelName} grabado el {startedAt:dd/MM/yyyy} a las {startedAt:HH:mm}.\n" +
            $"Grabado automáticamente por Sepius.";

        return new Video
        {
            Snippet = new VideoSnippet
            {
                Title = title.Length > 100 ? title[..100] : title,
                Description = description,
                Tags = [recording.ChannelName, "stream", "twitch", "grabación"],
                CategoryId = "20" // Gaming
            },
            Status = new VideoStatus
            {
                PrivacyStatus = _options.PrivacyStatus
            }
        };
    }

    private void LogProgress(IUploadProgress progress, string fileName)
    {
        switch (progress.Status)
        {
            case UploadStatus.Uploading:
                _logger.LogDebug(
                    "Subiendo '{File}': {MB:F1} MB enviados",
                    fileName, progress.BytesSent / 1_048_576.0);
                break;
            case UploadStatus.Failed:
                _logger.LogError(progress.Exception,
                    "Fallo durante la subida de '{File}'.", fileName);
                break;
        }
    }
}
