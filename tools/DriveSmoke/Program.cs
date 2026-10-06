// Prueba de humo contra Google Drive REAL: sube un fichero de prueba con el mismo código que usa
// el pipeline, comprueba que es idempotente y lo borra. Las credenciales salen de variables de
// entorno de TU sesión; nunca se escriben en disco ni se imprimen.
//
//   $env:GOOGLE_DRIVE_REFRESH_TOKEN = '...'
//   $env:GOOGLE_DRIVE_CLIENT_ID     = '...'   # opcional: si falta se usa YOUTUBE_CLIENT_ID
//   $env:GOOGLE_DRIVE_CLIENT_SECRET = '...'   # opcional: si falta se usa YOUTUBE_CLIENT_SECRET
//   dotnet run --project tools/DriveSmoke -- --size-mb 40        (3 trozos de 16 MB)
//   dotnet run --project tools/DriveSmoke -- --keep              (no borra el fichero al terminar)

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sepius.Domain.Entities;
using Sepius.Infrastructure.Drive;
using Sepius.Infrastructure.YouTube;

static string Env(string name, string fallback = "") =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var sizeMb = args.Contains("--size-mb") ? int.Parse(args[Array.IndexOf(args, "--size-mb") + 1]) : 40;
var keep = args.Contains("--keep");

var options = new GoogleDriveOptions
{
    Enabled = true,
    ClientId = Env("GOOGLE_DRIVE_CLIENT_ID", Env("YOUTUBE_CLIENT_ID")),
    ClientSecret = Env("GOOGLE_DRIVE_CLIENT_SECRET", Env("YOUTUBE_CLIENT_SECRET")),
    RefreshToken = Env("GOOGLE_DRIVE_REFRESH_TOKEN"),
    RootFolderName = Env("GOOGLE_DRIVE_FOLDER", "Sepius"),
};

if (options.RefreshToken.Length == 0 || options.ClientId.Length == 0 || options.ClientSecret.Length == 0)
{
    Console.Error.WriteLine("Faltan variables: GOOGLE_DRIVE_REFRESH_TOKEN y el Client ID/Secret " +
                            "(GOOGLE_DRIVE_CLIENT_ID/SECRET o YOUTUBE_CLIENT_ID/SECRET).");
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromHours(1) };
var service = new GoogleDriveService(
    Options.Create(options),
    Options.Create(new YouTubeOptions()),
    new ConsoleLogger<GoogleDriveService>(),
    http);

// Fichero de prueba (disperso: ocupa poco en disco, pero se envían los bytes completos).
var name = $"smoke-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.mp4";
var path = Path.Combine(Path.GetTempPath(), name);
await using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(sizeMb * 1024L * 1024L);

var recording = Recording.Create("smoke-test", path);
recording.FileSizeBytes = new FileInfo(path).Length;

Console.WriteLine($"\n== 1) Subida de {sizeMb} MB → Drive/{options.RootFolderName}/smoke-test/{name}");
var sw = Stopwatch.StartNew();
var id = await service.UploadAsync(recording);
sw.Stop();
if (id is null) { Console.Error.WriteLine("FALLÓ la subida (mira los mensajes [Drive] de arriba)."); File.Delete(path); return 1; }
Console.WriteLine($"   OK id={id} en {sw.Elapsed.TotalSeconds:F1}s ({sizeMb / sw.Elapsed.TotalSeconds:F1} MB/s)");

Console.WriteLine("\n== 2) Idempotencia: repetir la subida no debe duplicar el fichero");
sw.Restart();
var id2 = await service.UploadAsync(recording);
Console.WriteLine($"   {(id2 == id ? "OK mismo id" : $"FALLO: id distinto ({id2})")} en {sw.Elapsed.TotalSeconds:F1}s");

var failed = id2 != id;

if (!keep)
{
    Console.WriteLine("\n== 3) Limpieza: borrar el fichero de prueba de Drive");
    var form = new Dictionary<string, string>
    {
        ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret,
        ["refresh_token"] = options.RefreshToken, ["grant_type"] = "refresh_token"
    };
    var tokenRes = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
    var token = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString();
    using var del = new HttpRequestMessage(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{id}");
    del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var delRes = await http.SendAsync(del);
    Console.WriteLine($"   {(delRes.IsSuccessStatusCode ? "OK borrado" : $"No se pudo borrar ({(int)delRes.StatusCode})")}");
}
else
{
    Console.WriteLine($"\n(--keep) El fichero queda en Drive: https://drive.google.com/file/d/{id}/view");
}

File.Delete(path);
Console.WriteLine(failed ? "\nRESULTADO: CON ERRORES" : "\nRESULTADO: TODO OK");
return failed ? 1 : 0;

/// <summary>Logger mínimo a consola (evita añadir paquetes solo para esta herramienta).</summary>
sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.WriteLine($"   [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : " :: " + exception.Message)}");
    }
}
