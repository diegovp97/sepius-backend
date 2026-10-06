<#
.SYNOPSIS
  Obtiene el refresh token de Google Drive (scope drive.file) para el pipeline de Sepius.

.DESCRIPTION
  Abre el navegador para que autorices la app, recibe el código en http://127.0.0.1:<puerto>/
  y lo cambia por un refresh token. El token se copia al portapapeles (no se imprime ni se guarda en
  ningún fichero; con -ShowToken se muestra). Pégalo en el .env de la VPS como GOOGLE_DRIVE_REFRESH_TOKEN.

  Requisitos en Google Cloud Console (el mismo proyecto que usa YouTube):
    1. APIs y servicios > Biblioteca > "Google Drive API" > Habilitar.
    2. Pantalla de consentimiento OAuth > añadir el scope ".../auth/drive.file"
       y poner la app en "En producción" (si se queda en "Pruebas" el token caduca a los 7 días).
    3. El cliente OAuth debe ser de tipo "Aplicación de escritorio". Si es "Aplicación web",
       añade http://127.0.0.1:8765/ como URI de redirección autorizado.

.EXAMPLE
  .\scripts\get-google-drive-token.ps1 -ClientJson "$env:USERPROFILE\Downloads\client_secret_*.json"
#>
param(
    [string]$ClientJson,
    [string]$ClientId,
    [string]$ClientSecret,
    [int]$Port = 8765,
    [switch]$ShowToken
)

$ErrorActionPreference = 'Stop'

# Si pasas el JSON que descargas de Google Cloud (client_secret_*.json), se leen de ahí el ID y el secreto.
if ($ClientJson) {
    $file = Get-ChildItem -Path $ClientJson -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $json = Get-Content -Raw -Path $file.FullName | ConvertFrom-Json
    $cfg = if ($json.installed) { $json.installed } elseif ($json.web) { $json.web } else { throw 'El JSON no tiene la sección "installed" ni "web".' }
    $ClientId = $cfg.client_id
    $ClientSecret = $cfg.client_secret
    Write-Host "Credenciales leídas de $($file.Name)" -ForegroundColor DarkGray
}

if (-not $ClientId) { $ClientId = Read-Host 'Client ID (el de tu proyecto de Google Cloud)' }
if (-not $ClientSecret) {
    $secure = Read-Host 'Client Secret' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $ClientSecret = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$redirect = "http://127.0.0.1:$Port/"
$scope = 'https://www.googleapis.com/auth/drive.file'
$state = [guid]::NewGuid().ToString('N')

$authUrl = 'https://accounts.google.com/o/oauth2/v2/auth' +
    '?client_id=' + [uri]::EscapeDataString($ClientId) +
    '&redirect_uri=' + [uri]::EscapeDataString($redirect) +
    '&response_type=code' +
    '&scope=' + [uri]::EscapeDataString($scope) +
    '&access_type=offline' +
    '&prompt=consent' +
    '&state=' + $state

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($redirect)
$listener.Start()

Write-Host ''
Write-Host 'Se abre el navegador. Inicia sesión con la cuenta de Google donde quieres guardar las copias.' -ForegroundColor Yellow
Write-Host "(Si no se abre, copia esta URL):`n$authUrl`n"
Start-Process $authUrl

try {
    $pending = $listener.GetContextAsync()
    if (-not $pending.Wait(300000)) { throw 'Tiempo agotado (5 min) esperando la autorización.' }
    $ctx = $pending.Result

    $query = $ctx.Request.QueryString
    $html = '<html><body style="font-family:sans-serif;background:#111;color:#eee;padding:40px">' +
        '<h2>Listo. Puedes cerrar esta pestaña y volver a la consola.</h2></body></html>'
    $bytes = [Text.Encoding]::UTF8.GetBytes($html)
    $ctx.Response.ContentType = 'text/html; charset=utf-8'
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}
finally {
    $listener.Stop()
}

if ($query['error']) { throw "Google devolvió un error: $($query['error'])" }
if ($query['state'] -ne $state) { throw 'El parámetro state no coincide; se aborta por seguridad.' }
$code = $query['code']
if (-not $code) { throw 'No llegó ningún código de autorización.' }

$tokens = Invoke-RestMethod -Method Post -Uri 'https://oauth2.googleapis.com/token' -Body @{
    code          = $code
    client_id     = $ClientId
    client_secret = $ClientSecret
    redirect_uri  = $redirect
    grant_type    = 'authorization_code'
}

if (-not $tokens.refresh_token) {
    throw ('Google no devolvió refresh_token. Revoca el acceso previo de la app en ' +
           'https://myaccount.google.com/permissions y vuelve a ejecutar el script.')
}

Write-Host ''
Write-Host "Scope concedido: $($tokens.scope)" -ForegroundColor Green
Write-Host ''
if ($ShowToken) {
    Write-Host 'REFRESH TOKEN (cópialo ahora; no se guarda en ningún fichero):' -ForegroundColor Cyan
    Write-Host $tokens.refresh_token
}
else {
    # Por defecto NO se imprime: así no acaba en capturas, logs ni chats. Está en el portapapeles.
    Set-Clipboard -Value $tokens.refresh_token
    Write-Host 'El REFRESH TOKEN está copiado en el portapapeles (no se muestra ni se guarda en ningún fichero).' -ForegroundColor Cyan
    Write-Host 'Pégalo directamente donde lo necesites (Ctrl+V). Usa -ShowToken solo si quieres verlo en pantalla.' -ForegroundColor DarkGray
}
Write-Host ''
Write-Host 'Siguiente paso: añade estas líneas al .env de la VPS (/opt/sepius/sepius-backend/.env):' -ForegroundColor Yellow
Write-Host '  GOOGLE_DRIVE_ENABLED=true'
Write-Host '  GOOGLE_DRIVE_REFRESH_TOKEN=<el token de arriba>'
Write-Host "  GOOGLE_DRIVE_CLIENT_ID=$ClientId"
Write-Host '  GOOGLE_DRIVE_CLIENT_SECRET=<el client_secret de tu JSON>'
Write-Host ''
Write-Host 'El refresh token solo funciona con el cliente OAuth que lo emitió, así que el CLIENT_ID y el' -ForegroundColor DarkGray
Write-Host 'CLIENT_SECRET deben ser los de este mismo JSON (no los de YouTube si son de otro proyecto).' -ForegroundColor DarkGray
