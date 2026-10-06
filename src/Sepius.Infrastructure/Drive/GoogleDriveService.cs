using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.YouTube;

namespace Sepius.Infrastructure.Drive;

/// <summary>
/// Sube las grabaciones a Google Drive con la API REST (subida reanudable por trozos).
/// Usa el scope drive.file: la app solo ve lo que ella misma crea.
/// </summary>
public sealed class GoogleDriveService : IDriveUploadService
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string FilesUrl = "https://www.googleapis.com/drive/v3/files";
    private const string UploadUrl = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";

    /// <summary>Drive exige trozos múltiplos de 256 KiB (salvo el último).</summary>
    private const int ChunkSize = 16 * 1024 * 1024;
    private const int MaxChunkAttempts = 6;

    private readonly GoogleDriveOptions _options;
    private readonly YouTubeOptions _youtube;
    private readonly ILogger<GoogleDriveService> _logger;
    private readonly HttpClient _http;

    /// <summary>Pausa entre reintentos de un trozo. Sobrescribible en pruebas.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Min(2 << attempt, 60));

    public GoogleDriveService(
        IOptions<GoogleDriveOptions> options,
        IOptions<YouTubeOptions> youtube,
        ILogger<GoogleDriveService> logger,
        HttpClient http)
    {
        _options = options.Value;
        _youtube = youtube.Value;
        _logger = logger;
        _http = http;
    }

    private string ClientId => string.IsNullOrWhiteSpace(_options.ClientId) ? _youtube.ClientId : _options.ClientId;
    private string ClientSecret => string.IsNullOrWhiteSpace(_options.ClientSecret) ? _youtube.ClientSecret : _options.ClientSecret;

    public async Task<string?> UploadAsync(Recording recording, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return null;

        if (!File.Exists(recording.FilePath))
        {
            _logger.LogWarning("[Drive] No se puede subir '{File}': no existe.", recording.FilePath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.RefreshToken) || string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            _logger.LogError("[Drive] Falta GoogleDrive:RefreshToken (o el ClientId/ClientSecret de Google).");
            return null;
        }

        try
        {
            var length = new FileInfo(recording.FilePath).Length;
            _logger.LogInformation("[Drive] Subiendo '{File}' ({SizeMB:F1} MB)…", recording.FileName, length / 1_048_576.0);

            var token = await GetAccessTokenAsync(ct);
            if (token is null) return null;

            var rootId = await EnsureFolderAsync(_options.RootFolderName, "root", token, ct);
            var channelId = await EnsureFolderAsync(string.IsNullOrWhiteSpace(recording.ChannelName) ? "sin-canal" : recording.ChannelName, rootId, token, ct);

            // Idempotente: si ya está subido (mismo nombre y tamaño) no se repite.
            var existing = await FindFileAsync(recording.FileName, channelId, token, ct);
            if (existing is { } e && e.Size == length)
            {
                _logger.LogInformation("[Drive] '{File}' ya estaba en Drive ({Id}). Sin cambios.", recording.FileName, e.Id);
                return e.Id;
            }

            var sessionUri = await StartResumableSessionAsync(recording.FileName, channelId, length, token, ct);
            if (sessionUri is null) return null;

            var fileId = await UploadChunksAsync(recording, sessionUri, length, ct);
            if (fileId is not null)
                _logger.LogInformation("[Drive] OK: '{File}' → {Id}", recording.FileName, fileId);
            return fileId;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Drive] Subida cancelada: '{File}'.", recording.FileName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Drive] Error inesperado subiendo '{File}'.", recording.FileName);
            return null;
        }
    }

    // ── OAuth ────────────────────────────────────────────────────────────────

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = _options.RefreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant = refresh token caducado/revocado (p. ej. app de Google en modo "Pruebas": caduca a los 7 días)
            _logger.LogError("[Drive] No se pudo renovar el token ({Status}): {Body}", response.StatusCode, body);
            return null;
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString();
    }

    // ── Carpetas y ficheros ──────────────────────────────────────────────────

    private async Task<string> EnsureFolderAsync(string name, string parentId, string token, CancellationToken ct)
    {
        var q = $"mimeType='{FolderMime}' and name='{Escape(name)}' and '{parentId}' in parents and trashed=false";
        var url = $"{FilesUrl}?q={Uri.EscapeDataString(q)}&fields=files(id)&spaces=drive&pageSize=1";

        using (var req = Authorized(HttpMethod.Get, url, token))
        using (var res = await _http.SendAsync(req, ct))
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.EnsureSuccessStatusCode();
            var files = JsonDocument.Parse(body).RootElement.GetProperty("files");
            if (files.GetArrayLength() > 0)
                return files[0].GetProperty("id").GetString()!;
        }

        var metadata = JsonSerializer.Serialize(new { name, mimeType = FolderMime, parents = new[] { parentId } });
        using var create = Authorized(HttpMethod.Post, $"{FilesUrl}?fields=id", token);
        create.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
        using var created = await _http.SendAsync(create, ct);
        var createdBody = await created.Content.ReadAsStringAsync(ct);
        created.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString()!;
        _logger.LogInformation("[Drive] Carpeta creada: '{Name}' ({Id})", name, id);
        return id;
    }

    private async Task<(string Id, long Size)?> FindFileAsync(string name, string parentId, string token, CancellationToken ct)
    {
        var q = $"name='{Escape(name)}' and '{parentId}' in parents and trashed=false and mimeType!='{FolderMime}'";
        var url = $"{FilesUrl}?q={Uri.EscapeDataString(q)}&fields=files(id,size)&spaces=drive&pageSize=10";

        using var req = Authorized(HttpMethod.Get, url, token);
        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        res.EnsureSuccessStatusCode();

        foreach (var f in JsonDocument.Parse(body).RootElement.GetProperty("files").EnumerateArray())
        {
            var size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var n) ? n : -1;
            return (f.GetProperty("id").GetString()!, size);
        }
        return null;
    }

    // ── Subida reanudable ────────────────────────────────────────────────────

    private async Task<string?> StartResumableSessionAsync(string fileName, string parentId, long length, string token, CancellationToken ct)
    {
        var metadata = JsonSerializer.Serialize(new { name = fileName, parents = new[] { parentId } });
        using var req = Authorized(HttpMethod.Post, $"{UploadUrl}?uploadType=resumable&fields=id", token);
        req.Headers.Add("X-Upload-Content-Type", "video/mp4");
        req.Headers.Add("X-Upload-Content-Length", length.ToString());
        req.Content = new StringContent(metadata, Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("[Drive] No se pudo iniciar la subida ({Status}): {Body}", res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            return null;
        }

        var uri = res.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(uri))
            _logger.LogError("[Drive] Drive no devolvió la URL de la sesión de subida.");
        return uri;
    }

    /// <summary>Envía el fichero por trozos. Ante un corte, pregunta a Drive hasta dónde llegó y sigue desde ahí.</summary>
    private async Task<string?> UploadChunksAsync(Recording recording, string sessionUri, long total, CancellationToken ct)
    {
        await using var stream = new FileStream(recording.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262_144, useAsync: true);
        var buffer = new byte[ChunkSize];
        long offset = 0;
        var failures = 0;

        while (offset < total)
        {
            ct.ThrowIfCancellationRequested();

            var len = (int)Math.Min(ChunkSize, total - offset);
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, len), ct);

            ChunkResult result;
            try
            {
                result = await SendChunkAsync(sessionUri, buffer, len, offset, total, ct);
            }
            catch (HttpRequestException ex) { result = ChunkResult.Retry($"red: {ex.Message}"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { result = ChunkResult.Retry("timeout"); }

            if (result.FileId is not null)
                return result.FileId;

            if (result.Fatal)
            {
                _logger.LogError("[Drive] Subida abortada en {Offset}/{Total}: {Reason}", offset, total, result.Reason);
                return null;
            }

            if (result.Received is { } received && received > offset)
            {
                offset = received;
                failures = 0;
                _logger.LogDebug("[Drive] '{File}': {Sent:F0}/{Total:F0} MB", recording.FileName, offset / 1_048_576.0, total / 1_048_576.0);
                continue;
            }
            // Sin avance (Drive no aceptó bytes nuevos) se trata como fallo transitorio, con tope de reintentos.

            // Error transitorio: espera, consulta hasta dónde llegó Drive y reintenta desde ahí.
            if (++failures >= MaxChunkAttempts)
            {
                _logger.LogError("[Drive] Demasiados fallos seguidos en {Offset}/{Total}: {Reason}", offset, total, FailureReason(result));
                return null;
            }

            _logger.LogWarning("[Drive] Fallo transitorio ({Reason}); reintento {N}/{Max}…", FailureReason(result), failures, MaxChunkAttempts);
            await Task.Delay(RetryDelay(failures), ct);

            var status = await QueryStatusAsync(sessionUri, total, ct);
            if (status.FileId is not null) return status.FileId;
            if (status.Fatal) { _logger.LogError("[Drive] La sesión de subida ya no es válida: {Reason}", status.Reason); return null; }
            if (status.Received is { } resumeAt) offset = resumeAt;
        }

        _logger.LogError("[Drive] La subida terminó sin confirmación de Drive para '{File}'.", recording.FileName);
        return null;
    }

    private static string FailureReason(ChunkResult r) => string.IsNullOrEmpty(r.Reason) ? "sin progreso" : r.Reason;

    private async Task<ChunkResult> SendChunkAsync(string sessionUri, byte[] buffer, int len, long offset, long total, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, sessionUri);
        req.Content = new ByteArrayContent(buffer, 0, len);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        req.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + len - 1, total);

        using var res = await _http.SendAsync(req, ct);
        return await InterpretAsync(res, assumeReceived: offset + len, ct);
    }

    private async Task<ChunkResult> QueryStatusAsync(string sessionUri, long total, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, sessionUri);
            req.Content = new ByteArrayContent(Array.Empty<byte>());
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(total); // "bytes */total"
            using var res = await _http.SendAsync(req, ct);
            return await InterpretAsync(res, assumeReceived: 0, ct);
        }
        catch (HttpRequestException ex)
        {
            return ChunkResult.Retry($"red: {ex.Message}");
        }
    }

    private static async Task<ChunkResult> InterpretAsync(HttpResponseMessage res, long assumeReceived, CancellationToken ct)
    {
        if (res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();
            return ChunkResult.Done(id!);
        }

        if ((int)res.StatusCode == 308) // Resume Incomplete
        {
            // "Range: bytes=0-N" = Drive tiene N+1 bytes. Sin cabecera: no tiene nada (consulta de estado)
            // o, tras enviar un trozo, se da por recibido completo.
            if (TryGetReceived(res, out var received))
                return ChunkResult.Progress(received);
            return ChunkResult.Progress(assumeReceived);
        }

        var reason = $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}";
        // 5xx, 429 y 408 son transitorios; el resto (400/401/403/404…) no tiene arreglo reintentando.
        return res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)res.StatusCode >= 500
            ? ChunkResult.Retry(reason)
            : ChunkResult.Abort(reason);
    }

    private static bool TryGetReceived(HttpResponseMessage res, out long received)
    {
        received = 0;
        if (!res.Headers.TryGetValues("Range", out var values)) return false;
        var raw = values.FirstOrDefault(); // "bytes=0-12345"
        var dash = raw?.LastIndexOf('-') ?? -1;
        if (dash < 0 || !long.TryParse(raw![(dash + 1)..], out var last)) return false;
        received = last + 1;
        return true;
    }

    // ── Utilidades ───────────────────────────────────────────────────────────

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private readonly record struct ChunkResult(string? FileId, long? Received, bool Fatal, string Reason)
    {
        public static ChunkResult Done(string id) => new(id, null, false, "");
        public static ChunkResult Progress(long received) => new(null, received, false, "");
        public static ChunkResult Retry(string reason) => new(null, null, false, reason);
        public static ChunkResult Abort(string reason) => new(null, null, true, reason);
    }
}
