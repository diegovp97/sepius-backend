using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Application.Interfaces;
using Sepius.Domain.Entities;

namespace Sepius.Infrastructure.Streamlink;

/// <summary>
/// Gestiona el ciclo de vida completo de los procesos de Streamlink.
///
/// PROBLEMA CENTRAL — PROCESOS HUÉRFANOS:
/// Cuando tu app .NET se cierra y ha lanzado procesos hijos con
/// UseShellExecute=false, esos procesos pueden quedar vivos en el SO
/// (especialmente en Linux/Docker) consumiendo recursos indefinidamente.
///
/// SOLUCIÓN IMPLEMENTADA EN ESTA CLASE:
/// 1. ConcurrentDictionary para rastrear todos los procesos activos.
/// 2. process.Kill(entireProcessTree: true) — mata el proceso Y todos
///    sus hijos (ej: ffmpeg invocado internamente por streamlink).
/// 3. IDisposable — cuando el contenedor DI hace Dispose al apagarse,
///    se matan TODOS los procesos activos pendientes.
/// 4. process.EnableRaisingEvents = true + evento Exited — limpieza
///    automática cuando streamlink termina por sí solo.
///
/// PARALELO NODE.JS: Es como child_process.spawn() con un Map de procesos
/// activos y process.on('exit', cleanup) para los hijos.
/// </summary>
public sealed class StreamlinkService : IStreamlinkService, IDisposable
{
    // ConcurrentDictionary = Map thread-safe.
    // Key: channelName en minúsculas. Value: tupla (Process, Recording).
    private readonly ConcurrentDictionary<string, (Process Process, Recording Recording)> _active = new();

    private readonly List<Recording> _completed = [];
    private readonly object _completedLock = new(); // Mutex para la lista de completados

    private readonly StreamlinkOptions _options;
    private readonly ILogger<StreamlinkService> _logger;
    private bool _disposed;

    // Regex para validar el nombre de canal (solo alfanumérico + guión bajo, 1-25 chars)
    // Se compila una vez como static readonly para rendimiento óptimo
    private static readonly Regex ValidChannelName = new(@"^[a-z0-9_]{1,25}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Parsea "kick:elttblue" -> ("kick", "elttblue")
    //         "twitch:elttblue" -> ("twitch", "elttblue")
    //         "elttblue" -> ("twitch", "elttblue")  (retrocompatibilidad)
    private static (string Platform, string Channel) ParseChannelInput(string input)
    {
        var colonIdx = input.IndexOf(':');
        if (colonIdx > 0)
        {
            var platform = input[..colonIdx];
            var channel  = input[(colonIdx + 1)..].TrimStart('/');
            return (platform, channel);
        }
        return ("twitch", input);
    }

    private static string BuildStreamUrl(string platform, string channel) => platform switch
    {
        "kick"   => $"https://kick.com/{channel}",
        "twitch" => $"https://www.twitch.tv/{channel}",
        _        => $"https://www.twitch.tv/{channel}"
    };

    public StreamlinkService(IOptions<StreamlinkOptions> options, ILogger<StreamlinkService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsRecording(string channelName)
        => _active.ContainsKey(channelName.ToLowerInvariant());

    /// <summary>
    /// Lanza un proceso de Streamlink de forma NO bloqueante.
    /// El método retorna inmediatamente; el proceso corre en paralelo en el SO.
    /// </summary>
    public Task StartRecordingAsync(string channelName, CancellationToken ct = default)
    {
        channelName = channelName.ToLowerInvariant().Trim();

        var (platform, channel) = ParseChannelInput(channelName);

        // SEGURIDAD: Validar solo el nombre de canal (sin prefijo de plataforma)
        // para prevenir inyección de comandos (OS Command Injection — OWASP A03)
        if (!ValidChannelName.IsMatch(channel))
            throw new ArgumentException($"Nombre de canal inválido: '{channelName}'", nameof(channelName));

        if (_active.ContainsKey(channelName))
        {
            _logger.LogWarning("Ya existe una grabación activa para '{Channel}'. Se ignora.", channelName);
            return Task.CompletedTask;
        }

        // Crear directorio de salida: /recordings/{platform}/{channel}/
        var outputDir = Path.Combine(_options.OutputPath, platform, channel);
        Directory.CreateDirectory(outputDir);

        var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.mp4";
        var outputPath = Path.Combine(outputDir, fileName);

        // ProcessStartInfo = la configuración del proceso, como los options de spawn() en Node
        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            Arguments = BuildArguments(platform, channel, outputPath),
            UseShellExecute = false,       // CRÍTICO: false para controlar el proceso
            RedirectStandardOutput = true, // Capturar stdout para logging
            RedirectStandardError = true,  // Streamlink escribe su progreso en stderr
            CreateNoWindow = true          // Sin ventana de consola
        };

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true // CRÍTICO: necesario para que se dispare el evento Exited
        };

        var recording = Recording.Create(channelName, outputPath);

        // ── SUSCRIPCIÓN A EVENTOS (antes de Start) ──────────────────────────
        // Los eventos se procesan en hilos del ThreadPool, no en el hilo principal.

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[{Channel}] {Line}", channelName, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            // Streamlink usa stderr para mensajes informativos (progreso, bitrate, etc.)
            if (e.Data is not null)
                _logger.LogInformation("[{Channel}] {Line}", channelName, e.Data);
        };

        process.Exited += (_, _) => OnProcessExited(channelName, process);

        // ── INICIO DEL PROCESO ───────────────────────────────────────────────
        try
        {
            process.Start();

            // BeginOutputReadLine/BeginErrorReadLine inician la lectura asíncrona
            // de los streams. Deben llamarse DESPUÉS de Start().
            // Si no los llamas, el proceso puede bloquearse al llenar el buffer de pipe.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _active[channelName] = (process, recording);

            _logger.LogInformation(
                "Grabación iniciada. Canal: '{Channel}' | PID: {Pid} | Archivo: {File}",
                channelName, process.Id, outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al iniciar Streamlink para '{Channel}'", channelName);
            process.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopRecordingAsync(string channelName)
    {
        channelName = channelName.ToLowerInvariant();

        if (!_active.TryRemove(channelName, out var entry))
        {
            _logger.LogWarning("No hay grabación activa para '{Channel}'", channelName);
            return Task.CompletedTask;
        }

        entry.Recording.EndedAt = DateTime.UtcNow;
        entry.Recording.Status = RecordingStatus.Completed;
        UpdateFileSize(entry.Recording);

        lock (_completedLock)
            _completed.Add(entry.Recording);

        KillProcessSafely(entry.Process, channelName);
        return Task.CompletedTask;
    }

    public IReadOnlyList<Recording> GetActiveRecordings()
        => _active.Values.Select(v => v.Recording).ToList().AsReadOnly();

    public IReadOnlyList<Recording> GetCompletedRecordings()
    {
        lock (_completedLock)
            return _completed.ToList().AsReadOnly();
    }

    // ── MÉTODOS PRIVADOS ─────────────────────────────────────────────────────

    private void OnProcessExited(string channelName, Process process)
    {
        // Leer el exit code ANTES de hacer Dispose del proceso
        var exitCode = -1;
        try { exitCode = process.ExitCode; }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo leer ExitCode de '{Channel}'", channelName); }

        _logger.LogInformation(
            "Streamlink para '{Channel}' terminó. ExitCode: {Code}", channelName, exitCode);

        // TryRemove es atómico: si StopRecordingAsync ya lo quitó, retorna false
        // y no hacemos nada (evita doble-dispose del proceso)
        if (_active.TryRemove(channelName, out var entry))
        {
            entry.Recording.EndedAt = DateTime.UtcNow;
            entry.Recording.Status = exitCode == 0
                ? RecordingStatus.Completed
                : RecordingStatus.Failed;

            UpdateFileSize(entry.Recording);

            lock (_completedLock)
                _completed.Add(entry.Recording);
        }

        try { process.Dispose(); }
        catch { /* Ignorar errores de Dispose */ }
    }

    private void KillProcessSafely(Process process, string channelName)
    {
        try
        {
            if (!process.HasExited)
            {
                _logger.LogInformation(
                    "Terminando proceso de '{Channel}' (PID: {Pid})...", channelName, process.Id);

                // entireProcessTree: true garantiza que ffmpeg (hijo de streamlink)
                // también muera. Sin esto, ffmpeg puede quedar huérfano grabando.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // El proceso ya había terminado, es OK
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al matar proceso de '{Channel}'", channelName);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void UpdateFileSize(Recording recording)
    {
        try
        {
            if (File.Exists(recording.FilePath))
                recording.FileSizeBytes = new FileInfo(recording.FilePath).Length;
        }
        catch { /* No crítico si falla */ }
    }

    private string BuildArguments(string platform, string channel, string outputPath)
    {
        var url = BuildStreamUrl(platform, channel);
        // Formato: streamlink {url} {quality} --output "{path}" {additionalArgs}
        return $"{url} {_options.Quality} " +
               $"--output \"{outputPath}\" " +
               $"{_options.AdditionalArgs}";
    }

    // ── DISPOSE — LIMPIEZA AL APAGAR ────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var count = _active.Count;
        if (count > 0)
        {
            _logger.LogWarning(
                "StreamlinkService dispose: matando {Count} grabaciones activas...", count);

            foreach (var (channelName, entry) in _active)
                KillProcessSafely(entry.Process, channelName);

            _active.Clear();
        }
    }
}
